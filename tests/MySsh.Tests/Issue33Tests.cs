using MySsh.App;
using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

internal sealed class Issue33Tests : IRegressionCase
{
    public string Name => "#33 local and remote paths complete with Unicode-safe prefixes and cursor placement";
    public async Task RunAsync()
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        Directory.CreateDirectory(root.File("foo-a"));
        Directory.CreateDirectory(root.File("foo-b"));
        await File.WriteAllTextAsync(root.File("foo-file.txt"), "fixture");
        var result = await PathCompletion.CompleteAsync(local, root.Path, "fo", CancellationToken.None);
        RegressionCases.Check(result.Text == "foo-" && result.Candidates.Length == 3, "Local common-prefix completion failed.");
        Directory.CreateDirectory(root.File("with spaces 日本語🚀"));
        result = await PathCompletion.CompleteAsync(local, root.Path, "with", CancellationToken.None);
        RegressionCases.Check(result.Text == "with spaces 日本語🚀" + Path.DirectorySeparatorChar, "Spaces or Unicode were changed.");
        result = await PathCompletion.CompleteAsync(local, root.Path, root.File("foo-f"), CancellationToken.None);
        RegressionCases.Check(result.Text == root.File("foo-file.txt"), "Absolute file completion failed.");
        result = await PathCompletion.CompleteAsync(local, root.Path, "missing", CancellationToken.None);
        RegressionCases.Check(result.Text == "missing" && result.Candidates.Length == 0, "No-match input was changed.");
        var remote = new ListingRemote(local);
        result = await PathCompletion.CompleteAsync(remote, "/srv/current", "../parent/日", CancellationToken.None);
        RegressionCases.Check(remote.Resolved == "/srv/current/../parent/" && remote.Listed == "/srv/parent" &&
            result.Text == "../parent/日本語/", "Remote completion used the wrong base or collapsed dot segments before REALPATH.");
        remote.Names = ["🚀one", "🚃two"];
        result = await PathCompletion.CompleteAsync(remote, "/srv", "", CancellationToken.None);
        RegressionCases.Check(result.Text == "", "Completion produced a dangling surrogate.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await RegressionCases.ThrowsAsync<OperationCanceledException>(() => PathCompletion.CompleteAsync(local, root.Path, "fo", cancelled.Token));

        Application.Init(new FakeDriver());
        try
        {
            var message = "";
            using var field = new PathCompletingTextField("🚀u", _ => new("🚀user", ["🚀usera", "🚀userb"]), value => message = value);
            field.ProcessKey(new KeyEvent(Key.Tab, new KeyModifiers()));
            RegressionCases.Check(field.CursorPosition == field.Text.RuneCount && message.Contains("usera"), "Cursor or suggestions are incorrect.");
            field.ProcessKey(new KeyEvent((Key)'a', new KeyModifiers()));
            RegressionCases.Check(field.Text.ToString() == "🚀usera", "Input after completion was inserted in the wrong position.");
            using var failing = new PathCompletingTextField("keep", _ => throw new IOException("unavailable"), value => message = value);
            failing.ProcessKey(new KeyEvent(Key.Tab, new KeyModifiers()));
            RegressionCases.Check(failing.Text.ToString() == "keep" && message.Contains("unavailable"), "A completion error lost input.");
        }
        finally { MySsh.App.Program.ShutdownUi(); }
    }

    private sealed class ListingRemote(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public string? Resolved;
        public string? Listed;
        public string[] Names = ["日本語"];
        public override bool IsRemote => true;
        public override StringComparison PathComparison => StringComparison.Ordinal;
        public override Task<string> CanonicalAsync(string path, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Resolved = path;
            return Task.FromResult(path == "/srv/current/../parent/" ? "/srv/parent" : path);
        }
        public override Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Listed = path;
            return Task.FromResult<IReadOnlyList<FileEntry>>(Names.Select(name =>
                new FileEntry(path + "/" + name, name, EntryKind.Directory, 0, DateTimeOffset.UnixEpoch)).ToArray());
        }
    }
}
