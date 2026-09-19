using Terminal.Gui;

namespace MySsh.App;

internal sealed partial class FileManagerWindow
{
    internal sealed record FileAction(string Id, string Name, Action Run);

    internal IReadOnlyList<FileAction> BuildFileActions()
    {
        var actions = new List<FileAction>
        {
            new("copy", "Copy to opposite pane", () => QueueSelected(false)),
            new("move", "Move to opposite pane", () => QueueSelected(true)),
            new("rename", "Rename", RenameSelected),
            new("delete", "Delete (permanent, confirmation required)", DeleteSelected),
            new("mkdir", "New directory", CreateDirectory),
            new("path", "Go to path", GoToPath),
            new("filter", "Filter by name", SetFilter),
            new("sort", "Sort", ChooseSort),
            new("select-all", "Select all", () => MarkAll(true)),
            new("clear-selection", "Clear selection", () => MarkAll(false)),
            new("refresh", "Refresh", () => ReloadPane(_activeLocal)),
            new("hidden", _state.ShowHidden ? "Hide hidden files" : "Show hidden files", ToggleHidden),
            new("preview", "Preview / properties", PreviewSelected),
            new("edit", "Edit", EditSelected),
            new("permissions", "Permissions", ChangePermissions),
            new("bookmark-save", "Save bookmark", AddBookmark),
            new("bookmark-open", "Open bookmark", OpenBookmark)
        };
        if (_activeLocal) actions.Add(new("root", "Choose local root", ChooseLocalRoot));
        else actions.Add(new("reconnect", "Reconnect remote", RequestRemoteReconnect));
        return actions;
    }

    private void ShowActions()
    {
        if (_queueList.HasFocus) { QueueActions(); return; }
        var localSide = _activeLocal;
        var actions = BuildFileActions();
        var selected = Choose("Actions (Alt+A)", actions.Select(x => (object)x.Name).ToList());
        if (selected < 0) return;
        _activeLocal = localSide;
        try { actions[selected].Run(); }
        catch (Exception ex) { ShowOperationError(actions[selected].Name, ex); }
    }

    public override bool ProcessHotKey(KeyEvent keyEvent)
    {
        // A keyboard-only route that does not need function keys. Active modal
        // dialogs retain ownership of their keys and confirmations.
        if ((keyEvent.Key == (Key.A | Key.AltMask) || keyEvent.Key == ((Key)'a' | Key.AltMask)) &&
            Application.Current == Application.Top && !UiFileOperation.IsBusy)
        {
            ShowActions();
            return true;
        }
        return base.ProcessHotKey(keyEvent);
    }
}
