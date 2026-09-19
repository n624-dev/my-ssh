using System.Buffers.Binary;
using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class Issue21Tests : IRegressionCase
{
    public string Name => "#21 chmod changes permissions without changing access or modification times";

    public async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            using var root = new TestDirectory();
            using var local = new LocalFileSystem();
            var path = root.File("file.txt");
            await File.WriteAllTextAsync(path, "unchanged contents");
            var modified = DateTime.UnixEpoch.AddSeconds(1_700_000_000);
            var accessed = modified.AddDays(-7);
            File.SetLastWriteTimeUtc(path, modified);
            File.SetLastAccessTimeUtc(path, accessed);
            await local.SetPermissionsAsync(path, 0x180, CancellationToken.None);
            RegressionCases.Check(File.GetLastWriteTimeUtc(path) == modified && File.GetLastAccessTimeUtc(path) == accessed,
                "A permission-only local operation changed timestamps.");
            RegressionCases.Check((uint)File.GetUnixFileMode(path) == 0x180, "Permissions did not change.");
            await RegressionCases.ThrowsAsync<ArgumentOutOfRangeException>(() => local.SetPermissionsAsync(path, 0xFFFF, CancellationToken.None));
        }
        await CheckPacketAsync();
        if (Environment.GetEnvironmentVariable("MYSSH_TEST_HOST") is { Length: > 0 } host)
            await CheckRealSftpAsync(host);
    }

    private static async Task CheckPacketAsync()
    {
        var sent = new MemoryStream();
        var replies = new MemoryStream(new[]
        {
            ScriptedSftp.Version(),
            ScriptedSftp.Packet(105, ScriptedSftp.U32(1), ScriptedSftp.U32(4), ScriptedSftp.U32(0x81A4)),
            ScriptedSftp.Status(2, 0)
        }.SelectMany(x => x).ToArray());
        var session = await SftpSession.ConnectAsync(sent, replies, CancellationToken.None);
        await using var remote = new SftpFileSystem(new Connection("permission.test", "test"), session);
        await remote.SetPermissionsAsync("file", 0x180, CancellationToken.None);
        var data = sent.ToArray();
        var offset = 0;
        var found = false;
        while (offset < data.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4)));
            var start = offset + 4;
            if (data[start] == 9)
            {
                var cursor = start + 5;
                var nameLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor, 4)));
                cursor += 4 + nameLength;
                RegressionCases.Check(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor, 4)) == 4 &&
                    BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor + 4, 4)) == 0x180 && cursor + 8 == start + length,
                    "The SFTP chmod request includes timestamps or other unintended attributes.");
                found = true;
            }
            offset += length + 4;
        }
        RegressionCases.Check(found, "No permission update was sent.");
    }

    private static async Task CheckRealSftpAsync(string host)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var user = Environment.GetEnvironmentVariable("MYSSH_TEST_USER") ?? throw new IOException("Missing test user.");
        var root = Environment.GetEnvironmentVariable("MYSSH_TEST_ROOT") ?? throw new IOException("Missing test root.");
        await using var remote = await SftpFileSystem.ConnectAsync(new Connection(host, user), ct);
        var path = remote.Join(await remote.CanonicalAsync(root, ct), "chmod-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var output = await remote.OpenWriteAsync(path, true, ct))
                await output.WriteAsync(new byte[] { 1, 2, 3 }, ct);
            var modified = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
            await remote.SetMetadataAsync(path, modified, 0x180, ct);
            await remote.SetPermissionsAsync(path, 0x1A4, ct);
            var after = await remote.StatAsync(path, ct);
            RegressionCases.Check(after?.Modified == modified && (after.Mode & 0x1FF) == 0x1A4,
                "Real SFTP chmod changed mtime or failed to set the mode.");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            if (await remote.StatAsync(path, cleanup.Token) is not null) await remote.DeleteAsync(path, false, cleanup.Token);
        }
    }
}
