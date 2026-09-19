using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Principal;
using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class Issue18Tests : IRegressionCase
{
    public string Name => "#18 staging files and recovered drafts start private";

    public async Task RunAsync()
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        var stage = root.File("partial");
        await using (var stream = await local.OpenWriteAsync(stage, true, CancellationToken.None))
        {
            await stream.WriteAsync(new byte[] { 1, 2, 3 });
        }
        CheckPrivate(stage, directory: false);
        var source = root.File("original.txt");
        await File.WriteAllTextAsync(source, "private contents");
        var drafts = root.File("drafts");
        Directory.CreateDirectory(drafts);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(drafts, (UnixFileMode)0x1FF);
        var edit = await EditSession.OpenAsync(local, source, drafts, "LOCAL", CancellationToken.None);
        CheckPrivate(drafts, true);
        CheckPrivate(edit.DraftDirectory, true);
        CheckPrivate(edit.DraftPath, false);
        CheckPrivate(Path.Combine(edit.DraftDirectory, "recovery.json"), false);
        edit.WriteText("changed private contents");
        CheckPrivate(edit.DraftPath, false);
        if (!OperatingSystem.IsWindows())
        {
            var target = root.File("target");
            Directory.CreateDirectory(target);
            var link = root.File("linked-drafts");
            Directory.CreateSymbolicLink(link, target);
            await RegressionCases.ThrowsAsync<IOException>(() => EditSession.OpenAsync(local, source, link, "LOCAL", CancellationToken.None));
            RegressionCases.Check(!Directory.EnumerateFileSystemEntries(target).Any(), "The draft followed a link.");
        }
        await CheckOpenAttributesAsync();
        if (Environment.GetEnvironmentVariable("MYSSH_TEST_HOST") is { Length: > 0 } host)
            await CheckRealSftpAsync(host);
    }

    private static void CheckPrivate(string path, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            FileSystemSecurity acl = directory
                ? new DirectoryInfo(path).GetAccessControl()
                : new FileInfo(path).GetAccessControl();
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            RegressionCases.Check(acl.AreAccessRulesProtected && rules.Length > 0 &&
                rules.All(rule => rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference.Equals(identity.User)),
                "A staging ACL grants access to another principal: " + path);
        }
        else
        {
            var mode = (uint)File.GetUnixFileMode(path);
            RegressionCases.Check((mode & 0x1FF) == (directory ? 0x1C0u : 0x180u), "Staging permissions are not private: " + path);
        }
    }

    private static async Task CheckOpenAttributesAsync()
    {
        var sent = new MemoryStream();
        var replies = new MemoryStream(new[] { ScriptedSftp.Version(), ScriptedSftp.Handle(1), ScriptedSftp.Status(2, 0),
            ScriptedSftp.Handle(3), ScriptedSftp.Status(4, 0) }.SelectMany(x => x).ToArray());
        await using var session = await SftpSession.ConnectAsync(sent, replies, CancellationToken.None);
        var created = await session.OpenAsync("stage", SftpSession.OpenCreate | SftpSession.OpenWrite | SftpSession.OpenExclusive, CancellationToken.None);
        await session.CloseAsync(created, CancellationToken.None);
        var existing = await session.OpenAsync("stage", SftpSession.OpenRead, CancellationToken.None);
        await session.CloseAsync(existing, CancellationToken.None);
        var data = sent.ToArray();
        var offset = 0;
        var opens = 0;
        while (offset < data.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4)));
            var start = offset + 4;
            if (data[start] == 3)
            {
                opens++;
                var cursor = start + 5;
                var nameLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor, 4)));
                cursor += 4 + nameLength;
                var flags = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor, 4));
                var attributes = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor + 4, 4));
                if ((flags & SftpSession.OpenCreate) != 0)
                    RegressionCases.Check(attributes == 4 && BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor + 8, 4)) == 0x180,
                        "SFTP creation did not request 0600 before any write.");
                else RegressionCases.Check(attributes == 0, "Opening an existing file unexpectedly changes its attributes.");
            }
            offset += length + 4;
        }
        RegressionCases.Check(opens == 2, "OPEN request checks did not run.");
    }

    private static async Task CheckRealSftpAsync(string host)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var user = Environment.GetEnvironmentVariable("MYSSH_TEST_USER") ?? throw new IOException("Missing test user.");
        var directory = Environment.GetEnvironmentVariable("MYSSH_TEST_ROOT") ?? throw new IOException("Missing test root.");
        await using var remote = await SftpFileSystem.ConnectAsync(new Connection(host, user), ct);
        var path = remote.Join(await remote.CanonicalAsync(directory, ct), "private-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = await remote.OpenWriteAsync(path, true, ct))
            {
                var before = await remote.StatAsync(path, ct);
                RegressionCases.Check((before?.Mode & 0x1FF) == 0x180, "Empty SFTP stage is not 0600.");
                await stream.WriteAsync(new byte[] { 1, 2, 3 }, ct);
                var during = await remote.StatAsync(path, ct);
                RegressionCases.Check((during?.Mode & 0x1FF) == 0x180, "In-progress SFTP data is readable by others.");
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            if (await remote.StatAsync(path, cleanup.Token) is not null) await remote.DeleteAsync(path, false, cleanup.Token);
        }
    }
}
