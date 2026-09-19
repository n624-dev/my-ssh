using MySsh.App;
using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

internal sealed class Issue19Tests : IRegressionCase
{
    public string Name => "#19 failed navigation preserves both displayed and operational paths";

    public Task RunAsync()
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        using var settings = new SettingsStore(root.File("settings"));
        var beforePath = root.File("before");
        var afterPath = root.File("after");
        var blockedPath = root.File("blocked");
        Directory.CreateDirectory(beforePath);
        Directory.CreateDirectory(afterPath);
        Directory.CreateDirectory(blockedPath);
        var kept = Path.Combine(beforePath, "kept.txt");
        File.WriteAllText(kept, "keep me");
        var backend = new FailingDirectory(local);
        var queue = new TransferQueue(1);
        var connection = new Connection("navigation.test", "test");
        var state = settings.Browser(connection);
        state.LocalPath = beforePath;
        state.RemotePath = beforePath;
        Application.Init(new FakeDriver());
        var run = Application.Begin(Application.Top);
        try
        {
            using var window = new FileManagerWindow(connection, backend, backend, state, settings, queue);
            Application.Top.Add(window);
            window.InitializeBrowser();
            var desired = window.CaptureInteractionState() with
            {
                Local = new(kept, [kept]), Remote = new(kept, [kept])
            };
            window.RestoreInteractionState(desired);
            var saved = File.ReadAllText(Path.Combine(settings.DirectoryPath, "state.json"));
            backend.FailurePath = blockedPath;
            foreach (var cancelled in new[] { false, true })
            {
                backend.Cancelled = cancelled;
                ExpectFailure(() => window.NavigatePane(true, blockedPath), cancelled);
                CheckUnchanged(window, desired, state);
                ExpectFailure(() => window.NavigateBoth(afterPath, blockedPath), cancelled);
                CheckUnchanged(window, desired, state);
                RegressionCases.Check(File.ReadAllText(Path.Combine(settings.DirectoryPath, "state.json")) == saved,
                    "Failed navigation persisted a path that was never displayed.");
            }
            backend.FailurePath = null;
            window.NavigatePane(true, afterPath);
            RegressionCases.Check(window.CaptureInteractionState().LocalPath == afterPath && state.LocalPath == afterPath,
                "Successful navigation failed to update the destination.");
            RegressionCases.Check(window.CaptureInteractionState().RemotePath == beforePath,
                "Single-pane navigation changed the other destination.");
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

    private static void CheckUnchanged(FileManagerWindow window, FileManagerWindow.InteractionState before, BrowserState saved)
    {
        var after = window.CaptureInteractionState();
        RegressionCases.Check(after.LocalPath == before.LocalPath && after.RemotePath == before.RemotePath &&
            saved.LocalPath == before.LocalPath && saved.RemotePath == before.RemotePath,
            "A failed directory read changed an internal or saved transfer destination.");
        RegressionCases.Check(after.Local.Selected == before.Local.Selected && after.Remote.Selected == before.Remote.Selected &&
            after.Local.Marked.SequenceEqual(before.Local.Marked) && after.Remote.Marked.SequenceEqual(before.Remote.Marked),
            "A failed directory read replaced the old selection or listing.");
    }

    private static void ExpectFailure(Action action, bool cancelled)
    {
        try { action(); }
        catch (OperationCanceledException) when (cancelled) { return; }
        catch (UnauthorizedAccessException) when (!cancelled) { return; }
        throw new InvalidOperationException("Navigation did not report the injected failure.");
    }

    private sealed class FailingDirectory(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public string? FailurePath;
        public bool Cancelled;
        public override Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct)
        {
            if (path.Equals(FailurePath, PathComparison))
                return Task.FromException<IReadOnlyList<FileEntry>>(Cancelled
                    ? new OperationCanceledException("Cancelled navigation")
                    : new UnauthorizedAccessException("Injected unreadable directory"));
            return base.ListAsync(path, ct);
        }
    }
}
