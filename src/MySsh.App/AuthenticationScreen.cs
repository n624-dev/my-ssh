using System.Runtime.InteropServices;

namespace MySsh.App;

// Used only between Terminal.Gui sessions. OpenSSH retains control of password
// input, while its prompts are kept out of the shell's normal screen/history.
internal sealed class AuthenticationScreen : IDisposable
{
    private readonly nint _outputHandle;
    private readonly uint _previousMode;
    private readonly bool _restoreMode;
    private bool _active;

    private AuthenticationScreen()
    {
        if (Console.IsOutputRedirected || Console.IsInputRedirected) return;

        if (OperatingSystem.IsWindows())
        {
            _outputHandle = GetStdHandle(-11);
            if (!GetConsoleMode(_outputHandle, out _previousMode)) return;
            const uint virtualTerminalProcessing = 0x0004;
            if (!SetConsoleMode(_outputHandle, _previousMode | virtualTerminalProcessing)) return;
            _restoreMode = true;
        }

        try
        {
            Console.Write("\u001b[?1049h\u001b[2J\u001b[H");
            Console.Out.Flush();
            _active = true;
        }
        catch
        {
            if (_restoreMode) SetConsoleMode(_outputHandle, _previousMode);
            throw;
        }
    }

    internal static async Task<T> RunAsync<T>(Func<Task<T>> connect)
    {
        using var screen = new AuthenticationScreen();
        return await connect().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (!_active) return;
        _active = false;
        try
        {
            Console.Write("\u001b[?1049l");
            Console.Out.Flush();
        }
        finally
        {
            if (_restoreMode) SetConsoleMode(_outputHandle, _previousMode);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(nint handle, out uint mode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(nint handle, uint mode);
}
