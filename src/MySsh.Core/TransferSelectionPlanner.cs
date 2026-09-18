namespace MySsh.Core;

public enum InvalidNameAction { Rename, Skip, Cancel }
public sealed record InvalidNameDecision(InvalidNameAction Action, string? Replacement = null);
public sealed record TransferSelection(FileEntry Source, string Destination);
public sealed record TransferSelectionPlan(bool Cancelled, IReadOnlyList<TransferSelection> Entries, int Skipped);

/// <summary>Resolves invalid target names for the whole selection before any job is enqueued.</summary>
public static class TransferSelectionPlanner
{
    public static TransferSelectionPlan Prepare(IFileSystem destination, string directory,
        IEnumerable<FileEntry> entries, Func<string, string, InvalidNameDecision> resolve)
    {
        var selected = new List<TransferSelection>();
        var targets = new HashSet<string>(StringComparer.FromComparison(destination.PathComparison));
        var skipped = 0;
        foreach (var entry in entries)
        {
            var name = entry.Name;
            while (true)
            {
                try
                {
                    var path = destination.Join(directory, name);
                    if (!targets.Add(path)) throw new IOException("Another selected entry uses this destination name.");
                    selected.Add(new(entry, path));
                    break;
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
                {
                    var decision = resolve(name, ex.Message);
                    if (decision.Action == InvalidNameAction.Cancel ||
                        (decision.Action == InvalidNameAction.Rename && decision.Replacement is null))
                        return new(true, Array.Empty<TransferSelection>(), skipped);
                    if (decision.Action == InvalidNameAction.Skip) { skipped++; break; }
                    name = decision.Replacement!;
                }
            }
        }
        return new(false, selected, skipped);
    }
}
