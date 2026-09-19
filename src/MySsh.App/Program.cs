using MySsh.Core;
using MySsh.Infrastructure;
using System.Text;
using Terminal.Gui;

namespace MySsh.App;

internal static class Program
{
    private static Encoding? _previousOutputEncoding;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            string? settingsDirectory = null;
            if (args.Length == 1 && args[0] is "--help" or "-h")
            {
                Console.WriteLine("Usage: my-ssh [--config-dir <directory>]");
                return 0;
            }
            if (args.Length == 2 && args[0] == "--config-dir" && !string.IsNullOrWhiteSpace(args[1]))
                settingsDirectory = Path.GetFullPath(args[1]);
            else if (args.Length != 0)
                throw new ArgumentException("Usage: my-ssh [--config-dir <directory>]");

            await OpenSsh.EnsureAvailableAsync(CancellationToken.None);
            using var settings = new SettingsStore(settingsDirectory);

            while (true)
            {
                SelectionResult? selection;
                InitializeUi();
                try
                {
                    selection = SelectionUi.Run(settings);
                }
                finally
                {
                    ShutdownUi();
                }

                if (selection is null || selection.Action == RequestedAction.Exit)
                    return 0;

                if (selection.Action == RequestedAction.OpenSsh)
                {
                    await OpenSsh.RunInteractiveAsync(selection.Connection, CancellationToken.None);
                    continue;
                }

                await FileManagerSession.RunAsync(selection.Connection, settings);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("my-ssh failed: " + ex.Message);
            return 1;
        }
    }

    internal static void InitializeUi()
    {
        // WindowsDriver writes a NUL continuation cell after each wide rune.
        // Windows Terminal displays that as extra space, shifting the rest of
        // the row (including the pane borders). NetDriver emits Unicode via VT.
        Application.UseSystemConsole = OperatingSystem.IsWindows();
        if (OperatingSystem.IsWindows())
        {
            _previousOutputEncoding = Console.OutputEncoding;
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        try { Application.Init(); }
        catch
        {
            RestoreOutputEncoding();
            throw;
        }
    }

    internal static void ShutdownUi()
    {
        try { Application.Shutdown(); }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(null);
            RestoreOutputEncoding();
        }
    }

    private static void RestoreOutputEncoding()
    {
        if (_previousOutputEncoding is not { } previous) return;
        _previousOutputEncoding = null;
        Console.OutputEncoding = previous;
    }
}
