using System.Diagnostics;
using MySsh.Core;

namespace MySsh.Infrastructure;

public static class OpenSsh
{
    public static Process StartSftpSubsystem(Connection connection)
    {
        connection.Validate();
        var info = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = false
        };
        info.ArgumentList.Add("-T");
        info.ArgumentList.Add("-s");
        info.ArgumentList.Add("-l");
        info.ArgumentList.Add(connection.User);
        info.ArgumentList.Add(connection.Host);
        info.ArgumentList.Add("sftp");
        return Process.Start(info) ?? throw new IOException("Could not start OpenSSH ssh client.");
    }

    public static async Task<int> RunInteractiveAsync(Connection connection, CancellationToken cancellationToken)
    {
        connection.Validate();
        var info = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };
        info.ArgumentList.Add("-l");
        info.ArgumentList.Add(connection.User);
        info.ArgumentList.Add(connection.Host);

        using var process = Process.Start(info) ?? throw new IOException("Could not start OpenSSH ssh client.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    public static async Task EnsureAvailableAsync(CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("-V");
        using var process = Process.Start(info) ?? throw new IOException("OpenSSH ssh was not found in PATH.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new IOException("OpenSSH ssh is required and must be available in PATH.");
    }
}
