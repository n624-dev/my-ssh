using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class Issue04Tests : IRegressionCase
{
    public string Name => "#4 edits preserve target permissions before commit without widening drafts";

    public async Task RunAsync()
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        foreach (var mode in new uint[] { 0x1ED, 0x1A4, 0x180 })
        {
            var path = root.File("mode-" + mode + ".txt");
            await File.WriteAllTextAsync(path, "original");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)mode);
            var target = new RecordingTarget(local, path, mode);
            var session = await EditSession.OpenAsync(target, path, root.File("drafts"), "test", CancellationToken.None);
            session.WriteText("edited contents");
            if (!OperatingSystem.IsWindows())
                RegressionCases.Check((uint)File.GetUnixFileMode(session.DraftPath) == 0x180, "draft was not private");
            await session.SaveAsync(null, CancellationToken.None);
            RegressionCases.Check(target.ModeAtCommit == mode, "target mode was not applied before commit");
            if (!OperatingSystem.IsWindows())
                RegressionCases.Check((uint)File.GetUnixFileMode(path) == mode, "local mode changed after edit");
            RegressionCases.Check(await File.ReadAllTextAsync(path) == "edited contents", "contents not saved");
        }

        var conflictPath = root.File("permissions-changed.txt");
        await File.WriteAllTextAsync(conflictPath, "original");
        var changed = new RecordingTarget(local, conflictPath, 0x180);
        var retained = await EditSession.OpenAsync(changed, conflictPath, root.File("drafts"), "test", CancellationToken.None);
        retained.WriteText("retained edit");
        changed.Mode = 0x1A4;
        await RegressionCases.ThrowsAsync<IOException>(() => retained.SaveAsync(null, CancellationToken.None));
        RegressionCases.Check(retained.ReadText() == "retained edit", "permission conflict lost draft");
        RegressionCases.Check(await File.ReadAllTextAsync(conflictPath) == "original", "permission conflict overwrote original");
        retained.Discard();

        if (Environment.GetEnvironmentVariable("MYSSH_TEST_HOST") is { Length: > 0 } host)
            await CheckRealSftp(root, local, host);
    }

    private static async Task CheckRealSftp(TestDirectory root, LocalFileSystem local, string host)
    {
        var user = Environment.GetEnvironmentVariable("MYSSH_TEST_USER")!;
        var fixture = Environment.GetEnvironmentVariable("MYSSH_TEST_ROOT")!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = deadline.Token;
        await using var remote = await SftpFileSystem.ConnectAsync(new(host, user), ct);
        var directory = remote.Join(await remote.CanonicalAsync(fixture, ct), "edit-mode-" + Guid.NewGuid().ToString("N"));
        await remote.CreateDirectoryAsync(directory, ct);
        try
        {
            foreach (var mode in new uint[] { 0x1ED, 0x1A4, 0x180 })
            {
                var localPath = root.File("sftp-seed.txt");
                await File.WriteAllTextAsync(localPath, "original", ct);
                var target = remote.Join(directory, "mode-" + mode);
                await new TransferEngine().CopyAsync(local, localPath, remote, target, new(), null, ct);
                await remote.SetMetadataAsync(target, DateTimeOffset.UtcNow, mode, ct);
                var edit = await EditSession.OpenAsync(remote, target, root.File("drafts"), host, ct);
                edit.WriteText("remote edited contents");
                await edit.SaveAsync(null, ct);
                RegressionCases.Check(((await remote.StatAsync(target, ct))!.Mode & 0x1FF) == mode, "SFTP mode changed after edit");
                await remote.DeleteAsync(target, false, ct);
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            foreach (var entry in await remote.ListAsync(directory, cleanup.Token))
                await remote.DeleteAsync(entry.Path, false, cleanup.Token);
            await remote.DeleteAsync(directory, true, cleanup.Token);
        }
    }

    private sealed class RecordingTarget(IFileSystem inner, string original, uint mode) : DelegatingFileSystem(inner)
    {
        public uint Mode { get; set; } = mode;
        public uint? ModeAtCommit { get; private set; }
        private uint? _partialMode;
        public override async Task<FileEntry?> StatAsync(string path, CancellationToken ct)
        {
            var entry = await base.StatAsync(path, ct);
            return entry is not null && path == original ? entry with { Mode = Mode } : entry;
        }
        public override async Task SetMetadataAsync(string path, DateTimeOffset modified, uint? value, CancellationToken ct)
        {
            _partialMode = value;
            await base.SetMetadataAsync(path, modified, value, ct);
        }
        public override async Task RenameAsync(string source, string destination, bool replace, CancellationToken ct)
        {
            if (destination == original) ModeAtCommit = _partialMode;
            await base.RenameAsync(source, destination, replace, ct);
        }
    }
}
