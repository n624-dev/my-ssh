using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class Issue09Tests : IRegressionCase
{
    public string Name => "#9 commit refuses destination changes, including equal-size and timestamp-preserving edits";

    public async Task RunAsync()
    {
        foreach (var mutation in new[] { "rewrite", "delete", "create" })
        foreach (var move in new[] { false, true })
        {
            using var root = new TestDirectory();
            using var local = new LocalFileSystem();
            var source = root.File("source.bin");
            var target = root.File("target.bin");
            await File.WriteAllBytesAsync(source, new byte[700_001]);
            if (mutation != "create") await File.WriteAllTextAsync(target, "old-value");
            var before = await local.StatAsync(target, CancellationToken.None);
            var changed = false;
            var progress = new InlineProgress(value =>
            {
                if (changed || value.BytesTransferred == 0) return;
                changed = true;
                if (mutation == "delete") File.Delete(target);
                else
                {
                    File.WriteAllText(target, "new-value");
                    if (before is not null) File.SetLastWriteTimeUtc(target, before.Modified.UtcDateTime);
                }
            });
            var error = await RegressionCases.ThrowsAsync<IOException>(() => new TransferEngine().CopyAsync(
                local, source, local, target, new(Move: move, Conflict: ConflictAction.Overwrite),
                progress, CancellationToken.None));
            RegressionCases.Check(changed && error.Message.Contains("destination changed", StringComparison.OrdinalIgnoreCase),
                "destination change was not reported");
            RegressionCases.Check(File.Exists(source) && new FileInfo(source).Length == 700_001,
                "move deleted its source after a destination conflict");
            if (mutation == "delete") RegressionCases.Check(!File.Exists(target), "deleted target was recreated");
            else RegressionCases.Check(await File.ReadAllTextAsync(target) == "new-value", "external write was overwritten");
        }

        using (var root = new TestDirectory())
        using (var local = new LocalFileSystem())
        {
            var source = root.File("empty.bin");
            var target = root.File("existing.bin");
            await File.WriteAllBytesAsync(source, []);
            await File.WriteAllTextAsync(target, "old contents");
            await new TransferEngine().CopyAsync(local, source, local, target,
                new(Conflict: ConflictAction.Overwrite), null, CancellationToken.None);
            RegressionCases.Check(new FileInfo(target).Length == 0, "unchanged target could not be replaced");
        }

        if (!OperatingSystem.IsWindows()) await LinkConflictAsync();
    }

    private static async Task LinkConflictAsync()
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        var source = root.File("source-link");
        var target = root.File("target-link");
        File.CreateSymbolicLink(source, "source-value");
        File.CreateSymbolicLink(target, "old-value");
        var destination = new MutatingLinks(local, target);
        await RegressionCases.ThrowsAsync<IOException>(() => new TransferEngine().CopyAsync(local, source,
            destination, target, new(Move: true, Conflict: ConflictAction.Overwrite), null, CancellationToken.None));
        RegressionCases.Check(new FileInfo(target).LinkTarget == "external-value", "changed destination link was replaced");
        RegressionCases.Check(new FileInfo(source).LinkTarget == "source-value", "source link was deleted");
        RegressionCases.Check(Directory.GetFileSystemEntries(root.Path).Length == 2, "temporary link leaked after conflict");
    }

    private sealed class MutatingLinks(IFileSystem inner, string target) : DelegatingFileSystem(inner)
    {
        public override async Task CreateLinkAsync(string path, string value, CancellationToken ct)
        {
            await base.CreateLinkAsync(path, value, ct);
            File.Delete(target);
            File.CreateSymbolicLink(target, "external-value");
        }
    }

    private sealed class InlineProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }
}
