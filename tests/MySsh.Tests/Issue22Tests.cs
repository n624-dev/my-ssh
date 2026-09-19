using MySsh.App;
using MySsh.Infrastructure;
using Terminal.Gui;

internal sealed class Issue22Tests : IRegressionCase
{
    public string Name => "#22 state write failures cannot skip terminal shutdown";

    public Task RunAsync()
    {
        var order = new List<string>();
        var expected = new IOException("Injected persistence failure");
        try
        {
            UiSessionCleanup.Run(() => { order.Add("save"); throw expected; }, () => order.Add("shutdown"));
            throw new InvalidOperationException("Persistence failure disappeared.");
        }
        catch (IOException ex)
        {
            RegressionCases.Check(ReferenceEquals(ex, expected), "The original persistence error was not propagated.");
        }
        RegressionCases.Check(order.SequenceEqual(new[] { "save", "shutdown" }), "Cleanup did not run exactly once after save failure.");

        using var root = new TestDirectory();
        using var settings = new SettingsStore(root.File("settings"));
        var stateFile = Path.Combine(settings.DirectoryPath, "state.json");
        File.Delete(stateFile);
        Directory.CreateDirectory(stateFile); // Atomic file replacement must fail on both OS families.
        Application.Init(new FakeDriver());
        try
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            var failed = false;
            try { UiSessionCleanup.Run(settings.SaveState, MySsh.App.Program.ShutdownUi); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            RegressionCases.Check(failed, "The real state write was not made to fail.");
            RegressionCases.Check(Application.Driver is null && SynchronizationContext.Current is null,
                "A failed state write left the terminal driver or its synchronization context active.");
        }
        finally { if (Application.Driver is not null) MySsh.App.Program.ShutdownUi(); }
        Directory.Delete(stateFile);
        Application.Init(new FakeDriver());
        try
        {
            UiSessionCleanup.Run(settings.SaveState, MySsh.App.Program.ShutdownUi);
            RegressionCases.Check(File.Exists(stateFile) && Application.Driver is null,
                "Normal persistence or repeated UI cleanup regressed.");
        }
        finally { if (Application.Driver is not null) MySsh.App.Program.ShutdownUi(); }
        return Task.CompletedTask;
    }
}
