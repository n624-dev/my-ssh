using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class Issue05Tests : IRegressionCase
{
    public string Name => "#5 invalid transfer names allow rename, skip or all-or-nothing cancellation";
    public Task RunAsync()
    {
        using var local = new LocalFileSystem();
        var windows = new WindowsNames(local);
        var root = Path.GetTempPath();
        static FileEntry Entry(string name) => new("/source/" + name, name, EntryKind.File, 1, DateTimeOffset.UnixEpoch);

        var renamed = TransferSelectionPlanner.Prepare(windows, root, [Entry("bad:name")],
            (_, _) => new(InvalidNameAction.Rename, "valid.txt"));
        RegressionCases.Check(!renamed.Cancelled && renamed.Entries.Single().Destination.EndsWith("valid.txt"), "rename failed");
        RegressionCases.Check(renamed.Entries.Single().Source.Name == "bad:name", "rename altered source");

        var skipped = TransferSelectionPlanner.Prepare(windows, root, [Entry("CON"), Entry("日本語 report.txt")],
            (_, _) => new(InvalidNameAction.Skip));
        RegressionCases.Check(skipped.Skipped == 1 && skipped.Entries.Count == 1, "skip affected valid entry");

        var cancelled = TransferSelectionPlanner.Prepare(windows, root, [Entry("valid.txt"), Entry("bad:name")],
            (_, _) => new(InvalidNameAction.Cancel));
        RegressionCases.Check(cancelled.Cancelled && cancelled.Entries.Count == 0, "cancel left an enqueueable partial plan");

        var decisions = new Queue<InvalidNameDecision>([
            new(InvalidNameAction.Rename, "LPT1"), new(InvalidNameAction.Rename, "safe.txt")]);
        var retried = TransferSelectionPlanner.Prepare(windows, root, [Entry("CON")], (_, _) => decisions.Dequeue());
        RegressionCases.Check(decisions.Count == 0 && retried.Entries.Single().Destination.EndsWith("safe.txt"), "replacement name was not revalidated");

        var conflict = TransferSelectionPlanner.Prepare(windows, root, [Entry("A.txt"), Entry("a.txt")],
            (_, _) => new(InvalidNameAction.Rename, "other.txt"));
        RegressionCases.Check(conflict.Entries.Count == 2 && conflict.Entries[1].Destination.EndsWith("other.txt"), "selection collision was not resolved");

        PathSafety.ValidateChildName("   ", windows: false);
        var rejected = false;
        try { PathSafety.ValidateChildName("   ", windows: true); } catch (IOException) { rejected = true; }
        RegressionCases.Check(rejected, "Windows whitespace name was accepted");
        return Task.CompletedTask;
    }

    private sealed class WindowsNames(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public override StringComparison PathComparison => StringComparison.OrdinalIgnoreCase;
        public override string Join(string directory, string name)
        {
            PathSafety.ValidateChildName(name, windows: true);
            return Path.Combine(directory, name);
        }
    }
}
