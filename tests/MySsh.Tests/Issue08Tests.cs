using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class Issue08Tests : IRegressionCase
{
    public string Name => "#8 recursive destination collisions are rejected before copy or move mutates files";

    public async Task RunAsync()
    {
        TransferEngine.ValidateDestinationNames([("one", "/target/A.txt"), ("two", "/target/a.txt")],
            StringComparison.Ordinal);
        await RegressionCases.ThrowsAsync<IOException>(() =>
        {
            TransferEngine.ValidateDestinationNames([("one", "/target/A.txt"), ("two", "/target/a.txt")],
                StringComparison.OrdinalIgnoreCase);
            return Task.CompletedTask;
        });

        // Virtual source names make this portable: Windows runners cannot create
        // A.txt and a.txt as distinct entries on their normal filesystem.
        foreach (var folders in new[] { false, true })
        foreach (var move in new[] { false, true })
        foreach (var conflict in new[] { ConflictAction.Ask, ConflictAction.Overwrite, ConflictAction.Skip })
        {
            using var root = new TestDirectory();
            using var local = new LocalFileSystem();
            var source = root.File("source");
            var destination = root.File("destination");
            Directory.CreateDirectory(source);
            if (folders)
            {
                Directory.CreateDirectory(Path.Combine(source, "one"));
                Directory.CreateDirectory(Path.Combine(source, "two"));
                await File.WriteAllTextAsync(Path.Combine(source, "one", "value.txt"), "first");
                await File.WriteAllTextAsync(Path.Combine(source, "two", "value.txt"), "second");
            }
            else
            {
                await File.WriteAllTextAsync(Path.Combine(source, "one"), "first");
                await File.WriteAllTextAsync(Path.Combine(source, "two"), "second");
            }
            var virtualSource = new RenamedSource(local, source);
            var guardedTarget = new ReadOnlyTarget(local);
            var error = await RegressionCases.ThrowsAsync<IOException>(() => new TransferEngine().CopyAsync(
                virtualSource, source, guardedTarget, destination, new(Move: move, Conflict: conflict),
                null, CancellationToken.None));
            RegressionCases.Check(error.Message.Contains("collision", StringComparison.OrdinalIgnoreCase),
                "transfer did not explain the name collision");
            RegressionCases.Check(guardedTarget.Mutations == 0 && !Directory.Exists(destination),
                "collision was detected after the destination was modified");
            RegressionCases.Check(Directory.GetFileSystemEntries(source).Length == 2, "move deleted a source entry");
            var first = folders ? Path.Combine(source, "one", "value.txt") : Path.Combine(source, "one");
            var second = folders ? Path.Combine(source, "two", "value.txt") : Path.Combine(source, "two");
            RegressionCases.Check(await File.ReadAllTextAsync(first) == "first" &&
                await File.ReadAllTextAsync(second) == "second", "source content was lost");
        }

        // Top-level selections already use the #5 rename/skip/cancel preflight.
        using var fs = new LocalFileSystem();
        var insensitive = new ReadOnlyTarget(fs);
        var entries = new[]
        {
            new FileEntry("/src/A.txt", "A.txt", EntryKind.File, 1, DateTimeOffset.UnixEpoch),
            new FileEntry("/src/a.txt", "a.txt", EntryKind.File, 1, DateTimeOffset.UnixEpoch)
        };
        var target = Path.GetTempPath();
        var renamed = TransferSelectionPlanner.Prepare(insensitive, target, entries,
            (_, _) => new(InvalidNameAction.Rename, "second.txt"));
        RegressionCases.Check(!renamed.Cancelled && renamed.Entries.Count == 2 &&
            !renamed.Entries[0].Destination.Equals(renamed.Entries[1].Destination, StringComparison.OrdinalIgnoreCase),
            "selection preflight did not allow resolving a collision by renaming");
    }

    private sealed class RenamedSource(IFileSystem inner, string root) : DelegatingFileSystem(inner)
    {
        public override async Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct)
        {
            var entries = await base.ListAsync(path, ct);
            if (path != root) return entries;
            return entries.Select(entry => entry with { Name = entry.Name == "one" ? "A" : "a" }).ToArray();
        }
    }

    private sealed class ReadOnlyTarget(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public int Mutations { get; private set; }
        public override StringComparison PathComparison => StringComparison.OrdinalIgnoreCase;
        public override Task CreateDirectoryAsync(string path, CancellationToken ct)
        {
            Mutations++;
            throw new InvalidOperationException("Unexpected destination mutation.");
        }
        public override Task<Stream> OpenWriteAsync(string path, bool createNew, CancellationToken ct)
        {
            Mutations++;
            throw new InvalidOperationException("Unexpected destination mutation.");
        }
    }
}
