using System.Collections;
using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

namespace MySsh.App;

internal sealed partial class FileManagerWindow
{
    private void OpenSelected(bool localSide)
    {
        _activeLocal = localSide;
        var list = localSide ? _localList : _remoteList;
        var rows = localSide ? _localRows : _remoteRows;
        if (list.SelectedItem < 0 || list.SelectedItem >= rows.Count) return;

        var row = rows[list.SelectedItem];
        var fs = localSide ? _local : _remote;
        var current = localSide ? _currentLocal : _currentRemote;

        try
        {
            if (row.IsParent)
            {
                SetCurrent(localSide, fs.Parent(current));
                ReloadPane(localSide);
                SaveBrowserState();
                return;
            }

            if (row.Entry?.Kind == EntryKind.Directory)
            {
                SetCurrent(localSide, row.Entry.Path);
                ReloadPane(localSide);
                SaveBrowserState();
                return;
            }

            if (row.Entry is not null) Preview(row.Entry, fs);
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery(70, 8, "Open", ex.Message, "OK");
        }
    }

    private void QueueSelected(bool move)
    {
        var queued = 0;
        try
        {
            var source = _activeLocal ? _local : _remote;
            var destination = _activeLocal ? _remote : _local;
            var destinationDirectory = _activeLocal ? _currentRemote : _currentLocal;
            var rows = SelectedRows(_activeLocal);
            if (rows.Count == 0) return;

            // Resolve the entire selection first. Cancelling a name prompt must
            // not leave earlier items from this selection running in the queue.
            var plan = TransferSelectionPlanner.Prepare(destination, destinationDirectory,
                rows, ResolveInvalidTransferName);
            if (plan.Cancelled)
            {
                _message.Text = "Selection cancelled; no new transfers were queued.";
                return;
            }
            foreach (var item in plan.Entries)
            {
                _transfers.Enqueue(source, item.Source.Path, destination, item.Destination,
                    new TransferOptions(Move: move, PreserveMetadata: _settings.Config.PreserveMetadata));
                queued++;
            }
            _message.Text = $"Queued {queued} {(move ? "move" : "copy")} operation(s); skipped {plan.Skipped}.";
            RefreshQueue();
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery(75, 10, "Transfer selection",
                $"{queued} transfer(s) were queued.\n" + Safe(ex.Message), "OK");
        }
    }

    private static InvalidNameDecision ResolveInvalidTransferName(string name, string error)
    {
        var selected = MessageBox.Query(75, 11, "Invalid destination name",
            "Name: " + Safe(name) + "\n" + Safe(error), "Rename", "Skip", "Cancel selection");
        if (selected == 1) return new(InvalidNameAction.Skip);
        if (selected != 0) return new(InvalidNameAction.Cancel);
        var replacement = Prompt("Destination name", "New name", name);
        return replacement is null ? new(InvalidNameAction.Cancel) : new(InvalidNameAction.Rename, replacement);
    }

