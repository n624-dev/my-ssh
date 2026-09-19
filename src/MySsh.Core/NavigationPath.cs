namespace MySsh.Core;

public static class NavigationPath
{
    public static string Resolve(string current, string input, bool remote)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        if (!remote) return Path.GetFullPath(input, current);
        if (!current.StartsWith('/')) throw new IOException("The remote pane must have an absolute current path.");
        // Leave dot segments for server REALPATH: collapsing them locally could
        // change the meaning of paths that traverse a symbolic-link directory.
        return input.StartsWith('/') ? input : current.TrimEnd('/') + "/" + input;
    }
}
