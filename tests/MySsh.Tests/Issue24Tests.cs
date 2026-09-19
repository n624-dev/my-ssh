using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class Issue24Tests : IRegressionCase
{
    public string Name => "#24 link permissions never modify the target";

    public async Task RunAsync()
    {
        var sent = new MemoryStream();
        var replies = new MemoryStream(new[]
        {
            ScriptedSftp.Version(),
            ScriptedSftp.Packet(105, ScriptedSftp.U32(1), ScriptedSftp.U32(4), ScriptedSftp.U32(0xA1FF))
        }.SelectMany(x => x).ToArray());
        var session = await SftpSession.ConnectAsync(sent, replies, CancellationToken.None);
        await using var remote = new SftpFileSystem(new Connection("link.test", "test"), session);
        await RegressionCases.ThrowsAsync<IOException>(() => remote.SetPermissionsAsync("link", 0x1A4, CancellationToken.None));
        var expected = ScriptedSftp.Packet(1, ScriptedSftp.U32(3)).Concat(
            ScriptedSftp.Packet(7, ScriptedSftp.U32(1), ScriptedSftp.Text("link"))).ToArray();
        RegressionCases.Check(sent.ToArray().SequenceEqual(expected), "A link permission operation sent more than INIT and LSTAT.");

        if (OperatingSystem.IsWindows()) return;
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        var target = root.File("target");
        var link = root.File("link");
        await File.WriteAllTextAsync(target, "private contents");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.CreateSymbolicLink(link, target);
        await RegressionCases.ThrowsAsync<IOException>(() => local.SetPermissionsAsync(link, 0x1A4, CancellationToken.None));
        RegressionCases.Check((uint)File.GetUnixFileMode(target) == 0x180, "The link target's permissions changed.");
        RegressionCases.Check(new FileInfo(link).LinkTarget == target, "The symbolic link changed.");
    }
}
