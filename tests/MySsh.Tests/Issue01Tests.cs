using System.Diagnostics;
using MySsh.Core;
using MySsh.Infrastructure;
using static RegressionCases;

internal sealed class Issue01Tests : IRegressionCase
{
    public string Name => "#1 SFTP close acknowledgement and move source retention";

    public async Task RunAsync()
    {
        foreach (var synchronous in new[] { false, true })
        {
            await using var fs = await ScriptedSftp.ConnectAsync(ScriptedSftp.Version(),
                ScriptedSftp.Handle(1), ScriptedSftp.Status(2, 0), ScriptedSftp.Status(3, 4, "close rejected"));
            var output = await fs.OpenWriteAsync("partial", true, CancellationToken.None);
            await output.WriteAsync(new byte[] { 1, 2, 3 });
            var error = await ThrowsAsync<IOException>(async () =>
            {
                if (synchronous) output.Dispose();
                else await output.DisposeAsync();
            });
            Check(error.Message.Contains("close", StringComparison.OrdinalIgnoreCase), "Close failure was hidden.");
        }

        using var directory = new TestDirectory();
        using var local = new LocalFileSystem();
        var source = directory.File("source.txt");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        foreach (var writeFails in new[] { false, true })
        {
            await using var fs = await ScriptedSftp.ConnectAsync(ScriptedSftp.Version(),
                ScriptedSftp.Handle(1), ScriptedSftp.Status(2, writeFails ? 4u : 0u, "write rejected"),
                ScriptedSftp.Status(3, 4, "close rejected"));
            var destination = new StreamDestination(local, fs);
            var error = await ThrowsAsync<IOException>(() => new TransferEngine().CopyAsync(
                local, source, destination, directory.File("destination.txt"),
                new(Move: true), null, CancellationToken.None));
            Check(File.Exists(source), "A failed close removed the move source.");
            Check(destination.Renames == 0, "A failed close committed the destination.");
            if (writeFails) Check(error.Message.Contains("write rejected"), "Cleanup hid the original write failure.");
        }

        await using (var fs = await ScriptedSftp.ConnectAsync(ScriptedSftp.Version(),
            ScriptedSftp.Handle(1), ScriptedSftp.Status(2, 0), ScriptedSftp.Status(3, 0)))
        {
            var output = await fs.OpenWriteAsync("partial", true, CancellationToken.None);
            await output.WriteAsync(new byte[] { 1, 2, 3 });
            await output.DisposeAsync();
            await output.DisposeAsync(); // CLOSE is sent at most once.
        }

        var watch = Stopwatch.StartNew();
        var responses = new StallingTailStream(ScriptedSftp.Version().Concat(ScriptedSftp.Handle(1)).ToArray());
        await using var session = await SftpSession.ConnectAsync(new MemoryStream(), responses, CancellationToken.None);
        await using var stalled = new SftpFileSystem(new Connection("scripted.test", "test"), session);
        var stream = await stalled.OpenWriteAsync("partial", true, CancellationToken.None);
        await ThrowsAsync<IOException>(() => stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8)));
        Check(watch.Elapsed < TimeSpan.FromSeconds(8), "Write close cleanup was unbounded.");
    }

    private sealed class StreamDestination(IFileSystem local, IFileSystem remote) : DelegatingFileSystem(local)
    {
        public int Renames;
        public override Task<Stream> OpenWriteAsync(string path, bool createNew, CancellationToken ct) => remote.OpenWriteAsync(path, createNew, ct);
        public override Task RenameAsync(string source, string destination, bool replace, CancellationToken ct)
        {
            Renames++;
            return base.RenameAsync(source, destination, replace, ct);
        }
    }

    private sealed class StallingTailStream(byte[] replies) : MemoryStream(replies)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
