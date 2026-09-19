using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class Issue13Tests : IRegressionCase
{
    public string Name => "#13 interrupted SFTP packets discard their session and preserve unknown outcomes";

    public async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        foreach (var replyBytes in new[] { 2, 7 })
        {
            // Stop once in the length header and once inside the response body.
            var prefix = ScriptedSftp.Version().Concat(ScriptedSftp.Handle(1).Take(replyBytes)).ToArray();
            var input = new RecordingInput();
            var output = new GatedOutput(prefix);
            await using var session = await SftpSession.ConnectAsync(input, output, ct);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var opening = session.OpenAsync("partial", SftpSession.OpenWrite, cancellation.Token);
            await output.Blocked.Task.WaitAsync(ct);
            cancellation.Cancel();
            var error = await RegressionCases.ThrowsAsync<SftpRequestCanceledException>(() => opening.WaitAsync(ct));
            RegressionCases.Check(error.OutcomeUnknown && error.RequestType == 3 && error.RequestId == 1,
                "Interrupted OPEN did not report its unknown server outcome.");
            RegressionCases.Check(session.IsFaulted && input.Closed && output.Closed, "Interrupted receive kept its transport open.");
            var count = input.Written;
            await RegressionCases.ThrowsAsync<IOException>(() => session.CloseAsync([1], ct));
            RegressionCases.Check(input.Written == count, "A faulted session sent another packet.");
        }

        foreach (var ioFailure in new[] { false, true })
        {
            var input = new RecordingInput();
            var output = new MemoryStream(ScriptedSftp.Version());
            await using var session = await SftpSession.ConnectAsync(input, output, ct);
            input.FaultAfter = input.Written + 2;
            input.ThrowIo = ioFailure;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var write = session.WriteAsync([1], 0, new byte[] { 1, 2, 3 }, cancellation.Token);
            await input.Blocked.Task.WaitAsync(ct);
            if (ioFailure)
            {
                var error = await RegressionCases.ThrowsAsync<SftpRequestInterruptedException>(() => write.WaitAsync(ct));
                RegressionCases.Check(error.OutcomeUnknown, "Interrupted write must not be declared safe to replay.");
            }
            else
            {
                cancellation.Cancel();
                await RegressionCases.ThrowsAsync<SftpRequestCanceledException>(() => write.WaitAsync(ct));
            }
            RegressionCases.Check(session.IsFaulted, "Interrupted send did not fault the session.");
            var count = input.Written;
            await RegressionCases.ThrowsAsync<IOException>(() => session.ReadAsync([1], 0, 10, ct));
            RegressionCases.Check(input.Written == count, "A faulted sender accepted another request.");
        }

        await CheckQueuedCancellationAsync(ct);
        await CheckServerErrorAndIdMismatchAsync(ct);
        await CheckUnknownQueueOutcomeAsync(ct);
    }

    private static async Task CheckQueuedCancellationAsync(CancellationToken ct)
    {
        var input = new RecordingInput();
        var output = new GatedOutput(ScriptedSftp.Version(), ScriptedSftp.Handle(1).Concat(ScriptedSftp.Handle(2)).ToArray());
        await using var session = await SftpSession.ConnectAsync(input, output, ct);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await RegressionCases.ThrowsAsync<OperationCanceledException>(() => session.OpenAsync("pre-cancelled", 1, cancellation.Token));
        RegressionCases.Check(!session.IsFaulted && input.Written == 9, "Pre-send cancellation changed the session.");
        var first = session.OpenAsync("first", 1, ct);
        await output.Blocked.Task.WaitAsync(ct);
        using var queuedCancellation = new CancellationTokenSource();
        var second = session.OpenAsync("never-sent", 1, queuedCancellation.Token);
        queuedCancellation.Cancel();
        await RegressionCases.ThrowsAsync<OperationCanceledException>(() => second.WaitAsync(ct));
        RegressionCases.Check(!session.IsFaulted, "Cancellation while waiting for the request lock poisoned the active request.");
        output.Release.TrySetResult();
        await first.WaitAsync(ct);
        await session.OpenAsync("next", 1, ct);
        RegressionCases.Check(!session.IsFaulted, "Session could not be reused after queued cancellation.");
    }

    private static async Task CheckServerErrorAndIdMismatchAsync(CancellationToken ct)
    {
        var input = new RecordingInput();
        var replies = ScriptedSftp.Version().Concat(ScriptedSftp.Status(1, 3, "denied"))
            .Concat(ScriptedSftp.Handle(2)).Concat(ScriptedSftp.Handle(99)).ToArray();
        await using var session = await SftpSession.ConnectAsync(input, new MemoryStream(replies), ct);
        await RegressionCases.ThrowsAsync<IOException>(() => session.OpenAsync("denied", 1, ct));
        RegressionCases.Check(!session.IsFaulted, "A complete server error reply poisoned the stream.");
        await session.OpenAsync("accepted", 1, ct);
        await RegressionCases.ThrowsAsync<SftpRequestInterruptedException>(() => session.OpenAsync("wrong-id", 1, ct));
        RegressionCases.Check(session.IsFaulted, "Mismatching response ID was not fatal to the session.");
    }

    private static async Task CheckUnknownQueueOutcomeAsync(CancellationToken ct)
    {
        using var directory = new TestDirectory();
        using var local = new LocalFileSystem();
        var source = directory.File("source.txt");
        await File.WriteAllTextAsync(source, "retain me", ct);
        var destination = directory.File("destination.txt");
        await using var queue = new TransferQueue(1);
        var id = queue.Enqueue(new UnknownOutcomeFileSystem(local), source, local, destination, new(Move: true));
        while (queue.Snapshot().Single().State is TransferState.Queued or TransferState.Running)
            await Task.Delay(5, ct);
        var job = queue.Snapshot().Single();
        RegressionCases.Check(job.Id == id && job.State == TransferState.Partial && job.Message.Contains("Result unknown"),
            "Unconfirmed mutation became a clean cancellation or success.");
        RegressionCases.Check(File.Exists(source) && !File.Exists(destination), "Unknown result deleted source or committed output.");
    }

    private sealed class UnknownOutcomeFileSystem(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public override Task<FileEntry?> StatAsync(string path, CancellationToken ct) =>
            throw new SftpRequestCanceledException(1, 18, true, new OperationCanceledException(), ct);
    }

    private sealed class RecordingInput : MemoryStream
    {
        public int Written;
        public int FaultAfter = int.MaxValue;
        public bool ThrowIo;
        public bool Closed;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (Written + buffer.Length > FaultAfter)
            {
                Written = FaultAfter;
                Blocked.TrySetResult();
                if (ThrowIo) throw new IOException("Injected partial send failure.");
                await Task.Delay(Timeout.Infinite, ct);
            }
            Written += buffer.Length;
            await base.WriteAsync(buffer, ct);
        }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }

    private sealed class GatedOutput(byte[] prefix, byte[]? tail = null) : MemoryStream(prefix)
    {
        private readonly MemoryStream _tail = new(tail ?? []);
        public bool Closed;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, ct);
            Blocked.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return await _tail.ReadAsync(buffer, ct);
        }
        protected override void Dispose(bool disposing)
        {
            Closed = true;
            _tail.Dispose();
            base.Dispose(disposing);
        }
    }
}
