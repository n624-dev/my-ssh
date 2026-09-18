using MySsh.Infrastructure;

internal sealed class Issue07Tests : IRegressionCase
{
    public string Name => "#7 editing releases original handles before the editor and atomic save";

    public async Task RunAsync()
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        var path = root.File("editable.txt");
        await File.WriteAllTextAsync(path, "original");

        // EditSession, shared by both editors since #3, must finish reading the
        // source before handing a draft to the UI. On Windows an old FileShare.Read
        // handle prevents BOTH the exclusive open and the later atomic replacement.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var session = await EditSession.OpenAsync(local, path, root.File("drafts"),
                "LOCAL", CancellationToken.None);
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                RegressionCases.Check(exclusive.Length > 0, "original was not readable exclusively");
            session.ReadText();
            session.WriteText("edited " + attempt);
            await session.SaveAsync(null, CancellationToken.None);
            RegressionCases.Check(session.Saved, "edit did not commit");
            RegressionCases.Check(await File.ReadAllTextAsync(path) == "edited " + attempt,
                "saved content differs");
            using var afterSave = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        // Keeping a draft must not keep a file handle on the live original.
        var kept = await EditSession.OpenAsync(local, path, root.File("drafts"), "LOCAL", CancellationToken.None);
        kept.WriteText("retained draft");
        var renamed = root.File("renamed.txt");
        File.Move(path, renamed);
        File.Move(renamed, path);
        RegressionCases.Check(kept.ReadText() == "retained draft", "rename damaged the draft");
        kept.Discard();
    }
}
