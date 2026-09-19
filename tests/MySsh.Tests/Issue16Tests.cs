using MySsh.App;
using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

internal sealed class Issue16Tests : IRegressionCase
{
    public string Name => "#16 filesystem waits keep the terminal loop responsive and cancellable";

    public Task RunAsync()
    {
        Application.Init(new FakeDriver());
        try
        {
            var uiThread = Environment.CurrentManagedThreadId;
            var workerThread = uiThread;
            var ticks = 0;
            var heartbeat = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(10), _ => { ticks++; return true; });
            try
            {
                var answer = UiFileOperation.Run("Delayed operation", async ct =>
                {
                    workerThread = Environment.CurrentManagedThreadId;
                    // Cover a backend's synchronous prefix before its first await.
                    Thread.Sleep(80);
                    await Task.Delay(60, ct).ConfigureAwait(false);
                    return 42;
                });
                RegressionCases.Check(answer == 42 && workerThread != uiThread && ticks >= 3,
                    "The filesystem call blocked the UI thread or its event loop.");

                var stopped = false;
                var cancel = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(40), _ =>
                {
                    if (Application.Current is not Dialog dialog || dialog.Title.ToString() != "Cancel operation") return true;
                    Application.RequestStop(dialog); // Same close path used by Esc.
                    return false;
                });
                try
                {
                    Expect<OperationCanceledException>(() => UiFileOperation.Run("Cancel operation", async ct =>
                    {
                        try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
                        finally { stopped = true; }
                    }));
                }
                finally { Application.MainLoop.RemoveTimeout(cancel); }
                RegressionCases.Check(stopped && !UiFileOperation.IsBusy,
                    "Cancellation returned before worker cleanup or left the UI busy.");
                Expect<TimeoutException>(() => UiFileOperation.Run("Timeout operation",
                    ct => Task.Delay(Timeout.Infinite, ct), TimeSpan.FromMilliseconds(60)));
                Expect<IOException>(() => UiFileOperation.Run("Failing operation",
                    _ => Task.FromException(new IOException("Injected filesystem failure."))));
                RegressionCases.Check(UiFileOperation.Run("After failure", _ => Task.FromResult(7)) == 7,
                    "An error prevented the next operation.");
                CheckSilentSftp();
                CheckBrowserReads(uiThread);
            }
            finally { Application.MainLoop.RemoveTimeout(heartbeat); }
        }
        finally { MySsh.App.Program.ShutdownUi(); }
        return Task.CompletedTask;
    }

    private static void CheckSilentSftp()
    {
        var replies = new SilentReplyStream(ScriptedSftp.Version().Concat(ScriptedSftp.Handle(1)).ToArray());
        var session = SftpSession.ConnectAsync(new MemoryStream(), replies, CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            Expect<TimeoutException>(() => UiFileOperation.Run("Unresponsive SFTP",
                ct => session.ListAsync("directory", ct), TimeSpan.FromMilliseconds(100)));
            RegressionCases.Check(session.IsFaulted && replies.Closed,
                "Timed-out SFTP listing did not close its interrupted transport.");
        }
        finally { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void CheckBrowserReads(int uiThread)
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        using var settings = new SettingsStore(root.File("settings"));
        var remote = new ThreadCheckedFileSystem(local, uiThread);
        var queue = new TransferQueue(1);
        try
        {
            using var window = new FileManagerWindow(new Connection("fixture.test", "test"), local, remote,
                new BrowserState { LocalPath = root.Path, RemotePath = root.Path }, settings, queue);
            RegressionCases.Check(remote.Calls >= 2 && !remote.CalledOnUi,
                "Browser path/listing I/O still ran on the UI thread.");
        }
        finally { queue.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class ThreadCheckedFileSystem(IFileSystem inner, int uiThread) : DelegatingFileSystem(inner)
    {
        public bool CalledOnUi;
        public int Calls;
        private async Task Check(CancellationToken ct)
        {
            Calls++;
            CalledOnUi |= Environment.CurrentManagedThreadId == uiThread;
            await Task.Delay(30, ct).ConfigureAwait(false);
        }
        public override async Task<string> CanonicalAsync(string path, CancellationToken ct)
        {
            await Check(ct).ConfigureAwait(false);
            return await base.CanonicalAsync(path, ct).ConfigureAwait(false);
        }
        public override async Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct)
        {
            await Check(ct).ConfigureAwait(false);
            return await base.ListAsync(path, ct).ConfigureAwait(false);
        }
    }

    private sealed class SilentReplyStream(byte[] prefix) : MemoryStream(prefix)
    {
        public bool Closed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, ct);
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
}
