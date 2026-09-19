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
        await using var transfers = new TransferQueue(settings.Config.ParallelTransfers);
        var state = settings.Browser(connection);
        FileManagerWindow.InteractionState? snapshot = null;
        ExternalEditorRequest? completedEditor = null;
        string? editorError = null;
        string? connectionMessage = null;
        try
        {
            while (true)
            {
                var handoff = false;
                var reconnect = false;
                ExternalEditorRequest? requestedEditor = null;
                Program.InitializeUi();
                try
                {
                    using var window = new FileManagerWindow(connection, local, remote, state, settings, transfers);
                    Application.Top.Add(window);
                    if (snapshot is not null) window.RestoreInteractionState(snapshot);
                    if (connectionMessage is not null) window.SetConnectionMessage(connectionMessage);
                    Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(50), _ =>
                    {
                        // Never interrupt a modal editor, progress or confirmation
                        // dialog to start another process that owns the terminal.
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
                    state.LocalPath = snapshot.LocalPath;
                    state.RemotePath = snapshot.RemotePath;
                }
                finally
                {
                    UiSessionCleanup.Run(settings.SaveState, Program.ShutdownUi);
                }

                if (requestedEditor is not null)
                {
                    // No Terminal.Gui reader or timer is alive here. Pending
                    // authentications wait; the transfer queue and drafts survive.
                    completedEditor = requestedEditor;
                    try { await ExternalEditorRunner.RunAsync(requestedEditor); }
                    catch (Exception ex) { editorError = ex.Message; }
                    continue;
                }
                if (!handoff && !reconnect) break;
                if (reconnect)
                {
                    try
                    {
                        async Task<bool> ReconnectAsync()
                        {
                            await remote.ReconnectAsync(CancellationToken.None).ConfigureAwait(false);
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
        finally
        {
            // Release queued authentication requests before joining workers.
            interactions.Dispose();
        }
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
        ConsoleCancelEventHandler cancel = (_, args) =>
        {
            args.Cancel = true;
            interactions.CancelPending();
        };
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
