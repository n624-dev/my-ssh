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
        string? connectionMessage = null;
        try
        {
            while (true)
            {
                var handoff = false;
                var reconnect = false;
                Program.InitializeUi();
                try
                {
                    using var window = new FileManagerWindow(connection, local, remote, state, settings, transfers);
                    Application.Top.Add(window);
                    if (snapshot is not null) window.RestoreInteractionState(snapshot);
                    if (connectionMessage is not null) window.SetConnectionMessage(connectionMessage);
                    Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(50), _ =>
                    {
                        // Never dismiss a modal editor or confirmation prompt to
                        // authenticate a background job. The job waits instead.
                        if (!interactions.HasPending || Application.Current != Application.Top) return true;
                        handoff = true;
                        Application.RequestStop();
                        return false;
                    });
                    Application.Run();
                    reconnect = window.ReconnectRequested;
                    snapshot = window.CaptureInteractionState();
                    state.LocalPath = snapshot.LocalPath;
                    state.RemotePath = snapshot.RemotePath;
                }
                finally
                {
                    try { settings.SaveState(); }
                    finally { Program.ShutdownUi(); }
                }
                if (!handoff && !reconnect) break;

                // All console readers have stopped. Existing data transfers and
                // their queue survive this screen transition.
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
            // Pending connection requests must finish before the queue's worker
            // join. In particular, exiting while a modal dialog is open is safe.
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
