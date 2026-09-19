using MySsh.App;
using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

internal sealed class Issue28Tests : IRegressionCase
{
    public string Name => "#28 relative navigation resolves against the displayed pane";

    public Task RunAsync()
    {
        foreach (var input in new[] { ".", "..", "subdir/file", "link/../sibling", "a\\b" })
            RegressionCases.Check(NavigationPath.Resolve("/workspace/current", input, true) == "/workspace/current/" + input,
                "Remote input was resolved against the SSH default directory or changed before REALPATH.");
        RegressionCases.Check(NavigationPath.Resolve("/workspace", "/absolute/path", true) == "/absolute/path", "Absolute remote path changed.");
        RegressionCases.Check(NavigationPath.Resolve("/", "..", true) == "/..", "Remote root was joined incorrectly.");
        using var root = new TestDirectory();
        using var fs = new LocalFileSystem();
        using var settings = new SettingsStore(root.File("settings"));
        var current = root.File("current");
        var child = Path.Combine(current, "subdir");
        Directory.CreateDirectory(child);
        foreach (var input in new[] { ".", "..", "subdir/file" })
            RegressionCases.Check(NavigationPath.Resolve(current, input, false) == Path.GetFullPath(Path.Combine(current, input)),
                "Local input ignored the pane directory.");
        var queue = new TransferQueue(1);
        Application.Init(new FakeDriver());
        var run = Application.Begin(Application.Top);
        try
        {
            var state = new BrowserState { LocalPath = current, RemotePath = current };
            using var window = new FileManagerWindow(new Connection("path.test", "test"), fs, fs, state, settings, queue);
            Application.Top.Add(window);
            window.InitializeBrowser();
            window.NavigatePane(true, "subdir");
            RegressionCases.Check(window.CaptureInteractionState().LocalPath == child && state.LocalPath == child, "UI did not use the pane as its base.");
            window.NavigatePane(true, "..");
            RegressionCases.Check(window.CaptureInteractionState().LocalPath == current, "Parent navigation used process cwd.");
            Application.Top.Remove(window);
        }
        finally
        {
            Application.End(run);
            MySsh.App.Program.ShutdownUi();
            queue.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        return Task.CompletedTask;
    }
}
