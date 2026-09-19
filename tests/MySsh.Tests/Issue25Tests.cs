using System.Diagnostics;
using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class Issue25Tests : IRegressionCase
{
    public string Name => "#25 Windows cloud placeholders are distinct from symbolic links and junctions";

    public async Task RunAsync()
    {
        const FileAttributes reparse = FileAttributes.ReparsePoint;
        foreach (var directory in new[] { false, true })
        {
            var attributes = reparse | (directory ? FileAttributes.Directory : FileAttributes.Normal);
            for (uint variant = 0; variant < 16; variant++)
                RegressionCases.Check(WindowsEntryKind.Classify(attributes, 0x9000001A | (variant << 12)) ==
                    (directory ? EntryKind.Directory : EntryKind.File), "Cloud placeholder misclassified.");
            foreach (var tag in new uint[] { 0xA000000C, 0xA0000003 })
                RegressionCases.Check(WindowsEntryKind.Classify(attributes, tag) == EntryKind.SymbolicLink, "Link tag misclassified.");
            RegressionCases.Check(WindowsEntryKind.Classify(attributes, 0x8000001B) == EntryKind.Other, "Unknown tag treated as a regular file.");
        }
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestDirectory();
        using var fs = new LocalFileSystem();
        var target = root.File("target directory");
        var junction = root.File("junction directory");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "file"), "retained");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", junction, target }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        RegressionCases.Check(process.ExitCode == 0, "Cannot create the junction fixture.");
        try
        {
            var entry = await fs.StatAsync(junction, CancellationToken.None);
            RegressionCases.Check(entry?.Kind == EntryKind.SymbolicLink && entry.LinkTarget is not null, "Native junction tag was not recognized.");
            await fs.DeleteAsync(junction, false, CancellationToken.None);
            RegressionCases.Check(File.Exists(Path.Combine(target, "file")), "Deleting a junction deleted its target.");
        }
        finally { if (Directory.Exists(junction)) Directory.Delete(junction); }
    }
}
