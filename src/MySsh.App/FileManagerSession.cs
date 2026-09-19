using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

namespace MySsh.App;

internal static class FileManagerSession
{
    internal static async Task RunAsync(Connection connection, SettingsStore settings)
    {
        using var interactions = new TerminalInteractionQueue();
        await using var local = new LocalFileSystem();
        var connect = SftpFileSystem.ConnectAsync(connection, CancellationToken.None, interactions);
        await using var remote = await AuthenticateUntilCompletedAsync(interactions, connect);
        using var journal = new TransferJournal(Path.Combine(settings.DirectoryPath, "transfers"), connection.Key);
        await using var transfers = new TransferQueue(settings.Config.ParallelTransfers, journal, local, remote);
        try { await RunConnectedAsync(connection, settings, local, remote, transfers, interactions); }
        finally
        {
            // Release pending authentication before the queue joins its workers.
            // Disposal checkpoints unfinished persistent jobs as paused.
            interactions.Dispose();
        }
    }

    // The caller owns connections and the queue. Screen transitions never dispose
    // them; only leaving this connection returns ownership to the caller.
    internal static async Task RunConnectedAsync(Connection connection, SettingsStore settings,
        IFileSystem local, IFileSystem remote, TransferQueue transfers, TerminalInteractionQueue interactions,
        Action? initializeUi = null, Func<Task>? runSsh = null, Func<bool>? confirmLeave = null)
    {
        initializeUi ??= Program.InitializeUi;
        runSsh ??= async () => { await OpenSsh.RunInteractiveAsync(connection, CancellationToken.None); };
        var state = settings.Browser(connection);
        FileManagerWindow.InteractionState? snapshot = null;
        ExternalEditorRequest? completedEditor = null;
        string? editorError = null;
        string? connectionMessage = transfers.RecoveryWarnings.Count > 0 ? string.Join("\n", transfers.RecoveryWarnings) : null;
        var showActions = false;
        while (true)
        {
            if (showActions)
            {
                ConnectionAction action;
                initializeUi();
                try { action = ShowActions(connection, transfers, interactions, confirmLeave); }
                finally { Program.ShutdownUi(); }
                if (action == ConnectionAction.Leave) return;
                if (action == ConnectionAction.Authenticate)
                {
                    await AuthenticatePendingAsync(interactions);
                    continue;
                }
                if (action == ConnectionAction.OpenSsh)
                {
                    // Existing data transfers continue, but new interactive
                    // authentication waits until SSH relinquishes the terminal.
                    try { await runSsh(); }
                    catch (Exception ex) { connectionMessage = "SSH failed: " + ex.Message; }
                    continue;
                }
                showActions = false;
            }

            var handoff = false;
            var reconnect = false;
            ExternalEditorRequest? requestedEditor = null;
            initializeUi();
            try
            {
                using var window = new FileManagerWindow(connection, local, remote, state, settings, transfers);
                Application.Top.Add(window);
                if (snapshot is not null) window.RestoreInteractionState(snapshot);
                if (connectionMessage is not null) window.SetConnectionMessage(connectionMessage);
                Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(50), _ =>
                {
                    if (Application.Current != Application.Top || UiFileOperation.IsBusy) return true;
                    if (completedEditor is { } edited)
                    {
                        completedEditor = null;
                        var error = editorError;
                        editorError = null;
                        try { window.CompleteExternalEdit(edited, error); }
                        catch (Exception ex) { window.SetConnectionMessage("Editor recovery: " + ex.Message); }
                        return true;
                    }
                    if (!interactions.HasPending) return true;
                    handoff = true;
                    Application.RequestStop();
                    return false;
                });
                Application.Run();
                reconnect = window.ReconnectRequested;
                requestedEditor = window.RequestedEditor;
                snapshot = window.CaptureInteractionState();
                window.CaptureBrowserState();
            }
            finally { UiSessionCleanup.Run(settings.SaveState, Program.ShutdownUi); }

            if (requestedEditor is not null)
            {
                completedEditor = requestedEditor;
                try { await ExternalEditorRunner.RunAsync(requestedEditor); }
                catch (Exception ex) { editorError = ex.Message; }
                continue;
            }
            if (!handoff && !reconnect)
            {
                // Esc means navigation, not cancellation/disposal of the queue.
                showActions = true;
                continue;
            }
            if (reconnect)
            {
                try
                {
                    async Task<bool> ReconnectAsync()
                    {
                        if (remote is not SftpFileSystem sftp) throw new IOException("This backend cannot reconnect.");
                        await sftp.ReconnectAsync(CancellationToken.None).ConfigureAwait(false);
                        return true;
                    }
                    await AuthenticateUntilCompletedAsync(interactions, ReconnectAsync());
                    connectionMessage = "Remote SFTP session reconnected.";
                }
                catch (Exception ex) { connectionMessage = "Reconnect failed: " + ex.Message; }
            }
            else
            {
                await AuthenticatePendingAsync(interactions);
                connectionMessage = null;
            }
        }
    }

    private static ConnectionAction ShowActions(Connection connection, TransferQueue transfers,
        TerminalInteractionQueue interactions, Func<bool>? confirmLeave)
    {
        using var dialog = new ConnectionActionDialog(connection, transfers, confirmLeave);
        var timer = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(100), _ =>
        {
            dialog.UpdateStatus();
            if (Application.Current != dialog || !interactions.HasPending) return true;
            dialog.TryChoose(ConnectionAction.Authenticate);
            return false;
        });
        try { Application.Run(dialog); return dialog.Result; }
        finally { Application.MainLoop.RemoveTimeout(timer); }
    }

    private static async Task<T> AuthenticateUntilCompletedAsync<T>(TerminalInteractionQueue interactions, Task<T> task)
    {
        while (!task.IsCompleted)
        {
            if (interactions.HasPending) await AuthenticatePendingAsync(interactions);
            else await Task.WhenAny(task, Task.Delay(10)).ConfigureAwait(false);
        }
        return await task.ConfigureAwait(false);
    }

    private static async Task AuthenticatePendingAsync(TerminalInteractionQueue interactions)
    {
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; interactions.CancelPending(); };
        Console.CancelKeyPress += cancel;
        try
        {
            await AuthenticationScreen.RunAsync(async () =>
            {
                Console.Error.WriteLine("Connecting SFTP session(s). Ctrl+C cancels pending authentication.");
                await interactions.DrainAsync().ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
