using MySsh.App;
using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

internal static class LayoutTests
{
    public static Task RunAsync()
    {
        using var fixture = new Fixture();
        var driver = new FakeDriver();
        Application.Init(driver);
        try
        {
            using var window = fixture.CreateWindow();
            Application.Top.Add(window);
            var run = Application.Begin(Application.Top);
            try
            {
                // Odd widths and shrinking after expansion exercise both pane edges.
                foreach (var width in new[] { 80, 81, 144, 145, 200, 119, 80 })
                {
                    driver.SetBufferSize(width, 35);
                    Application.Refresh();
                    var left = Descendants(window).OfType<FrameView>().Single(x => x.Title.ToString() == "LOCAL");
                    var right = Descendants(window).OfType<FrameView>().Single(x => x.Title.ToString() == "REMOTE");
                    var divider = left.Frame.Right;
                    if (right.Frame.X != left.Frame.Right)
                        throw new Exception($"Pane frames overlap or have a gap at width {width}.");
                    for (var row = 2; row < left.Frame.Bottom; row++)
                    {
                        if (driver.Contents[row, divider, 0] != '│' ||
                            driver.Contents[row, divider + 1, 0] != '│')
                            throw new Exception($"Wide/long name overwrote divider at {width}x35, row {row}.");
                    }

                    var helpShown = false;
                    Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(20), _ =>
                    {
                        helpShown = Application.Current is Dialog dialog && dialog.Title.ToString() == "Help" &&
                            dialog.Frame.X >= 0 && dialog.Frame.Y >= 0 &&
                            dialog.Frame.Right <= width && dialog.Frame.Bottom <= 35;
                        Application.RequestStop();
                        return false;
                    });
                    FileManagerWindow.ShowHelp();
                    if (!helpShown) throw new Exception("Help dialog was not shown.");
                    Application.Refresh();
                    for (var row = 2; row < left.Frame.Bottom; row++)
                        if (driver.Contents[row, divider, 0] != '│' ||
                            driver.Contents[row, divider + 1, 0] != '│')
                            throw new Exception($"Closing Help damaged divider at width {width}, row {row}.");

                    CheckPreview("# Title\n\n短い本文\n", width, compact: true);
                    CheckPreview(string.Join('\n', Enumerable.Repeat("日本語の長い本文 " + new string('x', 120), 100)),
                        width, compact: false);
                }
            }
            finally { Application.End(run); }
        }
        finally
        {
            MySsh.App.Program.ShutdownUi();
        }
        return Task.CompletedTask;
    }

    private static IEnumerable<View> Descendants(View parent)
    {
        foreach (var view in parent.Subviews)
        {
            yield return view;
            foreach (var child in Descendants(view)) yield return child;
        }
    }

    private static void CheckPreview(string text, int terminalWidth, bool compact)
    {
        using var dialog = FileManagerWindow.CreatePreviewDialog("README.md", text);
        var run = Application.Begin(dialog);
        try
        {
            var view = Descendants(dialog).OfType<TextView>().Single();
            var close = Descendants(dialog).OfType<Button>().Single();
            if (dialog.Frame.X < 0 || dialog.Frame.Right > terminalWidth ||
                dialog.Frame.Y < 0 || dialog.Frame.Bottom > Application.Driver.Rows)
                throw new Exception("Preview extends outside the terminal.");
            if (view.Frame.Bottom > close.Frame.Y)
                throw new Exception("Preview text covers the Close button.");
            if (!view.WordWrap || (compact && dialog.Frame.Height > 8))
                throw new Exception("Short Markdown preview contains excess blank rows.");
            if (!compact && view.Lines <= view.Frame.Height)
                throw new Exception("Long Markdown fixture should remain scrollable.");
            if (!compact)
            {
                view.TopRow = view.Lines - 1;
                view.SetNeedsDisplay();
                Application.Refresh();
                if (view.TopRow > Math.Max(0, view.Lines - view.Frame.Height))
                    throw new Exception("Scrolling to the end leaves excess blank space below the text.");
            }
        }
        finally { Application.End(run); }
    }

    public static int RunPreviewDemo()
    {
        MySsh.App.Program.InitializeUi();
        try
        {
            using var dialog = FileManagerWindow.CreatePreviewDialog("README.md",
                "# Preview\n\n短いMarkdownは本文に合わせた高さで表示します。\n" +
                "日本語と長い文章は折り返し、画面に収まらない分はスクロールできます。\n");
            Application.Run(dialog);
        }
        finally { MySsh.App.Program.ShutdownUi(); }
        return 0;
    }

    public static int RunDemo(bool legacyDriver, bool showHelp)
    {
        using var fixture = new Fixture();
        if (legacyDriver)
            Application.Init();
        else
            MySsh.App.Program.InitializeUi();
        try
        {
            using var window = fixture.CreateWindow();
            Application.Top.Add(window);
            if (showHelp)
                Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(800), _ =>
                {
                    FileManagerWindow.ShowHelp();
                    return false;
                });
            Application.Run();
        }
        finally
        {
            MySsh.App.Program.ShutdownUi();
        }
        return 0;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "my-ssh-layout-" + Guid.NewGuid().ToString("N"));
        private readonly LocalFileSystem _local = new();
        private readonly LocalFileSystem _remote = new();
        private readonly TransferQueue _queue = new(1);
        private readonly SettingsStore _settings;
        private readonly BrowserState _state;

        public Fixture()
        {
            var local = Path.Combine(_root, "local");
            var remote = Path.Combine(_root, "remote");
            Directory.CreateDirectory(local);
            Directory.CreateDirectory(remote);
            string[] names = [
                "01-ascii", "02-日本語", "03-スライド穴埋め空欄版", "04-日本語とASCII-mixed",
                "05-" + new string('日', 60), "06-" + new string('a', 120), "07-情報処理Ⅰ・基礎工学",
                "08-日本語の後にASCII", "09-emoji-📁", "10-ascii-after-wide"
            ];
            foreach (var name in names)
            {
                File.WriteAllText(Path.Combine(local, name + ".txt"), "layout fixture");
                File.WriteAllText(Path.Combine(remote, name + ".txt"), "layout fixture");
            }
            _settings = new SettingsStore(Path.Combine(_root, "settings"));
            _state = new BrowserState { LocalPath = local, RemotePath = remote };
        }

        public FileManagerWindow CreateWindow() => new(new Connection("layout.test", "local"),
            _local, _remote, _state, _settings, _queue);

        public void Dispose()
        {
            _queue.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _local.Dispose();
            _remote.Dispose();
            _settings.Dispose();
            Directory.Delete(_root, true);
        }
    }
}
