using MySsh.Core;
using MySsh.Infrastructure;

internal static class SftpIntegration
{
    public static async Task RunAsync()
    {
        var connection = new Connection(Required("MYSSH_TEST_HOST"), Required("MYSSH_TEST_USER"));
        var fixture = Required("MYSSH_TEST_ROOT");
        var localRoot = Path.Combine(Path.GetTempPath(), "my-ssh-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localRoot);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = deadline.Token;
        using var local = new LocalFileSystem();
        await using var remote = await SftpFileSystem.ConnectAsync(connection, ct);
        var remoteRoot = remote.Join(await remote.CanonicalAsync(fixture, ct), "run-" + Guid.NewGuid().ToString("N"));
        await remote.CreateDirectoryAsync(remoteRoot, ct);
        try
        {
            await VerifyUiContextAsync(connection, remoteRoot, ct);
            var engine = new TransferEngine();
            // Larger than the old 256 KiB WRITE packet, with a non-aligned final chunk.
            var bytes = new byte[1024 * 1024 + 123];
            new Random(42).NextBytes(bytes);
            var source = Path.Combine(localRoot, "sample space 日本語.bin");
            var destination = remote.Join(remoteRoot, Path.GetFileName(source));
            await File.WriteAllBytesAsync(source, bytes, ct);
            await engine.CopyAsync(local, source, remote, destination, new(), null, ct);
            var download = Path.Combine(localRoot, "download.bin");
            await engine.CopyAsync(remote, destination, local, download, new(), null, ct);
            var downloadedBytes = await File.ReadAllBytesAsync(download, ct);
            Check(bytes.SequenceEqual(downloadedBytes), "round trip differs");
            Check((await remote.ListAsync(remoteRoot, ct)).Any(x => x.Name == Path.GetFileName(source)), "Unicode listing failed");
            Console.WriteLine("PASS SFTP >1 MiB upload/download, Unicode and spaces");

            var conflict = false;
            try { await engine.CopyAsync(local, source, remote, destination, new(), null, ct); }
            catch (TransferConflictException) { conflict = true; }
            Check(conflict, "existing destination was silently replaced");
            bytes[0] ^= 0x55;
            await File.WriteAllBytesAsync(source, bytes, ct);
            await engine.CopyAsync(local, source, remote, destination, new(Conflict: ConflictAction.Overwrite), null, ct);
            await engine.CopyAsync(remote, destination, local, download, new(Conflict: ConflictAction.Overwrite), null, ct);
            downloadedBytes = await File.ReadAllBytesAsync(download, ct);
            Check(bytes.SequenceEqual(downloadedBytes), "atomic overwrite differs");
            Console.WriteLine("PASS SFTP conflict and POSIX atomic overwrite");

            var tree = Path.Combine(localRoot, "tree");
            Directory.CreateDirectory(Path.Combine(tree, "nested"));
            await File.WriteAllTextAsync(Path.Combine(tree, "nested", "file.txt"), "nested contents", ct);
            var remoteTree = remote.Join(remoteRoot, "tree");
            await engine.CopyAsync(local, tree, remote, remoteTree, new(), null, ct);
            var treeDownload = Path.Combine(localRoot, "tree-download");
            await engine.CopyAsync(remote, remoteTree, local, treeDownload, new(), null, ct);
            Check(await File.ReadAllTextAsync(Path.Combine(treeDownload, "nested", "file.txt"), ct) == "nested contents", "recursive transfer differs");

            var renamed = remote.Join(remoteRoot, "renamed.bin");
            await remote.RenameAsync(destination, renamed, false, ct);
            var link = remote.Join(remoteRoot, "symbolic-link");
            await remote.CreateLinkAsync(link, "renamed.bin", ct);
            Check(await remote.ReadLinkAsync(link, ct) == "renamed.bin", "link target differs");
            await remote.DeleteAsync(link, false, ct);
            Check(await remote.StatAsync(renamed, ct) is { Kind: EntryKind.File }, "deleting link removed target");
            await remote.SetMetadataAsync(renamed, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), 0x180, ct);
            var metadata = await remote.StatAsync(renamed, ct);
            Check((metadata!.Mode & 0x1FF) == 0x180, "chmod failed");
            Console.WriteLine("PASS SFTP recursive copy, rename, links, metadata");

            var resumable = remote.Join(remoteRoot, "resume.bin");
            using (var interruption = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                await using var interrupted = await SftpFileSystem.ConnectAsync(connection, ct);
                var progress = new ImmediateProgress(p =>
                {
                    if (p.BytesTransferred >= 256 * 1024) interruption.Cancel();
                });
                var cancelled = false;
                try { await engine.CopyAsync(local, source, interrupted, resumable, new(), progress, interruption.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "interruption did not cancel transfer");
            }
            Check(await remote.StatAsync(resumable, ct) is null, "cancelled upload was committed");
            Check((await remote.ListAsync(remoteRoot, ct)).Any(x => x.Name.StartsWith(".resume.bin.my-ssh-part-")), "partial upload missing");
            await using (var reconnected = await SftpFileSystem.ConnectAsync(connection, ct))
                await engine.CopyAsync(local, source, reconnected, resumable, new(), null, ct);
            var resumedDownload = Path.Combine(localRoot, "resumed.bin");
            await engine.CopyAsync(remote, resumable, local, resumedDownload, new(), null, ct);
            downloadedBytes = await File.ReadAllBytesAsync(resumedDownload, ct);
            Check(bytes.SequenceEqual(downloadedBytes), "resumed upload differs");
            Console.WriteLine("PASS SFTP interrupted upload resumed through a fresh SSH session");

            // A mismatching partial file must never be trusted or committed.
            var corruptDestination = remote.Join(remoteRoot, "corrupt.bin");
            using (var interruption = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                await using var interrupted = await SftpFileSystem.ConnectAsync(connection, ct);
                try
                {
                    await engine.CopyAsync(local, source, interrupted, corruptDestination, new(),
                        new ImmediateProgress(p => { if (p.BytesTransferred >= 256 * 1024) interruption.Cancel(); }), interruption.Token);
                }
                catch (OperationCanceledException) { }
            }
            var partial = (await remote.ListAsync(remoteRoot, ct)).Single(x => x.Name.StartsWith(".corrupt.bin.my-ssh-part-"));
            await using (var writer = await remote.OpenWriteAsync(partial.Path, false, ct))
                await writer.WriteAsync(new byte[] { (byte)(bytes[0] ^ 0xFF) }, ct);
            var rejected = false;
            try { await engine.CopyAsync(local, source, remote, corruptDestination, new(), null, ct); }
            catch (IOException ex) when (ex.Message.Contains("does not match")) { rejected = true; }
            Check(rejected && await remote.StatAsync(corruptDestination, ct) is null, "corrupt partial was accepted");
            Console.WriteLine("PASS SFTP corrupted resume prefix is rejected");

            var moveSource = Path.Combine(localRoot, "move.txt");
            await File.WriteAllTextAsync(moveSource, "move contents", ct);
            var moved = remote.Join(remoteRoot, "moved.txt");
            await engine.CopyAsync(local, moveSource, remote, moved, new(Move: true), null, ct);
            Check(!File.Exists(moveSource) && await remote.StatAsync(moved, ct) is not null, "move failed");
            Console.WriteLine("PASS SFTP move deletes source after commit");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await DeleteTreeAsync(remote, remoteRoot, cleanup.Token);
            Directory.Delete(localRoot, true);
        }
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing integration setting: {name}");
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static async Task DeleteTreeAsync(IFileSystem fs, string path, CancellationToken ct)
    {
        var entry = await fs.StatAsync(path, ct);
        if (entry is null) return;
        if (entry.Kind == EntryKind.Directory)
            foreach (var child in await fs.ListAsync(path, ct)) await DeleteTreeAsync(fs, child.Path, ct);
        await fs.DeleteAsync(path, entry.Kind == EntryKind.Directory, ct);
    }
    private sealed class ImmediateProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }

    private static async Task VerifyUiContextAsync(Connection connection, string directory, CancellationToken ct)
    {
        // The browser synchronously waits for filesystem calls on the UI thread.
        // Transport continuations must not depend on that blocked context.
        await Task.Run(() =>
        {
            var previous = SynchronizationContext.Current;
            var context = new RecordingContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var fs = SftpFileSystem.ConnectAsync(connection, ct).GetAwaiter().GetResult();
                try
                {
                    fs.CanonicalAsync(directory, ct).GetAwaiter().GetResult();
                    fs.ListAsync(directory, ct).GetAwaiter().GetResult();
                    var file = fs.Join(directory, "ui-context.txt");
                    using (var output = fs.OpenWriteAsync(file, true, ct).GetAwaiter().GetResult())
                        output.Write(new byte[] { 1, 2, 3 });
                    using (var input = fs.OpenReadAsync(file, ct).GetAwaiter().GetResult())
                        Check(input.ReadByte() == 1, "UI context read failed");
                    fs.DeleteAsync(file, false, ct).GetAwaiter().GetResult();
                }
                finally { fs.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                Check(context.Posts == 0, "SFTP attempted to resume on the blocked UI context");
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }, ct);
        Console.WriteLine("PASS SFTP browser operations do not capture the UI context");
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref Posts);
            // Keep regressions from hanging the runner; the assertion still fails.
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }
}
