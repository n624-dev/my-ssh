namespace MySsh.App;

internal sealed partial class FileManagerWindow
{
    private bool _restoringState;

    internal void CaptureBrowserState()
    {
        // A failed initial listing must not erase the last usable saved selection.
        if (!_browserLoaded || _restoringState || _localRows.Count == 0 || _remoteRows.Count == 0) return;
        var snapshot = CaptureInteractionState();
        _state.LocalPath = snapshot.LocalPath;
        _state.RemotePath = snapshot.RemotePath;
        _state.LocalSelection = snapshot.Local.Selected;
        _state.RemoteSelection = snapshot.Remote.Selected;
        _state.LocalMarked = snapshot.Local.Marked.ToList();
        _state.RemoteMarked = snapshot.Remote.Marked.ToList();
        _state.LocalFilter = snapshot.LocalFilter;
        _state.RemoteFilter = snapshot.RemoteFilter;
        _state.Sort = _sortMode.ToString();
        _state.SortDescending = snapshot.Descending;
        _state.ActiveLocal = snapshot.ActiveLocal;
    }

    private InteractionState ReadSavedInteractionState()
    {
        var sort = Enum.TryParse<SortMode>(_state.Sort, out var parsed) && Enum.IsDefined(parsed) ? parsed : SortMode.Name;
        return new(_currentLocal, _currentRemote, _state.LocalFilter ?? "", _state.RemoteFilter ?? "",
            (int)sort, _state.SortDescending, _state.ActiveLocal, false, null,
            new(_state.LocalSelection, (_state.LocalMarked ?? []).Where(x => !string.IsNullOrEmpty(x)).ToArray()),
            new(_state.RemoteSelection, (_state.RemoteMarked ?? []).Where(x => !string.IsNullOrEmpty(x)).ToArray()));
    }

    private void SaveBrowserState()
    {
        if (_restoringState) return;
        CaptureBrowserState();
        _settings.SaveState();
    }
}
