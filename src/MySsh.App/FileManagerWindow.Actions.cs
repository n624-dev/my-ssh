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
        catch (Exception ex) { ShowOperationError("Open", ex); }
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
            if (rows[i].Entry is not null && list.Source?.IsMarked(i) == true) selected.Add(rows[i].Entry!);
        if (selected.Count == 0 && list.SelectedItem >= 0 && list.SelectedItem < rows.Count &&
            rows[list.SelectedItem].Entry is { } current) selected.Add(current);
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
            UiFileOperation.Run("Rename", ct => fs.RenameAsync(entry.Path, destination, false, ct));
            ReloadPane(_activeLocal);
        }
        catch (Exception ex) { ShowOperationError("Rename", ex); }
    }

    private void CreateDirectory()
    {
        var name = Prompt("New Directory", "Name", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var fs = _activeLocal ? _local : _remote;
            var path = fs.Join(_activeLocal ? _currentLocal : _currentRemote, name);
            UiFileOperation.Run("New directory", ct => fs.CreateDirectoryAsync(path, ct));
            ReloadPane(_activeLocal);
        }
        catch (Exception ex) { ShowOperationError("New directory", ex); }
    }

    private void DeleteSelected()
    {
        var selected = SelectedRows(_activeLocal);
        if (selected.Count == 0) return;
        var fs = _activeLocal ? _local : _remote;
        var location = _activeLocal ? "LOCAL" : _connection.Key;
        if (MessageBox.Query(70, 9, "Delete",
                $"Permanently delete {selected.Count} item(s) from {location}?\nThis does not use a recycle bin.",
                "Delete", "Cancel") != 0) return;
        try
        {
            UiFileOperation.Run("Delete selected entries", async ct =>
            {
                foreach (var entry in selected) await DeleteTreeAsync(fs, entry.Path, ct).ConfigureAwait(false);
            });
            ReloadPane(_activeLocal);
        }
        catch (Exception ex) { ShowOperationError("Delete (completed deletions are not rolled back)", ex); }
    }

    private static async Task DeleteTreeAsync(IFileSystem fs, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entry = await fs.StatAsync(path, ct).ConfigureAwait(false) ?? throw new FileNotFoundException(path);
        PathSafety.ProtectRoot(path, fs);
        if (entry.Kind == EntryKind.Directory)
        {
            foreach (var child in await fs.ListAsync(path, ct).ConfigureAwait(false))
                await DeleteTreeAsync(fs, child.Path, ct).ConfigureAwait(false);
            await fs.DeleteAsync(path, true, ct).ConfigureAwait(false);
        }
        else await fs.DeleteAsync(path, false, ct).ConfigureAwait(false);
    }

    private static void ShowOperationError(string title, Exception error) =>
        MessageBox.ErrorQuery(Math.Min(75, Math.Max(1, Application.Driver.Cols - 2)),
            Math.Min(11, Math.Max(1, Application.Driver.Rows - 2)), title, Safe(error.Message), "OK");

    private void ShowActions()
    {
        if (_queueList.HasFocus) { QueueActions(); return; }
        var actions = new List<(string Name, Action Run)>
        {
            ("Go to path", GoToPath), ("Filter by name", SetFilter), ("Sort", ChooseSort),
            ("Select all", () => MarkAll(true)), ("Clear selection", () => MarkAll(false)),
            ("Refresh", () => ReloadPane(_activeLocal)),
            (_state.ShowHidden ? "Hide hidden files" : "Show hidden files", ToggleHidden),
            ("Preview / properties", PreviewSelected), ("Edit", EditSelected),
            ("Permissions", ChangePermissions), ("Save bookmark", AddBookmark), ("Open bookmark", OpenBookmark)
        };
        if (_activeLocal) actions.Add(("Choose local root", ChooseLocalRoot));
        else actions.Add(("Reconnect remote", RequestRemoteReconnect));
        var selected = Choose("Actions", actions.Select(x => (object)x.Name).ToList());
        if (selected < 0) return;
        try { actions[selected].Run(); }
        catch (Exception ex) { ShowOperationError(actions[selected].Name, ex); }
    }

    private void ToggleHidden()
    {
        _state.ShowHidden = !_state.ShowHidden;
        ReloadPane(true);
        ReloadPane(false);
        SaveBrowserState();
    }

    private void PreviewSelected()
    {
        var selected = SelectedRows(_activeLocal).FirstOrDefault();
        if (selected is not null) Preview(selected, _activeLocal ? _local : _remote);
    }

    private void ChooseSort()
    {
        var selected = Choose("Sort", new List<object>
        {
            "Name ascending", "Name descending", "Size ascending", "Size descending",
            "Modified ascending", "Modified descending"
        });
        if (selected < 0) return;
        _sortMode = (selected / 2) switch { 1 => SortMode.Size, 2 => SortMode.Modified, _ => SortMode.Name };
        _sortDescending = selected % 2 == 1;
        ReloadPane(true);
        ReloadPane(false);
    }

    private void SetFilter()
    {
        var value = Prompt("Filter", "Name contains (empty clears)", _activeLocal ? _localFilter : _remoteFilter);
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
            var canonical = UiFileOperation.Run("Resolve path", async ct =>
            {
                var resolved = await fs.CanonicalAsync(path, ct).ConfigureAwait(false);
                if (await fs.StatAsync(resolved, ct).ConfigureAwait(false) is not { Kind: EntryKind.Directory })
                    throw new IOException("Path is not a directory.");
                return resolved;
            });
            SetCurrent(_activeLocal, canonical);
            ReloadPane(_activeLocal);
            SaveBrowserState();
        }
        catch (Exception ex) { ShowOperationError("Go to path", ex); }
    }

    private void ChooseLocalRoot()
    {
        var roots = UiFileOperation.Run("Read local roots", _ => Task.FromResult(ReadLocalRoots()));
        if (roots.Count == 0)
        {
            MessageBox.Query(55, 7, "Local Roots", "No accessible local roots were found.", "OK");
            return;
        }
        var selected = Choose("Local Roots", roots.Cast<object>().ToList());
        if (selected < 0) return;
        _currentLocal = roots[selected];
        ReloadPane(true);
        SaveBrowserState();
    }

    private static List<string> ReadLocalRoots()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try { if (drive.IsReady) roots.Add(drive.RootDirectory.FullName); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        else
        {
            roots.Add("/");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home) && home != "/") roots.Add(home);
        }
        return roots;
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
            MessageBox.Query(70, 8, "Permissions", "POSIX permission bits are not available for this entry on this filesystem.", "OK");
            return;
        }
        var value = Prompt("Permissions", "Octal mode (000-777)", Convert.ToString(entry.Mode.Value & 0x1FF, 8).PadLeft(3, '0'));
        if (value is null) return;
        try
        {
            if (value.Length != 3 || value.Any(c => c is < '0' or > '7'))
                throw new IOException("Permission mode must be exactly three octal digits from 000 to 777.");
            var mode = Convert.ToUInt32(value, 8);
            var fs = _activeLocal ? _local : _remote;
            UiFileOperation.Run("Change permissions", ct => fs.SetMetadataAsync(entry.Path, entry.Modified, mode, ct));
            ReloadPane(_activeLocal);
        }
        catch (Exception ex) { ShowOperationError("Permissions", ex); }
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
        var items = _state.Bookmarks.Select(x => (object)$"{x.Name}  |  {Safe(x.LocalPath)}  |  {Safe(x.RemotePath)}").ToList();
        var selected = Choose("Bookmarks", items);
        if (selected < 0) return;
        _currentLocal = _state.Bookmarks[selected].LocalPath;
        _currentRemote = _state.Bookmarks[selected].RemotePath;
        ReloadAll();
    }
}
