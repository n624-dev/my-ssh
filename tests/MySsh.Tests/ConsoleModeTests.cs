using System.Runtime.InteropServices;
using MySsh.App;
using Terminal.Gui;

internal static class ConsoleModeTests
{
    public static Task RunAsync()
    {
        foreach (var sharedBuffer in new[] { true, false })
        foreach (var outputMode in new uint[] { 3, 7, 15 })
        {
            const uint inputMode = 0x1F7;
            var errorMode = sharedBuffer ? outputMode : 3u;
            var modes = new Dictionary<nint, uint> { [-10] = inputMode, [-11] = outputMode, [-12] = errorMode };
            nint Buffer(nint handle) => sharedBuffer && handle == -12 ? -11 : handle;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var reads = 0;
                var console = new NetWinVTConsole(handle => handle,
                    handle => { reads++; return modes[Buffer(handle)]; },
                    (handle, mode) =>
                    {
                        if (reads != 3) throw new Exception("Console modes changed before all originals were captured.");
                        modes[Buffer(handle)] = mode;
                    });
                if ((modes[-11] & 12) != 12 || (modes[Buffer(-12)] & 12) != 12)
                    throw new Exception("VT output mode was lost on an aliased handle.");
                console.Cleanup();
                if (modes[-10] != inputMode || modes[-11] != outputMode || modes[Buffer(-12)] != errorMode)
                    throw new Exception("UI console mode changes leaked into SSH authentication.");
            }
        }
        return Task.CompletedTask;
    }

    // Real-console check: no network connection, key or password is used.
    public static async Task<int> RunNativeAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var output = GetStdHandle(-11);
        var error = GetStdHandle(-12);
        var originalOutput = Mode(output);
        var originalError = Mode(error);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                MySsh.App.Program.InitializeUi();
                try
                {
                    Application.Top.Add(new Window("Console mode regression check"));
                    Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(100), _ =>
                    {
                        Application.RequestStop();
                        return false;
                    });
                    Application.Run();
                }
                finally { MySsh.App.Program.ShutdownUi(); }

                if (Mode(output) != originalOutput || Mode(error) != originalError)
                    throw new Exception($"Console modes leaked: output {originalOutput:x}->{Mode(output):x}, " +
                        $"stderr {originalError:x}->{Mode(error):x}.");
                CheckNewline("SSH");
                await AuthenticationScreen.RunAsync(() =>
                {
                    CheckNewline("SFTP");
                    return Task.FromResult(true);
                });
                if (Mode(output) != originalOutput || Mode(error) != originalError)
                    throw new Exception("Authentication screen did not restore console modes.");
            }
            Console.WriteLine("PASS native console modes and SSH/SFTP prompt newlines");
            return 0;
        }
        finally
        {
            SetConsoleMode(output, originalOutput);
            SetConsoleMode(error, originalError);
        }
    }

    private static void CheckNewline(string label)
    {
        Console.Write("\r");
        Console.Error.Write("SYNTHETIC_PASSPHRASE_PROMPT: \n");
        Console.Error.Flush();
        var column = Console.CursorLeft;
        Console.WriteLine($"{label}_BANNER");
        if (column != 0) throw new Exception($"{label} banner starts in column {column}, not column 0.");
    }

    private static uint Mode(nint handle) => GetConsoleMode(handle, out var mode)
        ? mode : throw new IOException("Run this check in a Windows console.");

    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int handle);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(nint handle, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(nint handle, uint mode);
}
