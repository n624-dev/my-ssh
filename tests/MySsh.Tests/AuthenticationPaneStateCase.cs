using MySsh.App;
using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

internal sealed class AuthenticationPaneStateCase : IRegressionCase
{
    public string Name => "B12 authentication screen recreation preserves pane marks and position";

    public Task RunAsync()
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        using var remote = new LocalFileSystem();
        using var settings = new SettingsStore(root.File("settings"));
        var path = root.File("files");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "first.txt"), "first");
        File.WriteAllText(Path.Combine(path, "second.txt"), "second");
        var queue = new TransferQueue(1);
        var driver = new FakeDriver();
        Application.Init(driver);
        try
        {
            var state = new BrowserState { LocalPath = path, RemotePath = path };
            using var window = new FileManagerWindow(new Connection("local.test", "test"), local, remote, state, settings, queue);
            Application.Top.Add(window);
            var initial = window.CaptureInteractionState();
            var desired = initial with
            {
                ActiveLocal = false,
                Local = new(Path.Combine(path, "second.txt"), [Path.Combine(path, "first.txt"), Path.Combine(path, "second.txt")]),
                Remote = new(Path.Combine(path, "first.txt"), [Path.Combine(path, "first.txt")])
            };
            window.RestoreInteractionState(desired);
            var restored = window.CaptureInteractionState();
            RegressionCases.Check(restored.Local.Selected == desired.Local.Selected, "Local cursor was lost.");
            RegressionCases.Check(restored.Local.Marked.Order().SequenceEqual(desired.Local.Marked.Order()), "Local marks were lost.");
            RegressionCases.Check(restored.Remote.Marked.SequenceEqual(desired.Remote.Marked), "Remote marks were lost.");
            RegressionCases.Check(!restored.ActiveLocal, "Active pane changed.");
        }
        finally
        {
            MySsh.App.Program.ShutdownUi();
            queue.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        return Task.CompletedTask;
    }
}
