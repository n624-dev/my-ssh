using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

namespace MySsh.App;

internal sealed partial class FileManagerWindow
{
    internal bool ReconnectRequested { get; private set; }
    private void RequestRemoteReconnect()
    {
        if (_remote is not SftpFileSystem)
        {
            MessageBox.Query(55, 7, "Reconnect", "This remote backend cannot reconnect.", "OK");
            return;
        }
        ReconnectRequested = true;
        Application.RequestStop();
    }

    internal sealed record PaneInteractionState(string? Selected, string[] Marked);
    internal sealed record InteractionState(string LocalPath, string RemotePath,
        string LocalFilter, string RemoteFilter, int Sort, bool Descending,
        bool ActiveLocal, bool QueueFocused, Guid? SelectedJob,
        PaneInteractionState Local, PaneInteractionState Remote);

    internal InteractionState CaptureInteractionState() => new(
        _currentLocal, _currentRemote, _localFilter, _remoteFilter, (int)_sortMode,
        _sortDescending, _activeLocal, _queueList.HasFocus,
        _queueList.SelectedItem >= 0 && _queueList.SelectedItem < _queueRows.Count
            ? _queueRows[_queueList.SelectedItem].Id : null,
        CapturePane(_localList, _localRows), CapturePane(_remoteList, _remoteRows));

    private static PaneInteractionState CapturePane(ListView list, List<BrowserRow> rows) => new(
        list.SelectedItem >= 0 && list.SelectedItem < rows.Count ? rows[list.SelectedItem].Entry?.Path : null,
        rows.Select((row, index) => (row, index))
            .Where(item => item.row.Entry is not null && list.Source?.IsMarked(item.index) == true)
            .Select(item => item.row.Entry!.Path).ToArray());

    internal void RestoreInteractionState(InteractionState snapshot)
    {
        if (!_browserLoaded) { _pendingInteractionState = snapshot; return; }
        var oldView = (_localFilter, _remoteFilter, _sortMode, _sortDescending);
        _restoringState = true;
        try
        {
            _localFilter = snapshot.LocalFilter;
            _remoteFilter = snapshot.RemoteFilter;
            _sortMode = Enum.IsDefined((SortMode)snapshot.Sort) ? (SortMode)snapshot.Sort : SortMode.Name;
            _sortDescending = snapshot.Descending;
            try { NavigateBoth(snapshot.LocalPath, snapshot.RemotePath); }
            catch (Exception ex)
            {
                (_localFilter, _remoteFilter, _sortMode, _sortDescending) = oldView;
                _message.Text = "Restore failed: " + Safe(ex.Message);
                return;
            }
            foreach (var job in _transfers.Snapshot())
                if (job.State is TransferState.Completed or TransferState.Partial) _reloadedCompleted.Add(job.Id);
            RefreshQueue();
            RestorePane(_localList, _localRows, snapshot.Local, _local.PathComparison);
            RestorePane(_remoteList, _remoteRows, snapshot.Remote, _remote.PathComparison);
            if (snapshot.SelectedJob is { } id)
            {
                var index = _queueRows.FindIndex(job => job.Id == id);
                if (index >= 0) _queueList.SelectedItem = index;
            }
            _activeLocal = snapshot.ActiveLocal;
            if (snapshot.QueueFocused) _queueList.SetFocus();
            else if (snapshot.ActiveLocal) _localList.SetFocus();
            else _remoteList.SetFocus();
        }
        finally { _restoringState = false; }
        SaveBrowserState();
    }

    private static void RestorePane(ListView list, List<BrowserRow> rows,
        PaneInteractionState snapshot, StringComparison comparison)
    {
        list.SelectedItem = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            var entry = rows[index].Entry;
            if (entry is null) continue;
            if (entry.Path.Equals(snapshot.Selected, comparison)) list.SelectedItem = index;
            list.Source?.SetMark(index, snapshot.Marked.Any(path => entry.Path.Equals(path, comparison)));
        }
        list.SetNeedsDisplay();
    }

    internal void SetConnectionMessage(string message) => _message.Text = Safe(message);
}
