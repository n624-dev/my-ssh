namespace MySsh.App;

internal sealed partial class FileManagerWindow
{
    private bool _browserLoaded;
    private InteractionState? _pendingInteractionState;

    // Terminal.Gui must have begun the parent run-state before a child modal
    // progress dialog is opened. Constructors never start filesystem I/O.
    internal void InitializeBrowser()
    {
        if (_browserLoaded) return;
        _browserLoaded = true;
        if (_pendingInteractionState is { } snapshot)
        {
            _pendingInteractionState = null;
            RestoreInteractionState(snapshot);
        }
        else ReloadAll();
    }
}
