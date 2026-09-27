using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class ConcurrentBrowserStateTests : IRegressionCase
{
    public string Name => "concurrent windows save coherent browser views without settings conflicts";

    public async Task RunAsync()
    {
        using var root = new TestDirectory();
        using var first = new SettingsStore(root.Path);
        using var second = new SettingsStore(root.Path);
        var connection = new Connection("shared.test", "user");
        var a = first.Browser(connection);
        var b = second.Browser(connection);
        // Both windows first visit an unsaved connection. Their default view
        // fields differ only in the active pane: previously activeLocal threw.
        b.ActiveLocal = false;
        first.SaveState();
        second.SaveState();
        using (var check = new SettingsStore(root.Path))
            RegressionCases.Check(!check.Browser(connection).ActiveLocal, "The latest active pane was not saved.");

        var other = first.Browser(new Connection("other.test", "user"));
        other.RemotePath = "/other";
        a.Bookmarks.Add(new("kept", "local", "/bookmark"));
        first.SaveState();
        using (var check = new SettingsStore(root.Path))
            RegressionCases.Check(!check.Browser(connection).ActiveLocal, "An unchanged view or bookmark save restored a stale pane.");

        for (var i = 0; i < 3; i++)
        {
            SetView(a, "a" + i, true);
            SetView(b, "b" + i, false);
            await Task.WhenAll(Task.Run(first.SaveState), Task.Run(second.SaveState));
            string winner;
            using (var check = new SettingsStore(root.Path))
            {
                var view = check.Browser(connection);
                winner = view.LocalPath;
                RegressionCases.Check(winner == "a" + i || winner == "b" + i, "Neither window's latest view was retained.");
                CheckView(view, winner, winner.StartsWith('a'));
                RegressionCases.Check(view.Bookmarks.Single().Name == "kept" &&
                    check.Browser(new("other.test", "user")).RemotePath == "/other", "A view save erased bookmarks or another connection.");
            }
            // Exit/refresh saves with no local changes must not steal the view
            // back from a different window. Live BrowserState references survive.
            first.SaveState();
            second.SaveState();
            using var unchanged = new SettingsStore(root.Path);
            CheckView(unchanged.Browser(connection), winner, winner.StartsWith('a'));
            RegressionCases.Check(ReferenceEquals(a, first.Browser(connection)) && ReferenceEquals(b, second.Browser(connection)),
                "Saving replaced browser objects held by the live UI.");
        }
        // A single changed view field still saves the whole coherent view,
        // rather than mixing selections from one directory with another path.
        b.ActiveLocal = true;
        second.SaveState();
        using (var check = new SettingsStore(root.Path)) CheckView(check.Browser(connection), b.LocalPath, true);

        using var third = new SettingsStore(root.Path);
        using var fourth = new SettingsStore(root.Path);
        third.Browser(connection).Bookmarks.Add(new("third", "local", "/third"));
        fourth.Browser(connection).Bookmarks.Clear();
        third.SaveState();
        var statePath = Path.Combine(root.Path, "state.json");
        var saved = await File.ReadAllTextAsync(statePath);
        await RegressionCases.ThrowsAsync<IOException>(() => Task.Run(fourth.SaveState));
        RegressionCases.Check(await File.ReadAllTextAsync(statePath) == saved, "Conflicting bookmarks overwrote saved data.");
    }

    private static void SetView(BrowserState view, string label, bool activeLocal)
    {
        view.LocalPath = label;
        view.RemotePath = "/" + label;
        view.LocalSelection = label + "/local";
        view.RemoteSelection = "/" + label + "/remote";
        view.LocalMarked = [view.LocalSelection];
        view.RemoteMarked = [view.RemoteSelection];
        view.LocalFilter = label;
        view.RemoteFilter = label;
        view.Sort = activeLocal ? "Name" : "Size";
        view.SortDescending = !activeLocal;
        view.ShowHidden = !activeLocal;
        view.ActiveLocal = activeLocal;
    }

    private static void CheckView(BrowserState view, string label, bool activeLocal)
    {
        RegressionCases.Check(view.LocalPath == label && view.RemotePath == "/" + label &&
            view.LocalSelection == label + "/local" && view.RemoteSelection == "/" + label + "/remote" &&
            view.LocalMarked.SequenceEqual(new[] { label + "/local" }) && view.RemoteMarked.SequenceEqual(new[] { "/" + label + "/remote" }) &&
            view.LocalFilter == label && view.RemoteFilter == label && view.ActiveLocal == activeLocal &&
            view.Sort == (label.StartsWith('a') ? "Name" : "Size") && view.SortDescending == label.StartsWith('b') &&
            view.ShowHidden == label.StartsWith('b'), "Saved state mixed different windows' paths, selections or view preferences.");
    }
}
