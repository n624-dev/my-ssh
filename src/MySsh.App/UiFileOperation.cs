using Terminal.Gui;

namespace MySsh.App;

/// <summary>
/// Executes filesystem work on a worker while a modal event loop keeps rendering
/// and cancellation responsive. Only the caller applies the result to the UI.
/// </summary>
internal static class UiFileOperation
{
    private static int _busy;
    internal static bool IsBusy => Volatile.Read(ref _busy) != 0;

    internal static void Run(string title, Func<CancellationToken, Task> operation,
        TimeSpan? timeout = null) => Run(title, async token =>
        {
            await operation(token).ConfigureAwait(false);
            return true;
        }, timeout);

    internal static T Run<T>(string title, Func<CancellationToken, Task<T>> operation,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("A filesystem operation is already running.");
        try
        {
            using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var userCancelled = false;
            // Even a backend which does work before its first await never runs
            // on the terminal input thread.
            var worker = Task.Run(() => operation(cancellation.Token));
            var cancel = new Button("Cancel") { IsDefault = true };
            using var dialog = new Dialog(title,
                Math.Min(74, Math.Max(1, Application.Driver.Cols - 2)),
                Math.Min(9, Math.Max(1, Application.Driver.Rows - 2)), cancel);
            var status = new Label("Waiting for filesystem response...")
            {
                X = 1, Y = 1, Width = Dim.Fill(1), Height = 2
            };
            dialog.Add(status);
            void Cancel()
            {
                userCancelled = true;
                cancellation.Cancel();
                status.Text = "Cancelling... Completed changes are not rolled back.";
            }
            cancel.Clicked += Cancel;
            dialog.Closing += args =>
            {
                if (worker.IsCompleted) return;
                args.Cancel = true;
                Cancel();
            };
            var timer = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(25), _ =>
            {
                if (worker.IsCompleted)
                {
                    Application.RequestStop(dialog);
                    return false;
                }
                if (deadline.IsCancellationRequested)
                    status.Text = "Operation timed out. Closing the interrupted connection...";
                return true;
            });
            try
            {
                if (!worker.IsCompleted) Application.Run(dialog);
                // No pending network wait remains here: the modal loop closes
                // only after the worker (including its cleanup) has completed.
                try { return worker.GetAwaiter().GetResult(); }
                catch (OperationCanceledException ex) when (deadline.IsCancellationRequested && !userCancelled)
                {
                    throw new TimeoutException("Filesystem operation timed out. Completed changes are not rolled back. " + ex.Message, ex);
                }
            }
            catch
            {
                cancellation.Cancel();
                // Do not orphan an operation that can still modify files after
                // its dialog disappeared. SFTP cancellation closes its transport.
                if (!worker.IsCompleted)
                {
                    try { worker.GetAwaiter().GetResult(); } catch { }
                }
                throw;
            }
            finally { Application.MainLoop.RemoveTimeout(timer); }
        }
        finally { Volatile.Write(ref _busy, 0); }
    }
}
