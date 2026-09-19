namespace MySsh.App;

internal sealed partial class FileManagerWindow
{
    private bool _browserLoaded;
    private InteractionState? _pendingInteractionState;

    // Snapshot saved paths and marks before any listing can update BrowserState.
    // A live terminal handoff takes precedence over the on-disk startup state.
    internal void InitializeBrowser()
    {
        if (_browserLoaded) return;
        var snapshot = _pendingInteractionState ?? ReadSavedInteractionState();
        _pendingInteractionState = null;
        _browserLoaded = true;
        RestoreInteractionState(snapshot);
    }
}
