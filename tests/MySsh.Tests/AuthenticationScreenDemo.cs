using MySsh.App;
using Terminal.Gui;

internal static class AuthenticationScreenDemo
{
    // Interactive terminal regression check. Enter advances the synthetic prompt;
    // no keys, credentials, network connections or fixture directories are used.
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("AUTH_BASELINE_MUST_REMAIN");
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await AuthenticationScreen.RunAsync(async () =>
                {
                    Console.Error.Write($"SYNTHETIC_AUTH_PROMPT_{attempt} (press Enter): ");
                    Console.ReadKey(intercept: true);
                    Console.Error.WriteLine();
                    await Task.Delay(20).ConfigureAwait(false);
                    if (attempt == 3) throw new IOException("SYNTHETIC_CONNECTION_FAILURE");
                    return true;
                });
            }
            catch (IOException ex) when (attempt == 3)
            {
                Console.WriteLine(ex.Message);
            }

            MySsh.App.Program.InitializeUi();
            try
            {
                Application.Top.Add(new Window("Synthetic post-authentication UI"));
                Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(100), _ =>
                {
                    Application.RequestStop();
                    return false;
                });
                Application.Run();
            }
            finally { MySsh.App.Program.ShutdownUi(); }
            Console.WriteLine($"AUTH_ATTEMPT_{attempt}_DONE");
        }
        return 0;
    }
}
