using MySsh.Infrastructure;

internal sealed class Issue31Tests : IRegressionCase
{
    public string Name => "#31 binary unknown extension values do not prevent SFTP initialization";

    public async Task RunAsync()
    {
        var sent = new MemoryStream();
        var version = ScriptedSftp.Packet(2, ScriptedSftp.U32(3),
            ScriptedSftp.Text("binary@example.test"), ScriptedSftp.Blob([0xFF, 0, 0xFE]),
            ScriptedSftp.Text("empty@example.test"), ScriptedSftp.Blob([]),
            ScriptedSftp.Text("posix-rename@openssh.com"), ScriptedSftp.Text("1"));
        await using (var session = await SftpSession.ConnectAsync(sent,
            new MemoryStream(version.Concat(ScriptedSftp.Status(1, 0)).ToArray()), CancellationToken.None))
        {
            await session.RenameAsync("stage", "target", true, CancellationToken.None);
            var expected = ScriptedSftp.Packet(1, ScriptedSftp.U32(3)).Concat(
                ScriptedSftp.Packet(200, ScriptedSftp.U32(1), ScriptedSftp.Text("posix-rename@openssh.com"),
                    ScriptedSftp.Text("stage"), ScriptedSftp.Text("target")));
            RegressionCases.Check(sent.ToArray().SequenceEqual(expected), "Binary extensions broke the known rename extension.");
        }
        foreach (var unsupported in new byte[][] { [], [0xFF], [(byte)'2'] })
        {
            var output = new MemoryStream();
            var response = ScriptedSftp.Packet(2, ScriptedSftp.U32(3),
                ScriptedSftp.Text("posix-rename@openssh.com"), ScriptedSftp.Blob(unsupported));
            await using var session = await SftpSession.ConnectAsync(output, new MemoryStream(response), CancellationToken.None);
            await RegressionCases.ThrowsAsync<IOException>(() => session.RenameAsync("stage", "target", true, CancellationToken.None));
            RegressionCases.Check(output.ToArray().SequenceEqual(ScriptedSftp.Packet(1, ScriptedSftp.U32(3))),
                "An unsupported rename extension version sent an unsafe replacement request.");
        }
        // Length framing must still be checked even though the data is opaque.
        var malformed = ScriptedSftp.Packet(2, ScriptedSftp.U32(3), ScriptedSftp.Text("binary@example.test"),
            ScriptedSftp.U32(2), new byte[] { 0xFF });
        await RegressionCases.ThrowsAsync<IOException>(() => SftpSession.ConnectAsync(new MemoryStream(), new MemoryStream(malformed), CancellationToken.None));
    }
}
