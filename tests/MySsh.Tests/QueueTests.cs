using MySsh.Core;
using MySsh.Infrastructure;

internal static class QueueTests
{
    public static async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        var root = Path.Combine(Path.GetTempPath(), "my-ssh-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var local = new LocalFileSystem();
            var source = Path.Combine(root, "source.bin");
            await File.WriteAllBytesAsync(source, new byte[700_000], ct);
            var blocked = new GatedFileSystem(local);
            await using (var queue = new TransferQueue(1))
            {
                var id = queue.Enqueue(blocked, source, local, Path.Combine(root, "paused.bin"), new());
                await blocked.Entered.Task.WaitAsync(ct);
                Check(queue.Pause(id), "running pause rejected");
                await WaitForState(queue, id, TransferState.Paused, ct);
                Check(queue.Cancel(id), "paused cancel rejected");
                Check(queue.Snapshot().Single().State == TransferState.Cancelled, "paused job stayed paused after cancel");
                blocked.Release.TrySetResult();
                Check(queue.Retry(id), "cancelled retry rejected");
                await WaitForState(queue, id, TransferState.Completed, ct);
                Check(queue.Snapshot().Single().BytesTransferred == 700_000, "final progress was stale");
                Check(queue.Remove(id), "completed job not removable");
            }
            Console.WriteLine("PASS queue pause, cancel while paused, retry and final progress");

            var gate = new GatedFileSystem(local);
            var shuttingDown = new TransferQueue(1);
            var active = shuttingDown.Enqueue(gate, source, local, Path.Combine(root, "active.bin"), new());
            await gate.Entered.Task.WaitAsync(ct);
            var waiting = shuttingDown.Enqueue(local, source, local, Path.Combine(root, "queued.bin"), new());
            await shuttingDown.DisposeAsync().AsTask().WaitAsync(ct);
            Check(shuttingDown.Snapshot().All(x => x.State == TransferState.Cancelled), "shutdown left active or queued workers");
            Check(!File.Exists(Path.Combine(root, "queued.bin")), "queued transfer ran after shutdown");
            var rejected = false;
            try { shuttingDown.Enqueue(local, source, local, Path.Combine(root, "after.bin"), new()); }
            catch (ObjectDisposedException) { rejected = true; }
            Check(rejected, "disposed queue accepted new work");
            Console.WriteLine("PASS queue shutdown awaits running and queued workers");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task WaitForState(TransferQueue queue, Guid id, TransferState state, CancellationToken ct)
    {
        while (queue.Snapshot().Single(x => x.Id == id).State != state)
        {
            var current = queue.Snapshot().Single(x => x.Id == id);
            if (current.State is TransferState.Failed or TransferState.Partial)
                throw new InvalidOperationException(current.Message);
            await Task.Delay(10, ct);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class GatedFileSystem(IFileSystem inner) : IFileSystem
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsRemote => inner.IsRemote;
        public StringComparison PathComparison => inner.PathComparison;
        public string Join(string directory, string name) => inner.Join(directory, name);
        public string Parent(string path) => inner.Parent(path);
        public Task<string> CanonicalAsync(string path, CancellationToken ct) => inner.CanonicalAsync(path, ct);
        public async Task<FileEntry?> StatAsync(string path, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return await inner.StatAsync(path, ct);
        }
        public Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct) => inner.ListAsync(path, ct);
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
        public Task<Stream> OpenWriteAsync(string path, bool createNew, CancellationToken ct) => inner.OpenWriteAsync(path, createNew, ct);
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public Task RenameAsync(string source, string destination, bool replace, CancellationToken ct) => inner.RenameAsync(source, destination, replace, ct);
        public Task DeleteAsync(string path, bool directory, CancellationToken ct) => inner.DeleteAsync(path, directory, ct);
        public Task SetMetadataAsync(string path, DateTimeOffset modified, uint? mode, CancellationToken ct) => inner.SetMetadataAsync(path, modified, mode, ct);
        public Task<string> ReadLinkAsync(string path, CancellationToken ct) => inner.ReadLinkAsync(path, ct);
        public Task CreateLinkAsync(string path, string target, CancellationToken ct) => inner.CreateLinkAsync(path, target, ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
