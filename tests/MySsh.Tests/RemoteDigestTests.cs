using System.Diagnostics;
using System.Security.Cryptography;
using MySsh.App;
using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class RemoteDigestTests : IRegressionCase
{
    public string Name => "remote checksums avoid rereading over SFTP; fallback verification reports its own percent and speed";
    private static string ValidOutput => new string('a', 64) + "  -\nmy-ssh-sha256-v1\n";

    public async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var ct = deadline.Token;
        RegressionCases.Check(RemoteFileDigest.Parse(ValidOutput, 0)?.Length == 32, "Valid digest rejected.");
        foreach (var output in new[] { ValidOutput.Replace('a', 'z'), "banner\n" + ValidOutput,
            ValidOutput + "extra", new string('a', 64), ValidOutput.Replace("  -", "  file") })
            RegressionCases.Check(RemoteFileDigest.Parse(output, 0) is null, "Malformed digest accepted.");
        RegressionCases.Check(RemoteFileDigest.Parse(ValidOutput, 1) is null, "Failed command accepted.");
        foreach (var mode in new[] { "ok", "stderr", "failure", "overflow" })
        {
            var watch = Stopwatch.StartNew();
            var result = await RemoteFileDigest.RunAsync(Helper(mode), TimeSpan.FromSeconds(8), ct);
            RegressionCases.Check((result is not null) == (mode is "ok" or "stderr"), "Wrong helper result: " + mode);
            RegressionCases.Check(watch.Elapsed < TimeSpan.FromSeconds(6), "Helper output blocked cleanup.");
        }
        RegressionCases.Check(await RemoteFileDigest.RunAsync(Helper("wait"), TimeSpan.FromMilliseconds(300), ct) is null,
            "Timed out optional checksum did not permit fallback.");
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cancel.CancelAfter(300);
            await RegressionCases.ThrowsAsync<OperationCanceledException>(() =>
                RemoteFileDigest.RunAsync(Helper("wait"), TimeSpan.FromSeconds(8), cancel.Token));
        }
        await CopyAsync(false, ct);
        await CopyAsync(true, ct);
        await RejectChangedAsync(ct);

        const string phase = "Verifying source: reading via SFTP 37.5% (12.3 MB/s)";
        var job = new TransferJobSnapshot(Guid.NewGuid(), "/long-name", true, "target", false, false,
            TransferState.Running, 1_000_000, 1_000_000, 0, 1, phase, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(-1), null);
        var row = FileManagerWindow.QueueText(job);
        RegressionCases.Check(row.StartsWith("Running   " + phase) && !row.Contains("100.0%") && !row.Contains("ETA"),
            "Verification was hidden behind transfer percentage or stale transfer speed.");
    }

    private static async Task CopyAsync(bool unavailable, CancellationToken ct)
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        var path = root.File("source");
        var target = root.File("copy");
        var bytes = new byte[RemoteFileDigest.MinimumLength + 17];
        new Random(90).NextBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes, ct);
        var source = new DigestFileSystem(local) { Unavailable = unavailable };
        var progress = new List<TransferProgress>();
        await new TransferEngine().CopyAsync(source, path, local, target, new(), new ProgressSink(progress.Add), ct);
        RegressionCases.Check((await File.ReadAllBytesAsync(target, ct)).SequenceEqual(bytes), "Checksum copy differs.");
        RegressionCases.Check(source.DigestCalls == 1 && source.Reads == (unavailable ? 2 : 1),
            "Checksum optimization either skipped fallback verification or downloaded the source twice.");
        RegressionCases.Check(progress.Any(p => p.Message.StartsWith("Verifying destination:") && p.Message.Contains("MB/s")),
            "Local verification did not report measured progress.");
        if (unavailable)
            RegressionCases.Check(progress.Any(p => p.Message.StartsWith("Verifying source: reading via SFTP") &&
                p.Message.Contains("%") && p.Message.Contains("MB/s")), "Fallback verification has no separate percent/speed.");
        RegressionCases.Check(progress.Last().State == TransferState.Completed, "Verification never completed.");
    }

    private static async Task RejectChangedAsync(CancellationToken ct)
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        var path = root.File("source");
        var target = root.File("target");
        await File.WriteAllBytesAsync(path, new byte[RemoteFileDigest.MinimumLength], ct);
        await File.WriteAllTextAsync(target, "keep existing", ct);
        var source = new DigestFileSystem(local) { ChangeDuringChecksum = true };
        await RegressionCases.ThrowsAsync<IOException>(() => new TransferEngine().CopyAsync(source, path, local, target,
            new(Move: true, Conflict: ConflictAction.Overwrite), null, ct));
        RegressionCases.Check(File.Exists(path) && await File.ReadAllTextAsync(target, ct) == "keep existing",
            "Same-size/same-time source change was committed or source deleted.");
    }

    private sealed class DigestFileSystem(IFileSystem inner) : DelegatingFileSystem(inner), IFileDigestProvider
    {
        public bool Unavailable;
        public bool ChangeDuringChecksum;
        public int DigestCalls;
        public int Reads;
        public override bool IsRemote => true;
        public override Task<Stream> OpenReadAsync(string path, CancellationToken ct)
        { Reads++; return base.OpenReadAsync(path, ct); }
        public async Task<byte[]?> TryReadDigestAsync(string path, long length, CancellationToken ct)
        {
            DigestCalls++;
            if (Unavailable) return null;
            var bytes = await File.ReadAllBytesAsync(path, ct);
            // Model a server changing contents without exposing new metadata.
            if (ChangeDuringChecksum) bytes[0] ^= 1;
            return SHA256.HashData(bytes);
        }
    }

    private sealed class ProgressSink(Action<TransferProgress> report) : IProgress<TransferProgress>
    { public void Report(TransferProgress value) => report(value); }

    private static ProcessStartInfo Helper(string mode)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "MySsh.Tests.dll"));
        info.ArgumentList.Add("--digest-helper");
        info.ArgumentList.Add(mode);
        return info;
    }

    internal static async Task<int> RunHelperAsync(string mode)
    {
        if (mode == "wait") { await Task.Delay(20_000); return 0; }
        if (mode == "overflow") { Console.Write(new string('x', 5000)); await Task.Delay(20_000); return 0; }
        if (mode == "stderr") Console.Error.Write(new string('e', 128 * 1024));
        Console.Write(ValidOutput);
        return mode == "failure" ? 1 : 0;
    }
}
