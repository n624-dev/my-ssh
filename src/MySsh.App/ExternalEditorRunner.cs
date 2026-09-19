using System.Diagnostics;
using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

namespace MySsh.App;

internal sealed record ExternalEditorRequest(EditSession Session, IFileSystem FileSystem,
    string Executable, IReadOnlyList<string> Arguments);

/// <summary>The session controller calls this only after the terminal UI is shut down.</summary>
internal static class ExternalEditorRunner
{
    internal static async Task RunAsync(ExternalEditorRequest request,
        Func<ProcessStartInfo, Task<int>>? execute = null)
    {
        if (Application.Driver is not null)
            throw new InvalidOperationException("Stop the terminal UI before starting an external editor.");
        if (string.IsNullOrWhiteSpace(request.Executable))
            throw new IOException("No external editor is configured.");
        var info = new ProcessStartInfo(request.Executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };
        foreach (var argument in request.Arguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Add(request.Session.DraftPath);
        var exitCode = await (execute ?? ExecuteAsync)(info).ConfigureAwait(false);
        if (exitCode != 0) throw new IOException($"Editor exited with code {exitCode}. Your draft is retained.");
    }

    private static async Task<int> ExecuteAsync(ProcessStartInfo info)
    {
        // Ctrl+C belongs to the child editor. Do not terminate the parent and
        // lose the opportunity to restore its screen when the editor exits.
        ConsoleCancelEventHandler cancel = (_, args) => args.Cancel = true;
        Console.CancelKeyPress += cancel;
        try
        {
            using var process = Process.Start(info) ?? throw new IOException("Could not start the configured editor.");
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
