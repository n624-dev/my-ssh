using MySsh.App;
using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class AuthenticationInteractionCase : IRegressionCase
{
    public string Name => "B12 serialized terminal ownership for initial, sibling and reconnect handshakes";

    public async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        using (var queue = new TerminalInteractionQueue())
        {
            var executing = 0;
            var maximum = 0;
            var started = 0;
            var tasks = Enumerable.Range(0, 6).Select(index => queue.RunAsync(async token =>
            {
                started++;
                maximum = Math.Max(maximum, Interlocked.Increment(ref executing));
                try { await Task.Delay(5, token); return index; }
                finally { Interlocked.Decrement(ref executing); }
            }, ct)).ToArray();
            RegressionCases.Check(started == 0 && queue.HasPending, "A handshake started before terminal ownership was granted.");
            await Task.WhenAll(queue.DrainAsync(), queue.DrainAsync()).WaitAsync(ct);
            var values = await Task.WhenAll(tasks);
            RegressionCases.Check(maximum == 1 && values.SequenceEqual(Enumerable.Range(0, 6)),
                "Concurrent authentication prompts or lost results.");

            using var cancelled = new CancellationTokenSource();
            var shouldNotRun = queue.RunAsync<int>(_ => throw new InvalidOperationException("Cancelled handshake started."), cancelled.Token);
            cancelled.Cancel();
            await RegressionCases.ThrowsAsync<OperationCanceledException>(() => shouldNotRun.WaitAsync(ct));
            await queue.DrainAsync().WaitAsync(ct);

            var failed = queue.RunAsync<int>(_ => Task.FromException<int>(new IOException("Authentication denied.")), ct);
            var next = queue.RunAsync(_ => Task.FromResult(17), ct);
            await queue.DrainAsync().WaitAsync(ct);
            await RegressionCases.ThrowsAsync<IOException>(() => failed);
            RegressionCases.Check(await next == 17, "One failed authentication stranded subsequent requests.");

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = queue.RunAsync(async token =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            }, ct);
            var pump = queue.DrainAsync();
            await entered.Task.WaitAsync(ct);
            queue.CancelPending();
            await pump.WaitAsync(ct);
            await RegressionCases.ThrowsAsync<OperationCanceledException>(() => active);
        }

        var exiting = new TerminalInteractionQueue();
        var pending = exiting.RunAsync(_ => Task.FromResult(1), ct);
        exiting.Dispose();
        await RegressionCases.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(ct));
        RegressionCases.Check(!exiting.HasPending, "Shutdown left pending authentication requests.");

        // Cancelling an unpumped SFTP request proves process creation is inside
        // the coordinator, not merely the wait for its completion.
        using (var queue = new TerminalInteractionQueue())
        using (var cancelled = new CancellationTokenSource())
        {
            var pendingSftp = SftpFileSystem.ConnectAsync(new Connection("unused.invalid", "test"), cancelled.Token, queue);
            RegressionCases.Check(queue.HasPending && !pendingSftp.IsCompleted, "SFTP bypassed the authentication queue.");
            cancelled.Cancel();
            await RegressionCases.ThrowsAsync<OperationCanceledException>(() => pendingSftp.WaitAsync(ct));
        }

        // The existing CI fixture is loopback-only and uses disposable keys.
        // Real user servers, passwords and Cloudflare credentials are not used.
        if (Environment.GetEnvironmentVariable("MYSSH_TEST_HOST") is { Length: > 0 } host)
        {
            var connection = new Connection(host, Environment.GetEnvironmentVariable("MYSSH_TEST_USER")!);
            var interaction = new CountingInteraction();
            await using var remote = await SftpFileSystem.ConnectAsync(connection, ct, interaction);
            await using var sibling = await remote.CreateSiblingAsync(ct);
            await remote.ReconnectAsync(ct);
            RegressionCases.Check(interaction.Calls == 3, "Initial, sibling or reconnect handshake bypassed the shared interaction handler.");
            await remote.CanonicalAsync(".", ct);
            await sibling.CanonicalAsync(".", ct);
        }
    }

    private sealed class CountingInteraction : IConnectionInteraction
    {
        public int Calls;
        public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> connect, CancellationToken ct)
        {
            Calls++;
            return connect(ct);
        }
    }
}
