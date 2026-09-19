using MySsh.App;
using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

internal sealed class Issue34Tests : IRegressionCase
{
    public string Name => "#34 pane selections persist per connection and restore only existing paths";
    public Task RunAsync()
    {
        using var root = new TestDirectory();
        var directory = root.File("files");
        Directory.CreateDirectory(directory);
        var first = Path.Combine(directory, "b.txt");
        var second = Path.Combine(directory, "c.txt");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        var connection = new Connection("saved.test", "user");
        var other = new Connection("saved.test", "other");
        var configuration = root.File("settings");
        using (var settings = new SettingsStore(configuration))
        {
            var state = settings.Browser(connection);
            state.LocalPath = directory;
            state.RemotePath = directory;
            var otherState = settings.Browser(other);
            otherState.LocalSelection = "unrelated-selection";
            WithWindow(settings, connection, window =>
            {
                var desired = window.CaptureInteractionState() with
                {
                    Local = new(second, [first, second]), Remote = new(first, [second]),
                    LocalFilter = ".txt", RemoteFilter = ".txt", Descending = true, ActiveLocal = false
                };
                window.RestoreInteractionState(desired);
                window.CaptureBrowserState();
                settings.SaveState();
            });
        }
        // Different row ordering and a missing file must not select replacements by index.
        File.WriteAllText(Path.Combine(directory, "a.txt"), "new file");
        File.Delete(first);
        using (var settings = new SettingsStore(configuration))
        {
            WithWindow(settings, connection, window =>
            {
                var actual = window.CaptureInteractionState();
                RegressionCases.Check(actual.Local.Selected == second && actual.Remote.Selected is null,
                    "Restored cursor does not match the saved path or selected a replacement row.");
                RegressionCases.Check(actual.Local.Marked.SequenceEqual(new[] { second }) && actual.Remote.Marked.SequenceEqual(new[] { second }),
                    "Restored marks include missing paths or lost existing entries.");
                RegressionCases.Check(actual.LocalFilter == ".txt" && actual.Descending && !actual.ActiveLocal,
                    "View settings were not restored with the selection.");
                window.CaptureBrowserState();
                settings.SaveState();
            });
            RegressionCases.Check(settings.Browser(other).LocalSelection == "unrelated-selection", "One connection overwrote another's selection.");
        }
        using (var settings = new SettingsStore(configuration))
            RegressionCases.Check(settings.Browser(connection).LocalMarked.SequenceEqual(new[] { second }), "Stale marks were not removed from durable state.");
        return Task.CompletedTask;
    }

    private static void WithWindow(SettingsStore settings, Connection connection, Action<FileManagerWindow> check)
    {
        using var local = new LocalFileSystem();
        var queue = new TransferQueue(1);
        Application.Init(new FakeDriver());
        var run = Application.Begin(Application.Top);
        try
        {
            using var window = new FileManagerWindow(connection, local, local, settings.Browser(connection), settings, queue);
            Application.Top.Add(window);
            window.InitializeBrowser();
            check(window);
            Application.Top.Remove(window);
        }
        finally
        {
            Application.End(run);
            MySsh.App.Program.ShutdownUi();
            queue.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
