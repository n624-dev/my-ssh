using System.Diagnostics;
using Terminal.Gui;

internal static class InputQueueTests
{
    public static Task RunAsync()
    {
        const int count = 30_000;
        var produced = 0;
        var received = 0;
        var probes = 0;
        using var driver = new NetMainLoop(new FakeDriver(), _ =>
            ++probes % 7 == 0 ? null : new NetEvents.InputResult {
                EventType = NetEvents.EventType.Key,
                ConsoleKeyInfo = new ConsoleKeyInfo((char)(++produced % 20 + 'a'), ConsoleKey.A, false, false, false)
            });
        var loop = new MainLoop(driver);
        driver.ProcessInput = input =>
        {
            received++;
            if (input.ConsoleKeyInfo.KeyChar != (char)(received % 20 + 'a'))
                throw new Exception("Input events were lost, duplicated or reordered.");
            // Nested dialog event loops run while an outer key handler is still active.
            if (received % 100 == 0) loop.Driver.MainIteration();
        };
        var deadline = Stopwatch.StartNew();
        while (received < count && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            loop.Driver.EventsPending(false);
            loop.Driver.MainIteration();
            if (driver.InputTask.IsFaulted)
                throw new Exception("Input pump died while the UI was still running.", driver.InputTask.Exception);
        }
        if (received < count)
            throw new Exception($"Input pump stopped responding after {received} events.");
        driver.Dispose();
        if (!SpinWait.SpinUntil(() => driver.InputTask.IsCompleted, TimeSpan.FromSeconds(2)))
            throw new Exception("Input pump remained blocked after shutdown.");
        if (driver.InputTask.IsFaulted)
            throw new Exception("Input pump faulted during shutdown.", driver.InputTask.Exception);
        return Task.CompletedTask;
    }
}
