using System.Text;
using System.Text.Json;
using MySsh.Core;
using MySsh.Infrastructure;

// A reproducible, local-only visual fixture; no SSH credentials or server needed.
if (args.Length > 0 && args[0] == "--layout-demo")
    return LayoutTests.RunDemo(args.Contains("--legacy-driver"), args.Contains("--show-help"));
if (args.Length == 1 && args[0] == "--auth-screen-demo")
    return await AuthenticationScreenDemo.RunAsync();
if (args.Length == 1 && args[0] == "--preview-demo")
    return LayoutTests.RunPreviewDemo();

var tests = new List<(string Name, Func<Task> Run)>
{
    ("input queue concurrency", InputQueueTests.RunAsync),
    ("completion", TestCompletion),
    ("file pane layout with wide names", LayoutTests.RunAsync),
    ("settings migration", TestSettingsMigration),
    ("concurrent settings stores", TestConcurrentSettingsStores),
    ("Windows reserved names", TestWindowsReservedNames),
    ("local copy and overwrite", TestLocalCopyAndOverwrite),
    ("recursive directory copy", TestDirectoryCopy),
    ("live progress", TestLiveProgress),
    ("move removes source after copy", TestMove),
    ("skipped move retains source", TestSkippedMoveRetainsSource),
    ("move retains new source entries", TestMoveRetainsNewEntries),
    ("move retains changed source file", TestMoveRetainsChangedFile),
    ("root protection", TestRootProtection),
    ("queue cancellation and shutdown", QueueTests.RunAsync)
};

var failed = 0;
if (Environment.GetEnvironmentVariable("MYSSH_TEST_HOST") is { Length: > 0 })
    tests.Add(("OpenSSH SFTP integration", SftpIntegration.RunAsync));

foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex}");
    }
}
return failed == 0 ? 0 : 1;

static Task TestCompletion()
{
    Equal("user", Completion.LongestCommonPrefix(["usera", "userb"], "u"));
    Equal("admin", Completion.LongestCommonPrefix(["admin"], "a"));
    Equal("x", Completion.LongestCommonPrefix(["admin"], "x"));
    return Task.CompletedTask;
}

static Task TestSettingsMigration()
{
    var root = TempDirectory();
    try
    {
        File.WriteAllText(Path.Combine(root, "config.json"), """
        {
          "servers": [
            { "host": "example.test", "users": ["nobu"] }
          ]
        }
        """, new UTF8Encoding(true));

        using (var store = new SettingsStore(root))
        {
            Equal(2, store.Config.Version);
            Equal("example.test", store.Config.Servers.Single().Host);
            Equal("nobu", store.Config.Servers.Single().Users.Single());
        }

        True(File.Exists(Path.Combine(root, "config.json.v1.bak")), "migration backup missing");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config.json")));
        Equal(2, document.RootElement.GetProperty("version").GetInt32());
    }
    finally
    {
        Directory.Delete(root, true);
    }
    return Task.CompletedTask;
}

static Task TestConcurrentSettingsStores()
{
    var root = TempDirectory();
    try
    {
        using var first = new SettingsStore(root);
        using var second = new SettingsStore(root);
        Equal(2, first.Config.Version);
        Equal(2, second.Config.Version);
    }
    finally
    {
        Directory.Delete(root, true);
    }
    return Task.CompletedTask;
}

static Task TestWindowsReservedNames()
{
    foreach (var name in new[] { "CON", "con.txt", "AUX.log", "COM1", "com9.bin", "LPT3" })
    {
        var rejected = false;
        try
        {
            PathSafety.ValidateChildName(name, windows: true);
        }
        catch (IOException)
        {
            rejected = true;
        }
        True(rejected, $"Windows reserved name was accepted: {name}");
    }

    PathSafety.ValidateChildName("COM10.txt", windows: true);
    PathSafety.ValidateChildName("normal-file.txt", windows: true);
    return Task.CompletedTask;
}

