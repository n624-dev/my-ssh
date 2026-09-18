using System.Text;
using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class Issue06Tests : IRegressionCase
{
    public string Name => "#6 long Unicode and ASCII names use bounded temporary names and resume";

    public async Task RunAsync()
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        var sourceDir = root.File("src");
        var targetDir = root.File("dst");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(targetDir);
        var names = new[] { new string('a', 240), new string('日', 80), "space " + new string('b', 235) };
        foreach (var name in names)
        {
            var source = Path.Combine(sourceDir, name);
            var target = Path.Combine(targetDir, name);
            await File.WriteAllBytesAsync(source, new byte[700_001]);
            var monitor = new CapturingTarget(local);
            using (var stop = new CancellationTokenSource())
            {
                var progress = new ProgressInline(p => { if (p.BytesTransferred >= 256 * 1024) stop.Cancel(); });
                await RegressionCases.ThrowsAsync<OperationCanceledException>(() => new TransferEngine().CopyAsync(
                    local, source, monitor, target, new(), progress, stop.Token));
            }
            RegressionCases.Check(monitor.LastWrite is not null &&
                Encoding.UTF8.GetByteCount(Path.GetFileName(monitor.LastWrite)) <= 200, "temporary name is too long");
            await new TransferEngine().CopyAsync(local, source, monitor, target, new(), null, CancellationToken.None);
            RegressionCases.Check(new FileInfo(target).Length == 700_001, "long-name resume failed");
            var edit = await EditSession.OpenAsync(local, source, root.File("drafts"), "LOCAL", CancellationToken.None);
            RegressionCases.Check(Path.GetFileName(edit.DraftPath).Length < 32, "edit draft repeats the long name");
            edit.Discard();
        }
        if (!OperatingSystem.IsWindows())
        {
            var name = new string('l', 240);
            var link = Path.Combine(sourceDir, name);
            File.CreateSymbolicLink(link, names[0]);
            await new TransferEngine().CopyAsync(local, link, local, Path.Combine(targetDir, name), new(), null, CancellationToken.None);
            RegressionCases.Check(new FileInfo(Path.Combine(targetDir, name)).LinkTarget == names[0], "long symlink transfer failed");
        }
        if (Environment.GetEnvironmentVariable("MYSSH_TEST_HOST") is { Length: > 0 } host)
            await RemoteRoundTrip(root, local, sourceDir, host, names);
    }

    private static async Task RemoteRoundTrip(TestDirectory root, LocalFileSystem local,
        string sourceDir, string host, string[] names)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = deadline.Token;
        await using var remote = await SftpFileSystem.ConnectAsync(new(host,
            Environment.GetEnvironmentVariable("MYSSH_TEST_USER")!), ct);
        var directory = remote.Join(await remote.CanonicalAsync(Environment.GetEnvironmentVariable("MYSSH_TEST_ROOT")!, ct),
            "long-names-" + Guid.NewGuid().ToString("N"));
        await remote.CreateDirectoryAsync(directory, ct);
        var downloadDir = root.File("download");
        Directory.CreateDirectory(downloadDir);
        try
        {
            foreach (var name in names)
            {
                var destination = remote.Join(directory, name);
                await new TransferEngine().CopyAsync(local, Path.Combine(sourceDir, name), remote, destination, new(), null, ct);
                await new TransferEngine().CopyAsync(remote, destination, local, Path.Combine(downloadDir, name), new(), null, ct);
                RegressionCases.Check(new FileInfo(Path.Combine(downloadDir, name)).Length == 700_001, "SFTP long-name round trip failed");
                await remote.DeleteAsync(destination, false, ct);
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            foreach (var entry in await remote.ListAsync(directory, cleanup.Token)) await remote.DeleteAsync(entry.Path, false, cleanup.Token);
            await remote.DeleteAsync(directory, true, cleanup.Token);
        }
    }

    private sealed class CapturingTarget(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public string? LastWrite { get; private set; }
        public override Task<Stream> OpenWriteAsync(string path, bool createNew, CancellationToken ct)
        {
            LastWrite = path;
            return base.OpenWriteAsync(path, createNew, ct);
        }
    }
    private sealed class ProgressInline(Action<TransferProgress> action) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => action(value);
    }
}
