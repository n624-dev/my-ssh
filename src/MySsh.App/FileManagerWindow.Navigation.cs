using System.Collections;
using MySsh.Core;

namespace MySsh.App;

internal sealed partial class FileManagerWindow
{
    private sealed record PaneContents(string Path, IReadOnlyList<FileEntry> Entries);

    private static async Task<PaneContents> ReadPaneAsync(IFileSystem fs, string path, CancellationToken ct)
    {
        var canonical = await fs.CanonicalAsync(path, ct).ConfigureAwait(false);
        if (await fs.StatAsync(canonical, ct).ConfigureAwait(false) is not { Kind: EntryKind.Directory })
            throw new IOException("Path is not a directory.");
        var entries = await fs.ListAsync(canonical, ct).ConfigureAwait(false);
        return new(canonical, entries);
    }

    // Publish the path, displayed entries and saved destination only after every
    // operation that can fail or be cancelled has finished. Callers do not set paths.
    internal void NavigatePane(bool localSide, string path)
    {
        var fs = localSide ? _local : _remote;
        var contents = UiFileOperation.Run("Open directory", ct => ReadPaneAsync(fs, path, ct));
        ApplyPane(localSide, contents);
        SaveBrowserState();
    }

    internal void NavigateBoth(string localPath, string remotePath)
    {
        var contents = UiFileOperation.Run("Open directories", async ct => (
            Local: await ReadPaneAsync(_local, localPath, ct).ConfigureAwait(false),
            Remote: await ReadPaneAsync(_remote, remotePath, ct).ConfigureAwait(false)));
        // Bookmarks and restored sessions are one navigation transaction: a
        // failed remote listing must not partially switch the local destination.
        ApplyPane(true, contents.Local);
        ApplyPane(false, contents.Remote);
        SaveBrowserState();
    }

    private void ApplyPane(bool localSide, PaneContents contents)
    {
        var filter = localSide ? _localFilter : _remoteFilter;
        var entries = contents.Entries.Where(x => _state.ShowHidden || !x.Name.StartsWith(".", StringComparison.Ordinal))
            .Where(x => string.IsNullOrWhiteSpace(filter) || x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        var rows = new List<BrowserRow> { BrowserRow.Parent };
        rows.AddRange(SortEntries(entries).Select(x => new BrowserRow(x)));
        var label = Safe(contents.Path);
        if (!string.IsNullOrWhiteSpace(filter)) label += $"  [filter: {Safe(filter)}]";
        label += $"  [sort: {_sortMode}{(_sortDescending ? " desc" : "")}]";
        var list = localSide ? _localList : _remoteList;
        var oldRows = localSide ? _localRows : _remoteRows;
        var fs = localSide ? _local : _remote;
        var sameDirectory = contents.Path.Equals(localSide ? _currentLocal : _currentRemote, fs.PathComparison);
        var selection = sameDirectory ? CapturePane(list, oldRows) : null;
        var top = list.TopItem;
        list.SetSource((IList)rows);
        list.SelectedItem = 0;
        if (localSide)
        {
            _currentLocal = contents.Path;
            _localRows = rows;
            _localPath.Text = label;
        }
        else
        {
            _currentRemote = contents.Path;
            _remoteRows = rows;
            _remotePath.Text = label;
        }
        // Never copy marks by row index: additions and re-sorting change indices.
        // Missing or filtered-out paths are deliberately not selected.
        if (selection is not null)
        {
            RestorePane(list, rows, selection, fs.PathComparison);
            list.TopItem = Math.Clamp(top, 0, rows.Count - 1);
        }
    }
}
