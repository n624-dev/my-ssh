using MySsh.App;
using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

internal sealed class Issue17Tests : IRegressionCase
{
    public string Name => "#17 external editors own the terminal only between UI sessions";

    public async Task RunAsync()
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        using var settings = new SettingsStore(root.File("settings"));
        settings.Config.Editor = "editor fixture";
        settings.Config.EditorArguments = ["--wait", "argument with spaces", "日本語"];
        var source = root.File("original.txt");
        await File.WriteAllTextAsync(source, "original");
        var session = await EditSession.OpenAsync(local, source, root.File("drafts"), "LOCAL", CancellationToken.None);
        await using var queue = new TransferQueue(1);
        ExternalEditorRequest request;
        FileManagerWindow.InteractionState snapshot;
        Application.Init(new FakeDriver());
        var run = Application.Begin(Application.Top);
        try
        {
            using var window = new FileManagerWindow(new Connection("fixture.test", "test"), local, local,
                new BrowserState { LocalPath = root.Path, RemotePath = root.Path }, settings, queue);
            Application.Top.Add(window);
            window.InitializeBrowser();
            snapshot = window.CaptureInteractionState();
            window.RequestExternalEdit(session, local);
            request = window.RequestedEditor ?? throw new Exception("No editor handoff was requested.");
            settings.Config.EditorArguments.Clear();
            RegressionCases.Check(request.Arguments.Count == 3, "Editor arguments were not snapshotted.");
            var invoked = false;
            try
            {
                ExternalEditorRunner.RunAsync(request, _ => { invoked = true; return Task.FromResult(0); }).GetAwaiter().GetResult();
                throw new Exception("Editor started while the UI was active.");
            }
            catch (InvalidOperationException) { }
            RegressionCases.Check(!invoked, "Editor process raced with the active terminal driver.");
            Application.Top.Remove(window);
        }
        finally { Application.End(run); MySsh.App.Program.ShutdownUi(); }

        await ExternalEditorRunner.RunAsync(request, async info =>
        {
            RegressionCases.Check(Application.Driver is null, "The UI still owns the terminal.");
            RegressionCases.Check(!info.UseShellExecute && !info.RedirectStandardInput &&
                !info.RedirectStandardOutput && !info.RedirectStandardError, "Editor does not inherit terminal streams.");
            RegressionCases.Check(info.ArgumentList.SequenceEqual(new[] { "--wait", "argument with spaces", "日本語", session.DraftPath }),
                "Arguments or the draft path were split or changed.");
            await File.WriteAllTextAsync(session.DraftPath, "edited draft");
            return 0;
        });
        await RegressionCases.ThrowsAsync<IOException>(() => ExternalEditorRunner.RunAsync(request, _ => Task.FromResult(7)));
        await RegressionCases.ThrowsAsync<IOException>(() => ExternalEditorRunner.RunAsync(request,
            _ => Task.FromException<int>(new IOException("Injected launch failure"))));
        RegressionCases.Check(File.Exists(session.DraftPath) && await File.ReadAllTextAsync(session.DraftPath) == "edited draft",
            "An editor failure lost the draft.");
        RegressionCases.Check(await File.ReadAllTextAsync(source) == "original", "The draft was uploaded without confirmation.");

        Application.Init(new FakeDriver());
        var restoredRun = Application.Begin(Application.Top);
        try
        {
            using var restored = new FileManagerWindow(new Connection("fixture.test", "test"), local, local,
                new BrowserState { LocalPath = root.Path, RemotePath = root.Path }, settings, queue);
            Application.Top.Add(restored);
            restored.RestoreInteractionState(snapshot);
            restored.InitializeBrowser();
            var actual = restored.CaptureInteractionState();
            RegressionCases.Check(actual.LocalPath == snapshot.LocalPath && actual.RemotePath == snapshot.RemotePath,
                "Editor return lost the pane paths.");
            RegressionCases.Check(File.Exists(session.DraftPath), "Recreating the UI discarded the draft.");
            Application.Top.Remove(restored);
        }
        finally { Application.End(restoredRun); MySsh.App.Program.ShutdownUi(); }
    }
}
