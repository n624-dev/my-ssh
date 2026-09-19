using System.Diagnostics;
using MySsh.Infrastructure;

internal sealed class Issue14Tests : IRegressionCase
{
    public string Name => "#14 directory close has a deadline and never masks the listing error";

    public async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        foreach (var cancelCaller in new[] { false, true })
        {
            var replies = new SilentTailStream(ScriptedSftp.Version().Concat(ScriptedSftp.Handle(1))
                .Concat(ScriptedSftp.Status(2, 1)).ToArray());
            await using var session = await SftpSession.ConnectAsync(new MemoryStream(), replies, ct);
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var watch = Stopwatch.StartNew();
            var listing = session.ListAsync("directory", caller.Token);
            await replies.Silent.Task.WaitAsync(ct);
            if (cancelCaller) caller.Cancel();
            await RegressionCases.ThrowsAsync<IOException>(() => listing.WaitAsync(TimeSpan.FromSeconds(8), ct));
            RegressionCases.Check(watch.Elapsed < TimeSpan.FromSeconds(7) && session.IsFaulted && replies.Closed,
                "Unresponsive directory CLOSE prevented bounded cleanup.");
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), ct);
        }

        foreach (var silent in new[] { false, true })
        {
            var prefix = ScriptedSftp.Version().Concat(ScriptedSftp.Handle(1))
                .Concat(ScriptedSftp.Status(2, 3, "PRIMARY_LIST_ERROR")).ToArray();
            Stream responses = silent ? new SilentTailStream(prefix) :
                new MemoryStream(prefix.Concat(ScriptedSftp.Status(3, 4, "SECONDARY_CLOSE_ERROR")).ToArray());
            await using var session = await SftpSession.ConnectAsync(new MemoryStream(), responses, ct);
            var error = await RegressionCases.ThrowsAsync<IOException>(() => session.ListAsync("directory", ct).WaitAsync(TimeSpan.FromSeconds(8), ct));
            RegressionCases.Check(error.Message.Contains("PRIMARY_LIST_ERROR") && !error.Message.Contains("SECONDARY_CLOSE_ERROR"),
                "Cleanup masked the original directory-listing error.");
            RegressionCases.Check(session.IsFaulted, "Failed cleanup retained a server handle/session.");
        }

        var healthyReplies = ScriptedSftp.Version().Concat(ScriptedSftp.Handle(1))
            .Concat(ScriptedSftp.Status(2, 1)).Concat(ScriptedSftp.Status(3, 0))
            .Concat(ScriptedSftp.Handle(4)).ToArray();
        await using var healthy = await SftpSession.ConnectAsync(new MemoryStream(), new MemoryStream(healthyReplies), ct);
        RegressionCases.Check((await healthy.ListAsync("directory", ct)).Count == 0, "Empty directory listing failed.");
        await healthy.OpenAsync("next", SftpSession.OpenRead, ct);
        RegressionCases.Check(!healthy.IsFaulted, "Successful directory close discarded a healthy session.");
    }

    private sealed class SilentTailStream(byte[] prefix) : MemoryStream(prefix)
    {
        public bool Closed;
        public TaskCompletionSource Silent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, ct);
            Silent.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
}
