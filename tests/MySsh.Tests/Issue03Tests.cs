using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class Issue03Tests : IRegressionCase
{
    public string Name => "#3 edit drafts survive conflicts and failed uploads until save or explicit discard";

    public async Task RunAsync()
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        var original = root.File("original.txt");
        var drafts = root.File("drafts");
        await File.WriteAllTextAsync(original, "original");
        var edit = await EditSession.OpenAsync(local, original, drafts, "LOCAL", CancellationToken.None);
        edit.WriteText("my edits");
        await File.WriteAllTextAsync(original, "someone else's changed contents");
        await RegressionCases.ThrowsAsync<IOException>(() => edit.SaveAsync(null, CancellationToken.None));
        RegressionCases.Check(edit.ReadText() == "my edits", "conflict destroyed draft");
        RegressionCases.Check(await File.ReadAllTextAsync(original) == "someone else's changed contents", "conflict overwrote original");
        var saveAs = root.File("recovered.txt");
        await edit.SaveAsync(saveAs, CancellationToken.None);
        RegressionCases.Check(await File.ReadAllTextAsync(saveAs) == "my edits", "save as lost edits");
        RegressionCases.Check(edit.Saved && !Directory.Exists(edit.DraftDirectory), "successful save did not finish draft");

        var failing = new FailingDestination(local);
        var retry = await EditSession.OpenAsync(failing, original, drafts, "LOCAL", CancellationToken.None);
        // External editors modify the draft file directly; the same recovery path must work.
        await File.WriteAllTextAsync(retry.DraftPath, "external editor contents");
        failing.FailWrites = true;
        await RegressionCases.ThrowsAsync<IOException>(() => retry.SaveAsync(null, CancellationToken.None));
        RegressionCases.Check(await File.ReadAllTextAsync(retry.DraftPath) == "external editor contents", "failed upload removed external draft");
        failing.FailWrites = false;
        await retry.SaveAsync(null, CancellationToken.None);
        RegressionCases.Check(await File.ReadAllTextAsync(original) == "external editor contents", "retry failed");

        var keep = await EditSession.OpenAsync(local, original, drafts, "LOCAL", CancellationToken.None);
        keep.WriteText("keep this draft");
        var second = await EditSession.OpenAsync(local, original, drafts, "LOCAL", CancellationToken.None);
        RegressionCases.Check(second.DraftDirectory != keep.DraftDirectory, "sessions share a draft directory");
        RegressionCases.Check(File.Exists(Path.Combine(keep.DraftDirectory, "recovery.json")), "recovery manifest missing");
        RegressionCases.Check(keep.ReadText() == "keep this draft", "another edit overwrote draft");
        keep.Discard();
        RegressionCases.Check(!Directory.Exists(keep.DraftDirectory), "explicit discard failed");
        RegressionCases.Check(File.Exists(second.DraftPath), "discard removed another draft");
        second.Discard();
    }

    private sealed class FailingDestination(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public bool FailWrites { get; set; }
        public override Task<Stream> OpenWriteAsync(string path, bool createNew, CancellationToken ct) =>
            FailWrites ? throw new IOException("Synthetic upload failure") : base.OpenWriteAsync(path, createNew, ct);
    }
}