    private List<FileEntry> SelectedRows(bool localSide)
    {
        var list = localSide ? _localList : _remoteList;
        var rows = localSide ? _localRows : _remoteRows;
        var selected = new List<FileEntry>();

        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Entry is not null && list.Source is not null && list.Source.IsMarked(i))
                selected.Add(rows[i].Entry!);
        }

        if (selected.Count == 0 && list.SelectedItem >= 0 && list.SelectedItem < rows.Count &&
            rows[list.SelectedItem].Entry is { } current)
            selected.Add(current);

        return selected;
    }

    private void MarkAll(bool marked)
    {
        var list = _activeLocal ? _localList : _remoteList;
        var rows = _activeLocal ? _localRows : _remoteRows;
        if (list.Source is null) return;

        for (var i = 1; i < rows.Count; i++) list.Source.SetMark(i, marked);
        list.SetNeedsDisplay();
    }

    private void RenameSelected()
    {
        var selected = SelectedRows(_activeLocal);
        if (selected.Count != 1)
        {
            MessageBox.Query(55, 7, "Rename", "Select exactly one file or directory.", "OK");
            return;
        }

        var entry = selected[0];
        var newName = Prompt("Rename", "New name", entry.Name);
        if (newName is null || newName == entry.Name) return;

        try
        {
            var fs = _activeLocal ? _local : _remote;
            var destination = fs.Join(fs.Parent(entry.Path), newName);
            fs.RenameAsync(entry.Path, destination, replace: false, CancellationToken.None)
                .GetAwaiter().GetResult();
            ReloadPane(_activeLocal);
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery(70, 8, "Rename", ex.Message, "OK");
        }
    }

    private void CreateDirectory()
    {
        var name = Prompt("New Directory", "Name", "");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var fs = _activeLocal ? _local : _remote;
            var current = _activeLocal ? _currentLocal : _currentRemote;
            fs.CreateDirectoryAsync(fs.Join(current, name), CancellationToken.None).GetAwaiter().GetResult();
            ReloadPane(_activeLocal);
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery(70, 8, "New Directory", ex.Message, "OK");
        }
    }

    private void DeleteSelected()
    {
        var selected = SelectedRows(_activeLocal);
        if (selected.Count == 0) return;

        var fs = _activeLocal ? _local : _remote;
        var location = _activeLocal ? "LOCAL" : _connection.Key;
        if (MessageBox.Query(70, 9, "Delete",
                $"Permanently delete {selected.Count} item(s) from {location}?\nThis does not use a recycle bin.",
                "Delete", "Cancel") != 0)
            return;

        try
        {
            foreach (var entry in selected) DeleteTree(fs, entry.Path);
            ReloadPane(_activeLocal);
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery(70, 8, "Delete", ex.Message, "OK");
        }
    }

    private static void DeleteTree(IFileSystem fs, string path)
    {
        var entry = fs.StatAsync(path, CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new FileNotFoundException(path);
        PathSafety.ProtectRoot(path, fs);

        if (entry.Kind == EntryKind.Directory)
        {
            foreach (var child in fs.ListAsync(path, CancellationToken.None).GetAwaiter().GetResult())
                DeleteTree(fs, child.Path);
            fs.DeleteAsync(path, directory: true, CancellationToken.None).GetAwaiter().GetResult();
        }
        else
        {
            fs.DeleteAsync(path, directory: false, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private void ShowActions()
    {
        if (_queueList.HasFocus)
        {
            QueueActions();
            return;
        }

        var actions = new List<(string Name, Action Run)>
        {
            ("Go to path", GoToPath),
            ("Filter by name", SetFilter),
            ("Sort", ChooseSort),
            ("Select all", () => MarkAll(true)),
            ("Clear selection", () => MarkAll(false)),
            ("Refresh", () => ReloadPane(_activeLocal)),
            (_state.ShowHidden ? "Hide hidden files" : "Show hidden files", ToggleHidden),
            ("Preview / properties", PreviewSelected),
            ("Edit", EditSelected),
            ("Permissions", ChangePermissions),
            ("Save bookmark", AddBookmark),
            ("Open bookmark", OpenBookmark)
        };

        if (_activeLocal) actions.Add(("Choose local root", ChooseLocalRoot));
        else actions.Add(("Reconnect remote", RequestRemoteReconnect));

        var selected = Choose("Actions", actions.Select(x => (object)x.Name).ToList());
        if (selected >= 0) actions[selected].Run();
    }

    private void ToggleHidden()
    {
        _state.ShowHidden = !_state.ShowHidden;
        ReloadPane(localSide: true);
        ReloadPane(localSide: false);
        SaveBrowserState();
    }

    private void PreviewSelected()
    {
        var selected = SelectedRows(_activeLocal).FirstOrDefault();
        if (selected is not null) Preview(selected, _activeLocal ? _local : _remote);
    }

    private void ChooseSort()
    {
        var options = new List<object>
        {
            "Name ascending", "Name descending",
            "Size ascending", "Size descending",
            "Modified ascending", "Modified descending"
        };
        var selected = Choose("Sort", options);
        if (selected < 0) return;

        _sortMode = (selected / 2) switch
        {
            1 => SortMode.Size,
            2 => SortMode.Modified,
            _ => SortMode.Name
        };
        _sortDescending = selected % 2 == 1;
        ReloadPane(localSide: true);
        ReloadPane(localSide: false);
    }

    private void SetFilter()
    {
        var current = _activeLocal ? _localFilter : _remoteFilter;
        var value = Prompt("Filter", "Name contains (empty clears)", current);
        if (value is null) return;
        if (_activeLocal) _localFilter = value.Trim();
        else _remoteFilter = value.Trim();
        ReloadPane(_activeLocal);
    }

    private void GoToPath()
    {
        var current = _activeLocal ? _currentLocal : _currentRemote;
        var path = Prompt("Go to Path", "Path", current);
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var fs = _activeLocal ? _local : _remote;
            var canonical = fs.CanonicalAsync(path, CancellationToken.None).GetAwaiter().GetResult();
            var stat = fs.StatAsync(canonical, CancellationToken.None).GetAwaiter().GetResult();
            if (stat is not { Kind: EntryKind.Directory }) throw new IOException("Path is not a directory.");
            SetCurrent(_activeLocal, canonical);
            ReloadPane(_activeLocal);
            SaveBrowserState();
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery(70, 8, "Go to Path", ex.Message, "OK");
        }
    }

    private void ChooseLocalRoot()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try { if (drive.IsReady) roots.Add(drive.RootDirectory.FullName); }
                catch { }
            }
        }
        else
        {
            roots.Add("/");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home) && home != "/") roots.Add(home);
        }

        if (roots.Count == 0)
        {
            MessageBox.Query(55, 7, "Local Roots", "No accessible local roots were found.", "OK");
            return;
        }

        var selected = Choose("Local Roots", roots.Cast<object>().ToList());
        if (selected < 0) return;
        _currentLocal = roots[selected];
        ReloadPane(localSide: true);
        SaveBrowserState();
    }

    private void ChangePermissions()
    {
        var selected = SelectedRows(_activeLocal);
        if (selected.Count != 1)
        {
            MessageBox.Query(60, 7, "Permissions", "Select exactly one entry.", "OK");
            return;
        }

        var entry = selected[0];
        if (entry.Mode is null)
        {
            MessageBox.Query(70, 8, "Permissions",
                "POSIX permission bits are not available for this entry on this filesystem.", "OK");
            return;
        }

        var currentMode = Convert.ToString(entry.Mode.Value & 0x1FF, 8).PadLeft(3, '0');
        var value = Prompt("Permissions", "Octal mode (000-777)", currentMode);
        if (value is null) return;

        try
        {
            if (value.Length != 3 || value.Any(c => c is < '0' or > '7'))
                throw new IOException("Permission mode must be exactly three octal digits from 000 to 777.");
            var mode = Convert.ToUInt32(value, 8);
            var fs = _activeLocal ? _local : _remote;
            fs.SetMetadataAsync(entry.Path, entry.Modified, mode, CancellationToken.None)
                .GetAwaiter().GetResult();
            ReloadPane(_activeLocal);
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery(70, 8, "Permissions", ex.Message, "OK");
        }
    }

    private void AddBookmark()
    {
        var name = Prompt("Bookmark", "Name", "Bookmark");
        if (string.IsNullOrWhiteSpace(name)) return;
        _state.Bookmarks.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _state.Bookmarks.Add(new Bookmark(name, _currentLocal, _currentRemote));
        SaveBrowserState();
    }

    private void OpenBookmark()
    {
        if (_state.Bookmarks.Count == 0)
        {
            MessageBox.Query(55, 7, "Bookmarks", "No bookmarks are saved.", "OK");
            return;
        }

        var items = _state.Bookmarks
            .Select(x => (object)$"{x.Name}  |  {Safe(x.LocalPath)}  |  {Safe(x.RemotePath)}")
            .ToList();
        var selected = Choose("Bookmarks", items);
        if (selected < 0) return;

        _currentLocal = _state.Bookmarks[selected].LocalPath;
        _currentRemote = _state.Bookmarks[selected].RemotePath;
        ReloadAll();
    }
}
