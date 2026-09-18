using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class Issue11Tests : IRegressionCase
{
    public string Name => "#11 concurrent settings saves merge independent changes and reject lost updates";

    public async Task RunAsync()
    {
        using var root = new TestDirectory();
        using var first = new SettingsStore(root.Path);
        using var second = new SettingsStore(root.Path);
        first.Config.Editor = "editor-one";
        second.Config.ParallelTransfers = 3;
        await Task.WhenAll(Task.Run(first.SaveConfig), Task.Run(second.SaveConfig));
        using (var check = new SettingsStore(root.Path))
        {
            RegressionCases.Check(check.Config.Editor == "editor-one" && check.Config.ParallelTransfers == 3,
                "independent settings changes were lost");
        }
        // A subsequent save by an instance with a stale view of an unrelated
        // property must not restore that property's old value.
        second.Config.PreserveMetadata = false;
        second.SaveConfig();
        using (var check = new SettingsStore(root.Path))
            RegressionCases.Check(check.Config.Editor == "editor-one" && !check.Config.PreserveMetadata,
                "repeat save rolled back another process's setting");

        second.Config.Editor = "conflicting-editor";
        await RegressionCases.ThrowsAsync<IOException>(() => Task.Run(second.SaveConfig));
        using (var check = new SettingsStore(root.Path))
            RegressionCases.Check(check.Config.Editor == "editor-one", "conflicting save overwrote the winner");

        var a = first.Browser(new Connection("server-a", "user"));
        var b = second.Browser(new Connection("server-b", "user"));
        a.LocalPath = "first-path";
        b.LocalPath = "second-path";
        await Task.WhenAll(Task.Run(first.SaveState), Task.Run(second.SaveState));
        a.RemotePath = "/changed-by-first";
        first.SaveState();
        b.RemotePath = "/changed-by-second";
        second.SaveState();
        using (var check = new SettingsStore(root.Path))
        {
            RegressionCases.Check(check.State.Connections.Count == 2, "another connection's state was erased");
            RegressionCases.Check(check.Browser(new("server-a", "user")).RemotePath == "/changed-by-first" &&
                check.Browser(new("server-b", "user")).RemotePath == "/changed-by-second", "state updates were lost");
        }

        using var third = new SettingsStore(root.Path);
        using var fourth = new SettingsStore(root.Path);
        third.Config.Servers.Add(new ServerConfig { Host = "one.test" });
        fourth.Config.Servers.Add(new ServerConfig { Host = "two.test" });
        third.SaveConfig();
        await RegressionCases.ThrowsAsync<IOException>(() => Task.Run(fourth.SaveConfig));
        using (var check = new SettingsStore(root.Path))
            RegressionCases.Check(check.Config.Servers.Single().Host == "one.test", "conflicting server list was overwritten");

        using (var held = SettingsConcurrency.Acquire(root.Path))
            await RegressionCases.ThrowsAsync<IOException>(() => Task.Run(() =>
            {
                using var unavailable = SettingsConcurrency.Acquire(root.Path, TimeSpan.FromMilliseconds(80));
            }));
        // The lock is released after every transaction; multiple instances still run.
        using (var available = SettingsConcurrency.Acquire(root.Path, TimeSpan.FromMilliseconds(80))) { }

        var configPath = Path.Combine(root.Path, "config.json");
        File.Delete(configPath);
        await RegressionCases.ThrowsAsync<IOException>(() => Task.Run(third.SaveConfig));
        RegressionCases.Check(!File.Exists(configPath), "removed settings were recreated from stale data");
    }
}
