using MySsh.App;
using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

internal sealed class Issue36Tests : IRegressionCase
{
    public string Name => "#36 key actions, status labels and help use one validated binding map";
    public Task RunAsync()
    {
        var configured = new AppConfig().Keys;
        configured["copy"] = "F8";
        configured["help"] = "Ctrl+H";
        var map = FileManagerKeyMap.Create(configured);
        var invoked = false;
        var item = map.Status("copy", () => invoked = true);
        RegressionCases.Check(item.Shortcut == Key.F8 && item.Title.ToString() == "~F8~ Copy", "Status shortcut and displayed key disagree.");
        item.Action();
        RegressionCases.Check(invoked && map.HelpText.Contains("F8: Copy to the opposite pane") && !map.HelpText.Contains("F5: Copy"),
            "The configured action or its help text was not updated.");
        RegressionCases.Check(map["help"].Key == (Key.CtrlMask | Key.H) && map.HelpText.Contains("Ctrl+H: Help"), "Modifier formatting is inconsistent.");
        var oldSyntax = FileManagerKeyMap.Create(new Dictionary<string, string> { ["copy"] = "CtrlMask, ShiftMask, X" });
        RegressionCases.Check(oldSyntax["copy"].Display == "Ctrl+Shift+X", "Existing enum-style key syntax regressed.");
        foreach (var value in new[] { "F99", "1234", "Ctrl", "Esc", "Alt+A", "Ctrl+Q", "A", "F5,F8" })
            ExpectInvalid(new Dictionary<string, string> { ["copy"] = value });
        ExpectInvalid(new Dictionary<string, string> { ["copy"] = "F6" });
        ExpectInvalid(new Dictionary<string, string> { ["unknown"] = "F8" });
        RegressionCases.Check(configured["copy"] == "F8", "Validation rewrote the user's configuration.");

        using var root = new TestDirectory();
        using var settings = new SettingsStore(root.File("settings"));
        using var local = new LocalFileSystem();
        settings.Config.Keys = configured;
        var queue = new TransferQueue(1);
        Application.Init(new FakeDriver());
        try
        {
            using var window = new FileManagerWindow(new Connection("keys.test", "user"), local, local,
                new BrowserState { LocalPath = root.Path, RemotePath = root.Path }, settings, queue);
            var status = window.Subviews.OfType<StatusBar>().Single();
            var copy = status.Items.Single(x => x.Title.ToString() == "~F8~ Copy");
            RegressionCases.Check(copy.Shortcut == Key.F8 && status.Items.All(x => x.Shortcut != Key.F5),
                "The live window is still using the old copy key.");
            RegressionCases.Check(status.Items.Any(x => x.Shortcut == (Key.CtrlMask | Key.H)), "The live Help action ignores custom bindings.");
        }
        finally { MySsh.App.Program.ShutdownUi(); queue.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        return Task.CompletedTask;
    }

    private static void ExpectInvalid(IReadOnlyDictionary<string, string> configured)
    {
        try { _ = FileManagerKeyMap.Create(configured); }
        catch (IOException) { return; }
        throw new InvalidOperationException("Invalid or conflicting bindings were silently accepted.");
    }
}
