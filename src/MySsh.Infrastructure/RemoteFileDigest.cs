using System.Diagnostics;
using System.Globalization;
using System.Text;
using MySsh.Core;

namespace MySsh.Infrastructure;

// Optional capability: null means unavailable and requires a full streamed check.
internal interface IFileDigestProvider
{
    Task<byte[]?> TryReadDigestAsync(string path, long length, CancellationToken ct);
}

internal static class RemoteFileDigest
{
    internal const long MinimumLength = 8 * 1024 * 1024;
    private const string Marker = "my-ssh-sha256-v1";

    internal static string Command()
    {
        // The command is constant. Paths travel over stdin, never through the
        // remote login shell (which may not implement POSIX quoting).
        // pipefail rejects missing commands/read failures. Read length + 1, just
        // like the streamed verifier, so growth cannot cause an unbounded read.
        const string script = "IFS= read -r -d \"\" file || exit 65; IFS= read -r count || exit 65; " +
            "[ -f \"$file\" ] && [ ! -L \"$file\" ] || exit 65; " +
            "head -c \"$count\" -- \"$file\" | sha256sum && printf 'my-ssh-sha256-v1\\n'";
        return "timeout 300s bash -o pipefail -c " + Quote(script);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    internal static byte[]? Parse(string output, int exitCode)
    {
        if (exitCode != 0) return null;
        var lines = output.Replace("\r\n", "\n").Split('\n');
        if (lines.Length != 3 || lines[1] != Marker || lines[2] != "" ||
            lines[0].Length != 67 || lines[0][64..] != "  -" ||
            !lines[0][..64].All(Uri.IsHexDigit)) return null;
        return Convert.FromHexString(lines[0][..64]);
    }

    internal static async Task<byte[]?> TryReadAsync(Connection connection, string path, long length, CancellationToken ct)
    {
        connection.Validate();
        if (!path.StartsWith('/') || path.Contains('\0') || length < 0 || length == long.MaxValue) return null;
        var info = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in new[] { "-T", "-a", "-x", "-oBatchMode=yes", "-oStrictHostKeyChecking=yes",
            "-oClearAllForwardings=yes", "-oConnectTimeout=5", "-oConnectionAttempts=1",
            "-oServerAliveInterval=15", "-oServerAliveCountMax=2", "-l", connection.User,
            connection.Host, Command() }) info.ArgumentList.Add(argument);
        var input = path + "\0" + (length + 1).ToString(CultureInfo.InvariantCulture) + "\n";
        return await RunAsync(info, TimeSpan.FromSeconds(315), ct, input).ConfigureAwait(false);
    }

    internal static async Task<byte[]?> RunAsync(ProcessStartInfo info, TimeSpan timeout, CancellationToken ct, string? input = null)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.Start(info) ?? throw new IOException("Could not start checksum helper.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            using var cancellation = deadline.Token.Register(Kill);
            try
            {
                var output = ReadOutputAsync();
                var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
                await Task.WhenAll(WriteInputAsync(), output, errors, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return Parse(await output.ConfigureAwait(false), process.ExitCode);
            }
            finally { Kill(); }

            async Task WriteInputAsync()
            {
                try
                {
                    if (input is not null)
                        await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token).ConfigureAwait(false);
                    process.StandardInput.Close();
                }
                catch { Kill(); throw; }
            }

            void Kill()
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }

            async Task<string> ReadOutputAsync()
            {
                var bytes = new byte[257];
                var used = 0;
                try
                {
                    while (true)
                    {
                        var count = await process.StandardOutput.BaseStream.ReadAsync(bytes.AsMemory(used), deadline.Token).ConfigureAwait(false);
                        if (count == 0) return Encoding.ASCII.GetString(bytes, 0, used);
                        used += count;
                        if (used == bytes.Length) throw new IOException("Unexpected checksum output.");
                    }
                }
                catch { Kill(); throw; }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }
}
