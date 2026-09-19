using System.Reflection;
using MySsh.App;
using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

internal sealed class Issue20Tests : IRegressionCase
{
    public string Name => "#20 automatic refresh restores file selections by path, not index";

    public Task RunAsync()
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        using var settings = new SettingsStore(root.File("settings"));
        var directory = root.File("pane");
        Directory.CreateDirectory(directory);
        var first = Path.Combine(directory, "b-first.txt");
        var second = Path.Combine(directory, "c-second.txt");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        var transferSource = root.File("transfer.txt");
        File.WriteAllText(transferSource, "new entry shifts row indices");
        var queue = new TransferQueue(1);
        Application.Init(new FakeDriver());
        var run = Application.Begin(Application.Top);
        try
        {
            using var window = new FileManagerWindow(new Connection("selection.test", "test"), local, local,
                new BrowserState { LocalPath = directory, RemotePath = directory }, settings, queue);
            Application.Top.Add(window);
            window.InitializeBrowser();
            var desired = window.CaptureInteractionState() with
            {
                Local = new(second, [first, second]), Remote = new(first, [second])
            };
            window.RestoreInteractionState(desired);
            queue.Enqueue(local, transferSource, local, Path.Combine(directory, "a-new.txt"), new());
            UiFileOperation.Run("Wait for fixture transfer", async ct =>
            {
                while (true)
                {
                    var job = queue.Snapshot().Single();
                    if (job.State == TransferState.Completed) return;
                    if (job.State is TransferState.Failed or TransferState.Partial)
                        throw new IOException(job.Message);
                    await Task.Delay(10, ct).ConfigureAwait(false);
                }
            }, TimeSpan.FromSeconds(10));
            Invoke(window, "RefreshQueue");
            var refreshed = window.CaptureInteractionState();
            RegressionCases.Check(refreshed.Local.Selected == second && refreshed.Remote.Selected == first,
                "A newly added earlier row changed the selected file.");
            RegressionCases.Check(refreshed.Local.Marked.Order().SequenceEqual(new[] { first, second }.Order()) &&
                refreshed.Remote.Marked.SequenceEqual(new[] { second }), "Transfer completion erased or moved marks.");
            File.Delete(second);
            Invoke(window, "ReloadPane", true);
            Invoke(window, "ReloadPane", false);
            var deleted = window.CaptureInteractionState();
            RegressionCases.Check(deleted.Local.Marked.SequenceEqual(new[] { first }) && deleted.Remote.Marked.Length == 0,
                "Refresh selected another file in place of a deleted marked path.");
            RegressionCases.Check(deleted.Remote.Selected == first, "An unchanged cursor path was lost.");
            window.NavigatePane(true, root.Path);
            RegressionCases.Check(window.CaptureInteractionState().Local.Marked.Length == 0,
                "Navigating to another directory carried marks across paths.");
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

    private static void Invoke(FileManagerWindow window, string name, params object[] args) =>
        (typeof(FileManagerWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Missing operation: " + name)).Invoke(window, args);
}