static async Task TestLocalCopyAndOverwrite()
{
    var root = TempDirectory();
    try
    {
        var sourceDir = Path.Combine(root, "source");
        var destinationDir = Path.Combine(root, "destination");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destinationDir);
        var source = Path.Combine(sourceDir, "sample.bin");
        var destination = Path.Combine(destinationDir, "sample.bin");
        var bytes = Enumerable.Range(0, 700_000).Select(i => (byte)(i * 31)).ToArray();
        await File.WriteAllBytesAsync(source, bytes);

        using var local = new LocalFileSystem();
        var engine = new TransferEngine();
        await engine.CopyAsync(local, source, local, destination,
            new TransferOptions(Conflict: ConflictAction.Ask), null, CancellationToken.None);
        var firstCopy = await File.ReadAllBytesAsync(destination);
        True(bytes.SequenceEqual(firstCopy), "initial copy differs");

        await File.WriteAllBytesAsync(source, bytes.Select(x => (byte)(x ^ 0x5A)).ToArray());
        await engine.CopyAsync(local, source, local, destination,
            new TransferOptions(Conflict: ConflictAction.Overwrite), null, CancellationToken.None);
        var changedSource = await File.ReadAllBytesAsync(source);
        var overwrittenDestination = await File.ReadAllBytesAsync(destination);
        True(changedSource.SequenceEqual(overwrittenDestination), "overwrite differs");
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static async Task TestDirectoryCopy()
{
    var root = TempDirectory();
    try
    {
        var source = Path.Combine(root, "source-tree");
        var destination = Path.Combine(root, "destination-tree");
        Directory.CreateDirectory(Path.Combine(source, "a", "b"));
        await File.WriteAllTextAsync(Path.Combine(source, "root.txt"), "root");
        await File.WriteAllTextAsync(Path.Combine(source, "a", "b", "deep.txt"), "deep");

        using var local = new LocalFileSystem();
        await new TransferEngine().CopyAsync(local, source, local, destination,
            new TransferOptions(), null, CancellationToken.None);

        Equal("root", await File.ReadAllTextAsync(Path.Combine(destination, "root.txt")));
        Equal("deep", await File.ReadAllTextAsync(Path.Combine(destination, "a", "b", "deep.txt")));
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static async Task TestLiveProgress()
{
    var root = TempDirectory();
    try
    {
        var source = Path.Combine(root, "large.bin");
        var destination = Path.Combine(root, "large-copy.bin");
        await File.WriteAllBytesAsync(source, Enumerable.Range(0, 900_000).Select(i => (byte)i).ToArray());
        var updates = new List<TransferProgress>();
        var progress = new InlineProgress<TransferProgress>(updates.Add);

        using var local = new LocalFileSystem();
        await new TransferEngine().CopyAsync(local, source, local, destination,
            new TransferOptions(), progress, CancellationToken.None);

        True(updates.Any(x => x.BytesTransferred > 0 && x.BytesTransferred < 900_000),
            "no in-file progress update was reported");
        Equal(900_000L, updates.Last().BytesTransferred);
        Equal(TransferState.Completed, updates.Last().State);
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static async Task TestMove()
{
    var root = TempDirectory();
    try
    {
        var source = Path.Combine(root, "source.txt");
        var destination = Path.Combine(root, "destination.txt");
        await File.WriteAllTextAsync(source, "hello");

        using var local = new LocalFileSystem();
        await new TransferEngine().CopyAsync(local, source, local, destination,
            new TransferOptions(Move: true), null, CancellationToken.None);

        True(!File.Exists(source), "move retained source");
        Equal("hello", await File.ReadAllTextAsync(destination));
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static async Task TestSkippedMoveRetainsSource()
{
    var root = TempDirectory();
    try
    {
        var source = Path.Combine(root, "source.txt");
        var destination = Path.Combine(root, "destination.txt");
        await File.WriteAllTextAsync(source, "source-data");
        await File.WriteAllTextAsync(destination, "existing-data");

        using var local = new LocalFileSystem();
        var partial = false;
        try
        {
            await new TransferEngine().CopyAsync(local, source, local, destination,
                new TransferOptions(Move: true, Conflict: ConflictAction.Skip),
                null, CancellationToken.None);
        }
        catch (PartialMoveException)
        {
            partial = true;
        }

        True(partial, "skipped move did not report partial outcome");
        True(File.Exists(source), "skipped move deleted the source");
        Equal("source-data", await File.ReadAllTextAsync(source));
        Equal("existing-data", await File.ReadAllTextAsync(destination));
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static Task TestRootProtection()
{
    using var local = new LocalFileSystem();
    var root = Path.GetPathRoot(Path.GetFullPath(Path.DirectorySeparatorChar.ToString()))!;
    var threw = false;
    try
    {
        PathSafety.ProtectRoot(root, local);
    }
    catch (IOException)
    {
        threw = true;
    }
    True(threw, "root protection accepted filesystem root");
    return Task.CompletedTask;
}

static async Task TestMoveRetainsNewEntries()
{
    var root = TempDirectory();
    try
    {
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "original.txt"), "copied");
        var late = Path.Combine(source, "created-during-transfer.txt");
        var progress = new InlineProgress<TransferProgress>(p =>
        {
            if (p.CompletedEntries == p.TotalEntries && p.State == TransferState.Running)
                File.WriteAllText(late, "must not be deleted");
        });
        using var local = new LocalFileSystem();
        var partial = false;
        try { await new TransferEngine().CopyAsync(local, source, local, destination, new(Move: true), progress, CancellationToken.None); }
        catch (PartialMoveException) { partial = true; }
        True(partial, "new source entry did not report a partial move");
        Equal("must not be deleted", await File.ReadAllTextAsync(late));
        Equal("copied", await File.ReadAllTextAsync(Path.Combine(destination, "original.txt")));
    }
    finally { Directory.Delete(root, true); }
}

static async Task TestMoveRetainsChangedFile()
{
    var root = TempDirectory();
    try
    {
        var source = Path.Combine(root, "source.txt");
        var destination = Path.Combine(root, "destination.txt");
        await File.WriteAllTextAsync(source, "original");
        var progress = new InlineProgress<TransferProgress>(p =>
        {
            if (p.CompletedEntries == p.TotalEntries && p.State == TransferState.Running)
                File.WriteAllText(source, "changed after copy");
        });
        using var local = new LocalFileSystem();
        var partial = false;
        try { await new TransferEngine().CopyAsync(local, source, local, destination, new(Move: true), progress, CancellationToken.None); }
        catch (PartialMoveException) { partial = true; }
        True(partial, "changed source did not report a partial move");
        Equal("changed after copy", await File.ReadAllTextAsync(source));
        Equal("original", await File.ReadAllTextAsync(destination));
    }
    finally { Directory.Delete(root, true); }
}

static string TempDirectory()
{
    var path = Path.Combine(Path.GetTempPath(), "my-ssh-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}");
}

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

file sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
