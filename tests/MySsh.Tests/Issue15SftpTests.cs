using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class Issue15SftpTests : IRegressionCase
{
    public string Name => "#15 directory resume across fresh loopback SFTP sessions";

    public async Task RunAsync()
    {
        if (Environment.GetEnvironmentVariable("MYSSH_TEST_HOST") is not { Length: > 0 } host) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var ct = deadline.Token;
        var connection = new Connection(host, Environment.GetEnvironmentVariable("MYSSH_TEST_USER")!);
        await using var remote = await SftpFileSystem.ConnectAsync(connection, ct);
        var root = remote.Join(await remote.CanonicalAsync(Environment.GetEnvironmentVariable("MYSSH_TEST_ROOT")!, ct),
            "resume15-" + Guid.NewGuid().ToString("N"));
        using var localRoot = new TestDirectory();
        using var local = new LocalFileSystem();
        var source = localRoot.File("tree");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "original", ct);
        var bytes = new byte[700_003];
        new Random(15).NextBytes(bytes);
        var second = Path.Combine(source, "b.bin");
        await File.WriteAllBytesAsync(second, bytes, ct);
        var state = new TransferResumeState();
        var engine = new TransferEngine();
        try
        {
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var progress = new ImmediateProgress(p =>
                {
                    if (p.Source == second && p.BytesTransferred > 8) stop.Cancel();
                });
                await RegressionCases.ThrowsAsync<OperationCanceledException>(() =>
                    engine.CopyAsync(local, source, remote, root, new(Move: true), progress, stop.Token, state));
            }
            RegressionCases.Check(File.Exists(Path.Combine(source, "a.txt")), "Interrupted move removed a source.");
            await using var reconnected = await remote.CreateSiblingAsync(ct);
            await engine.CopyAsync(local, source, reconnected, root, new(Move: true), null, ct, state);
            RegressionCases.Check(!Directory.Exists(source), "Verified resumed move did not remove its source tree.");
            var download = localRoot.File("download.bin");
            await engine.CopyAsync(reconnected, reconnected.Join(root, "b.bin"), local, download, new(), null, ct);
            var downloadedBytes = await File.ReadAllBytesAsync(download, ct);
            RegressionCases.Check(bytes.SequenceEqual(downloadedBytes), "Resumed SFTP contents differ.");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (await remote.StatAsync(root, cleanup.Token) is not null)
            {
                foreach (var entry in await remote.ListAsync(root, cleanup.Token))
                    await remote.DeleteAsync(entry.Path, false, cleanup.Token);
                await remote.DeleteAsync(root, true, cleanup.Token);
            }
        }
    }

    private sealed class ImmediateProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }
}
