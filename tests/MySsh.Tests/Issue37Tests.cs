using MySsh.App;
using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

internal sealed class Issue37Tests : IRegressionCase
{
    public string Name => "#37 leaving the browser returns to Action without disposing its live queue";
    public async Task RunAsync()
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        using var settings = new SettingsStore(root.File("settings"));
        var sourceDirectory = root.File("source");
        var destinationDirectory = root.File("destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Combine(sourceDirectory, "file.txt");
        await File.WriteAllTextAsync(source, "retained transfer source");
        var connection = new Connection("session.test", "user");
        var state = settings.Browser(connection);
        state.LocalPath = sourceDirectory;
        state.RemotePath = destinationDirectory;
        state.LocalSelection = source;
        state.LocalMarked = [source];
        using var interactions = new TerminalInteractionQueue();
        var journalRoot = root.File("journal");
        var queue = new TransferQueue(1, new TransferJournal(journalRoot, connection.Key), local, local);
        var blocking = new BlockingSource(local);
        var id = queue.Enqueue(blocking, source, local, Path.Combine(destinationDirectory, "file.txt"), new());
        var browsers = 0;
        var actions = 0;
        var shellRuns = 0;
        var leavePrompts = 0;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        try
        {
            await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await FileManagerSession.RunConnectedAsync(connection, settings, local, local, queue, interactions,
                initializeUi: () =>
                {
                    Application.Init(new FakeDriver());
                    Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(20), _ =>
                    {
                        if (DateTime.UtcNow > deadline) throw new TimeoutException("Session navigation fixture did not finish.");
                        if (UiFileOperation.IsBusy) return true;
                        if (Application.Current is ConnectionActionDialog dialog)
                        {
                            actions++;
                            CheckRunning(queue, id);
                            if (actions == 1) dialog.TryChoose(ConnectionAction.FileTransfer);
                            else if (actions == 2) dialog.TryChoose(ConnectionAction.OpenSsh);
                            else
                            {
                                RegressionCases.Check(!dialog.TryChoose(ConnectionAction.Leave), "Declining exit did not keep the connection open.");
                                CheckRunning(queue, id);
                                RegressionCases.Check(dialog.TryChoose(ConnectionAction.Leave), "Explicitly confirmed exit did not leave.");
                            }
                            return false;
                        }
                        if (Application.Current != Application.Top) return true;
                        var window = Application.Top.Subviews.OfType<FileManagerWindow>().FirstOrDefault();
                        if (window is null) return true;
                        browsers++;
                        CheckRunning(queue, id);
                        var view = window.CaptureInteractionState();
                        RegressionCases.Check(view.Local.Selected == source && view.Local.Marked.SequenceEqual(new[] { source }),
                            "Returning from Action lost the browser selection.");
                        Application.RequestStop(); // The same action as the browser's Esc status item.
                        return false;
                    });
                },
                runSsh: () =>
                {
                    RegressionCases.Check(Application.Driver is null, "SSH ran while Terminal.Gui still owned the terminal.");
                    CheckRunning(queue, id);
                    shellRuns++;
                    return Task.CompletedTask;
                },
                confirmLeave: () => ++leavePrompts > 1);
            RegressionCases.Check(browsers == 2 && actions == 3 && shellRuns == 1 && leavePrompts == 2,
                "The expected browser/Action/SSH navigation sequence was not followed.");
            CheckRunning(queue, id); // The controller does not own/dispose this queue.
        }
        finally
        {
            if (Application.Driver is not null) MySsh.App.Program.ShutdownUi();
            interactions.Dispose();
            await queue.DisposeAsync();
        }
        await using var recovered = new TransferQueue(1, new TransferJournal(journalRoot, connection.Key), local, local);
        RegressionCases.Check(recovered.Snapshot().Single().State == TransferState.Paused && File.Exists(source),
            "Explicit session exit did not checkpoint unfinished work as paused.");
    }

    private static void CheckRunning(TransferQueue queue, Guid id)
    {
        var job = queue.Snapshot().Single();
        RegressionCases.Check(job.Id == id && job.State == TransferState.Running, "Navigation cancelled, replaced or disposed the live transfer.");
    }

    private sealed class BlockingSource(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return await base.OpenReadAsync(path, ct);
        }
    }
}
