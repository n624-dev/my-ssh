using MySsh.App;
using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

internal sealed class Issue35Tests : IRegressionCase
{
    public string Name => "#35 the Actions menu includes primary file operations and opens without function keys";
    public Task RunAsync()
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        using var settings = new SettingsStore(root.File("settings"));
        var source = root.File("source");
        var destination = root.File("destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var file = Path.Combine(source, "file.txt");
        File.WriteAllText(file, "menu transfer");
        var queue = new TransferQueue(1);
        Application.Init(new FakeDriver());
        var run = Application.Begin(Application.Top);
        try
        {
            using var window = new FileManagerWindow(new Connection("menu.test", "user"), local, local,
                new BrowserState { LocalPath = source, RemotePath = destination }, settings, queue);
            Application.Top.Add(window);
            window.InitializeBrowser();
            var actions = window.BuildFileActions();
            string[] required = ["copy", "move", "rename", "delete", "mkdir", "path", "filter", "sort",
                "select-all", "clear-selection", "refresh", "hidden", "preview", "edit", "permissions", "bookmark-save", "bookmark-open"];
            RegressionCases.Check(required.All(id => actions.Count(x => x.Id == id) == 1), "The menu omits or duplicates a primary operation.");
            var opened = false;
            var timer = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(20), _ =>
            {
                if (Application.Current is not Dialog dialog || dialog.Title.ToString() != "Actions (Alt+A)") return true;
                opened = true;
                Application.RequestStop(dialog);
                return false;
            });
            try { RegressionCases.Check(window.ProcessHotKey(new KeyEvent(Key.A | Key.AltMask, new KeyModifiers())), "Alt+A was not handled."); }
            finally { Application.MainLoop.RemoveTimeout(timer); }
            RegressionCases.Check(opened, "Alt+A did not open the menu.");
            actions.Single(x => x.Id == "select-all").Run();
            RegressionCases.Check(window.CaptureInteractionState().Local.Marked.SequenceEqual(new[] { file }), "Menu selection action is not wired.");
            actions.Single(x => x.Id == "copy").Run();
            UiFileOperation.Run("Await menu copy", async ct =>
            {
                while (queue.Snapshot().Single().State is TransferState.Running or TransferState.Queued)
                    await Task.Delay(10, ct).ConfigureAwait(false);
            }, TimeSpan.FromSeconds(10));
            RegressionCases.Check(queue.Snapshot().Single().State == TransferState.Completed &&
                File.ReadAllText(Path.Combine(destination, "file.txt")) == "menu transfer", "Menu Copy did not run the normal transfer path.");
            actions.Single(x => x.Id == "clear-selection").Run();
            RegressionCases.Check(window.CaptureInteractionState().Local.Marked.Length == 0, "Menu clear-selection is not wired.");
            Application.Top.Remove(window);
        }
        finally
        {
            Application.End(run);
            MySsh.App.Program.ShutdownUi();
            queue.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        return Task.CompletedTask;
    }
}
