using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class Issue32Tests : IRegressionCase
{
    public string Name => "#32 durable queues recover paused jobs and verified receipts without automatic replay";

    public async Task RunAsync()
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        var source = root.File("source");
        var destination = root.File("destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "acknowledged first file");
        var payload = Enumerable.Range(0, 900_000).Select(x => (byte)(x % 251)).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(source, "b.bin"), payload);
        var journalRoot = root.File("journal");
        var journal = new TransferJournal(journalRoot, "user@fixture");
        var gate = new BlockAfterWrite(local, Path.Combine(destination, "a.txt"));
        var queue = new TransferQueue(1, journal, local, local);
        Guid id;
        try
        {
            id = queue.Enqueue(local, source, gate, destination, new());
            await gate.Written.Task.WaitAsync(TimeSpan.FromSeconds(15));
            // A simultaneous window cannot restore or execute an actively owned job.
            await using (var other = new TransferQueue(1, new TransferJournal(journalRoot, "user@fixture"), local, local))
            {
                RegressionCases.Check(other.Snapshot().Count == 0 && other.RecoveryWarnings.Count == 1,
                    "A second window took ownership of an active transfer.");
            }
            RegressionCases.Check(queue.Pause(id), "The running transfer could not be paused.");
            await WaitState(queue, id, TransferState.Paused);
        }
        finally { await queue.DisposeAsync(); }

        var partial = Directory.GetFiles(destination).Single(path => Path.GetFileName(path) != "a.txt");
        RegressionCases.Check(new FileInfo(partial).Length > 0, "No real partial data was retained.");
        await using (var restored = new TransferQueue(1, new TransferJournal(journalRoot, "user@fixture"), local, local))
        {
            var job = restored.Snapshot().Single();
            RegressionCases.Check(job.Id == id && job.State == TransferState.Paused && !File.Exists(Path.Combine(destination, "b.bin")),
                "Recovery started work or lost the job identity.");
            await Task.Delay(40);
            RegressionCases.Check(restored.Snapshot().Single().State == TransferState.Paused, "Recovery automatically resumed a job.");
            RegressionCases.Check(restored.Resume(id), "The recovered job could not be resumed explicitly.");
            await WaitState(restored, id, TransferState.Completed);
            RegressionCases.Check((await File.ReadAllBytesAsync(Path.Combine(destination, "b.bin"))).SequenceEqual(payload),
                "Recovered partial data did not produce the original file.");
            RegressionCases.Check(await File.ReadAllTextAsync(Path.Combine(destination, "a.txt")) == "acknowledged first file",
                "An acknowledged file was lost while restoring its receipt.");
            RegressionCases.Check(restored.Remove(id), "Finished job removal failed.");
        }
        await using (var empty = new TransferQueue(1, new TransferJournal(journalRoot, "user@fixture"), local, local))
            RegressionCases.Check(empty.Snapshot().Count == 0, "Removed jobs returned after reopening.");

        await CheckInterruptedAndCorruptAsync(root, local);
        await CheckCleanupCheckpointAsync(root, local);
    }

    private static async Task WaitState(TransferQueue queue, Guid id, TransferState expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var job = queue.Snapshot().Single(x => x.Id == id);
            if (job.State == expected) return;
            if (job.State is TransferState.Failed or TransferState.Partial or TransferState.Cancelled)
                throw new IOException("Unexpected transfer state: " + job.State + " " + job.Message);
            await Task.Delay(10, deadline.Token);
        }
    }

    private static async Task CheckInterruptedAndCorruptAsync(TestDirectory root, LocalFileSystem local)
    {
        var source = root.File("not-deleted.txt");
        var destination = root.File("copy.txt");
        await File.WriteAllTextAsync(source, "must remain");
        var journalRoot = root.File("interrupted");
        var id = Guid.NewGuid();
        var cleanupId = Guid.NewGuid();
        var corruptId = Guid.NewGuid();
        string corruptPath;
        using (var journal = new TransferJournal(journalRoot, "fixture"))
        {
            var snapshot = new TransferJobSnapshot(id, source, false, destination, false, false,
                TransferState.Running, 0, null, 0, 0, "Running at interruption", DateTimeOffset.UtcNow, null, null);
            var receipt = new TransferResumeState.ResumeData(new("local", source, "local", destination), [], [], false);
            journal.Claim(id);
            journal.Save(new(snapshot, new(), receipt));
            journal.Claim(cleanupId);
            journal.Save(new(snapshot with { Id = cleanupId, Move = true }, new(Move: true), receipt with { SourceCleanupStarted = true }));
            corruptPath = Path.Combine(journal.DirectoryPath, corruptId.ToString("N") + ".json");
            await File.WriteAllTextAsync(corruptPath, "{truncated checkpoint");
        }
        await using var recovered = new TransferQueue(1, new TransferJournal(journalRoot, "fixture"), local, local);
        RegressionCases.Check(recovered.Snapshot().Single(x => x.Id == id).State == TransferState.Paused,
            "Interrupted Running state was not converted to Paused.");
        RegressionCases.Check(recovered.Snapshot().Single(x => x.Id == cleanupId).State == TransferState.Partial &&
            !recovered.Retry(cleanupId) && !recovered.Resume(cleanupId), "Interrupted source deletion can be replayed.");
        RegressionCases.Check(!File.Exists(destination) && await File.ReadAllTextAsync(source) == "must remain",
            "Loading a checkpoint mutated source or destination.");
        RegressionCases.Check(recovered.RecoveryWarnings.Count == 1 && await File.ReadAllTextAsync(corruptPath) == "{truncated checkpoint",
            "Corrupt state was executed or silently overwritten.");
    }

    private static async Task CheckCleanupCheckpointAsync(TestDirectory root, LocalFileSystem local)
    {
        var source = root.File("move.txt");
        var destination = root.File("moved.txt");
        await File.WriteAllTextAsync(source, "checkpoint before deletion");
        var resume = new TransferResumeState();
        resume.Checkpoint = () =>
        {
            if (resume.Capture().SourceCleanupStarted) throw new IOException("Injected durable checkpoint failure");
        };
        await RegressionCases.ThrowsAsync<PartialMoveException>(() => new TransferEngine().CopyAsync(local, source, local, destination,
            new(Move: true), null, CancellationToken.None, resume));
        RegressionCases.Check(File.Exists(source) && File.Exists(destination), "Move deleted its source before persisting cleanup intent.");
    }

    private sealed class BlockAfterWrite(IFileSystem inner, string acknowledgedPath) : DelegatingFileSystem(inner)
    {
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<Stream> OpenWriteAsync(string path, bool createNew, CancellationToken ct)
        {
            var output = await base.OpenWriteAsync(path, createNew, ct);
            return File.Exists(acknowledgedPath) ? new BlockingStream(output, Written) : output;
        }
    }

    private sealed class BlockingStream(Stream inner, TaskCompletionSource written) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct);
            await inner.FlushAsync(ct);
            written.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
