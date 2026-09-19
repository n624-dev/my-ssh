using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class Issue15Tests : IRegressionCase
{
    public string Name => "#15 directory resume verifies per-job commits instead of conflicting with itself";

    public async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await CheckQueuePauseResumeAsync(ct);
        foreach (var changedSide in new[] { "none", "source", "destination" })
            await CheckContentValidationAsync(changedSide, ct);
    }

    private static async Task CheckQueuePauseResumeAsync(CancellationToken ct)
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        var source = root.File("source");
        var destination = root.File("destination");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "already copied", ct);
        await File.WriteAllBytesAsync(Path.Combine(source, "b.bin"), new byte[700_001], ct);
        var gated = new GatedSource(local, Path.Combine(source, "b.bin"));
        var counted = new CountingDestination(local);
        await using var queue = new TransferQueue(1);
        var id = queue.Enqueue(gated, source, counted, destination, new());
        await gated.Entered.Task.WaitAsync(ct);
        RegressionCases.Check(counted.FirstCommits == 1, "First file was not committed before the pause.");
        RegressionCases.Check(queue.Pause(id), "Running directory could not be paused.");
        await WaitAsync(queue, TransferState.Paused, ct);
        gated.Release.TrySetResult();
        RegressionCases.Check(queue.Resume(id), "Paused directory could not be resumed.");
        await WaitAsync(queue, TransferState.Completed, ct);
        var result = queue.Snapshot().Single();
        RegressionCases.Check(counted.FirstCommits == 1, "Resume overwrote its previously-completed first file.");
        RegressionCases.Check(result.BytesTransferred == result.TotalBytes && result.CompletedEntries == result.TotalEntries,
            "Resume progress did not include verified completed entries.");
        RegressionCases.Check(await File.ReadAllTextAsync(Path.Combine(destination, "a.txt"), ct) == "already copied",
            "First destination changed during resume.");
    }

    private static async Task CheckContentValidationAsync(string changedSide, CancellationToken ct)
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        var source = root.File("source");
        var destination = root.File("destination");
        Directory.CreateDirectory(source);
        var first = Path.Combine(source, "a.txt");
        var second = Path.Combine(source, "b.bin");
        await File.WriteAllTextAsync(first, "original", ct);
        await File.WriteAllBytesAsync(second, new byte[800_003], ct);
        var engine = new TransferEngine();
        var resume = new TransferResumeState();
        using (var interrupted = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var progress = new ImmediateProgress(p =>
            {
                if (p.Source == second && p.BytesTransferred > 8) interrupted.Cancel();
            });
            await RegressionCases.ThrowsAsync<OperationCanceledException>(() => engine.CopyAsync(local, source, local,
                destination, new(), progress, interrupted.Token, resume));
        }
        var copiedFirst = Path.Combine(destination, "a.txt");
        RegressionCases.Check(File.Exists(copiedFirst) && !File.Exists(Path.Combine(destination, "b.bin")),
            "Test did not interrupt after one acknowledged commit.");

        if (changedSide != "none")
        {
            var changed = changedSide == "source" ? first : copiedFirst;
            var stamp = File.GetLastWriteTimeUtc(changed);
            await File.WriteAllTextAsync(changed, "modified", ct);
            File.SetLastWriteTimeUtc(changed, stamp); // Same size/time: requires content verification.
            await RegressionCases.ThrowsAsync<TransferConflictException>(() => engine.CopyAsync(local, source, local,
                destination, new(), null, ct, resume));
            RegressionCases.Check(await File.ReadAllTextAsync(changed, ct) == "modified", "Resume silently replaced a changed file.");
            await engine.CopyAsync(local, source, local, destination,
                new(Conflict: ConflictAction.Overwrite), null, ct, resume);
        }
        else
        {
            await engine.CopyAsync(local, source, local, destination, new(), null, ct, resume);
        }
        RegressionCases.Check((await File.ReadAllBytesAsync(second, ct)).SequenceEqual(
            await File.ReadAllBytesAsync(Path.Combine(destination, "b.bin"), ct)), "Resumed file contents differ.");
        RegressionCases.Check(await File.ReadAllTextAsync(first, ct) == await File.ReadAllTextAsync(copiedFirst, ct),
            "Explicit conflict handling did not copy the current source.");
        await RegressionCases.ThrowsAsync<InvalidOperationException>(() => engine.CopyAsync(local, source, local,
            root.File("different-destination"), new(), null, ct, resume));
        RegressionCases.Check(!Directory.Exists(root.File("different-destination")), "Receipt state was reused for another destination.");
    }

    private static async Task WaitAsync(TransferQueue queue, TransferState expected, CancellationToken ct)
    {
        while (true)
        {
            var job = queue.Snapshot().Single();
            if (job.State == expected) return;
            if (job.State is TransferState.Failed or TransferState.Partial or TransferState.Cancelled)
                throw new InvalidOperationException(job.Message);
            await Task.Delay(5, ct);
        }
    }

    private sealed class ImmediateProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }

    private sealed class GatedSource(IFileSystem inner, string gatedPath) : DelegatingFileSystem(inner)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
        {
            if (path == gatedPath)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return await base.OpenReadAsync(path, ct);
        }
    }

    private sealed class CountingDestination(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public int FirstCommits;
        public override async Task RenameAsync(string source, string destination, bool replace, CancellationToken ct)
        {
            await base.RenameAsync(source, destination, replace, ct);
            if (Path.GetFileName(destination) == "a.txt") FirstCommits++;
        }
    }
}
