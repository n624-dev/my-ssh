using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class Issue30Tests : IRegressionCase
{
    public string Name => "#30 complete read-only stages retry commit without a write handle";

    public async Task RunAsync()
    {
        foreach (var length in new[] { 0, 1024 })
        {
            using var root = new TestDirectory();
            using var local = new LocalFileSystem();
            var source = root.File("source");
            var destination = root.File("destination");
            await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)42, length).ToArray());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(source, (UnixFileMode)0x124); // 0444
            var backend = new FailedCommit(local);
            var engine = new TransferEngine();
            var options = new TransferOptions(Move: true);
            await RegressionCases.ThrowsAsync<IOException>(() => engine.CopyAsync(local, source, backend, destination, options, null, CancellationToken.None));
            RegressionCases.Check(File.Exists(source) && !File.Exists(destination), "A failed commit removed source or published destination.");
            await engine.CopyAsync(local, source, backend, destination, options, null, CancellationToken.None);
            RegressionCases.Check(backend.WriteOpens == 1 && backend.Renames == 2, "Retry reopened the completed stage for writing.");
            RegressionCases.Check(!File.Exists(source) && (await File.ReadAllBytesAsync(destination)).Length == length, "Retry did not finish the move.");
            if (!OperatingSystem.IsWindows())
                RegressionCases.Check((uint)File.GetUnixFileMode(destination) == 0x124, "Retry lost read-only permissions.");
        }
        await CorruptStageAsync();
    }

    private static async Task CorruptStageAsync()
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        var source = root.File("source");
        var destination = root.File("destination");
        await File.WriteAllTextAsync(source, "original");
        var backend = new FailedCommit(local);
        var engine = new TransferEngine();
        var options = new TransferOptions(Move: true, VerifyResumePrefix: false);
        await RegressionCases.ThrowsAsync<IOException>(() => engine.CopyAsync(local, source, backend, destination, options, null, CancellationToken.None));
        await File.WriteAllTextAsync(backend.Stage!, "tampered");
        await RegressionCases.ThrowsAsync<IOException>(() => engine.CopyAsync(local, source, backend, destination, options, null, CancellationToken.None));
        RegressionCases.Check(File.Exists(source) && !File.Exists(destination) && backend.Renames == 1 && backend.WriteOpens == 1,
            "A full but corrupted stage bypassed integrity verification.");
    }

    private sealed class FailedCommit(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public int WriteOpens, Renames;
        public string? Stage;
        private bool _metadataApplied;
        public override Task<Stream> OpenWriteAsync(string path, bool createNew, CancellationToken ct)
        {
            WriteOpens++;
            if (_metadataApplied) throw new UnauthorizedAccessException("The completed stage is read-only.");
            Stage = path;
            return base.OpenWriteAsync(path, createNew, ct);
        }
        public override async Task SetMetadataAsync(string path, DateTimeOffset modified, uint? mode, CancellationToken ct)
        {
            await base.SetMetadataAsync(path, modified, mode, ct);
            _metadataApplied = true;
        }
        public override Task RenameAsync(string source, string destination, bool replace, CancellationToken ct)
        {
            if (++Renames == 1) throw new IOException("Injected transient rename failure.");
            return base.RenameAsync(source, destination, replace, ct);
        }
    }
}
