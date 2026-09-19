namespace MySsh.Core;

public sealed record PathCompletionResult(string Text, string[] Candidates);

/// <summary>Completes one path without changing the process working directory or invoking a shell.</summary>
public static class PathCompletion
{
    public static async Task<PathCompletionResult> CompleteAsync(IFileSystem fs, string currentDirectory,
        string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        var windows = !fs.IsRemote && OperatingSystem.IsWindows();
        var separator = windows ? "\\" : "/";
        var index = text.LastIndexOf('/');
        if (windows) index = Math.Max(index, text.LastIndexOf('\\'));
        var prefix = index < 0 ? "" : text[..(index + 1)];
        var leaf = text[(index + 1)..];
        var directory = NavigationPath.Resolve(currentDirectory, prefix.Length == 0 ? "." : prefix, fs.IsRemote);
        var resolved = await fs.CanonicalAsync(directory, cancellationToken).ConfigureAwait(false);
        var entries = await fs.ListAsync(resolved, cancellationToken).ConfigureAwait(false);
        var matches = entries.Where(entry => entry.Name is not ("." or "..") &&
                entry.Name.StartsWith(leaf, fs.PathComparison))
            .OrderBy(entry => entry.Kind == EntryKind.Directory ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => entry.Name + (entry.Kind == EntryKind.Directory ? separator : ""))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (matches.Length == 0) return new(text, []);
        var common = matches[0];
        while (common.Length > 0 && matches.Any(candidate => !candidate.StartsWith(common, fs.PathComparison)))
            common = common[..^1];
        if (common.Length > 0 && char.IsHighSurrogate(common[^1])) common = common[..^1];
        var completed = common.Length >= leaf.Length ? prefix + common : text;
        return new(completed, matches.Select(name => prefix + name).ToArray());
    }
}
