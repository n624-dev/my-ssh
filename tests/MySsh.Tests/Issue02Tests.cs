using System.Runtime.InteropServices;
using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class Issue02Tests : IRegressionCase
{
    public string Name => "#2 reject self, hard-link and descendant transfers before writing";

    public async Task RunAsync()
    {
        using var root = new TestDirectory();
        using var left = new LocalFileSystem();
        using var right = new LocalFileSystem();
        var source = root.File("source.txt");
        await File.WriteAllTextAsync(source, "must survive");
        Directory.CreateDirectory(root.File("sub"));
        foreach (var move in new[] { false, true })
        foreach (var conflict in new[] { ConflictAction.Ask, ConflictAction.Overwrite })
        {
            await Reject(left, source, right, source, move, conflict);
            await Reject(left, source, right, root.File("sub/../source.txt"), move, conflict);
            RegressionCases.Check(await File.ReadAllTextAsync(source) == "must survive", "self-transfer changed source");
        }

        var hardLink = root.File("hard-link.txt");
        var linked = OperatingSystem.IsWindows()
            ? CreateHardLink(hardLink, source, nint.Zero)
            : Link(source, hardLink) == 0;
        RegressionCases.Check(linked, "could not create isolated hard-link fixture");
        await Reject(left, source, right, hardLink, true, ConflictAction.Overwrite);
        RegressionCases.Check(await File.ReadAllTextAsync(hardLink) == "must survive", "hard link changed");

        var tree = root.File("tree");
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(Path.Combine(tree, "original.txt"), "original");
        foreach (var move in new[] { false, true })
        {
            await Reject(left, tree, right, tree, move, ConflictAction.Overwrite);
            await Reject(left, tree, right, Path.Combine(tree, "new", "nested"), move, ConflictAction.Overwrite);
        }
        RegressionCases.Check(!Directory.Exists(Path.Combine(tree, "new")), "descendant rejection wrote data");

        if (!OperatingSystem.IsWindows())
        {
            var alias = root.File("alias");
            Directory.CreateSymbolicLink(alias, tree);
            await Reject(left, tree, right, Path.Combine(alias, "child"), true, ConflictAction.Overwrite);
            await Reject(left, Path.Combine(tree, "original.txt"), right,
                Path.Combine(alias, "original.txt"), true, ConflictAction.Overwrite);
        }

        // Similar spelling is not ancestry; this ordinary operation must still work.
        var distinct = root.File("tree-other");
        await new TransferEngine().CopyAsync(left, tree, right, distinct, new(), null, CancellationToken.None);
        RegressionCases.Check(await File.ReadAllTextAsync(Path.Combine(distinct, "original.txt")) == "original", "sibling copy failed");

        // Different sessions in the same named remote namespace are protected too.
        var remote1 = new RemoteScope(left, "test-server:user");
        var remote2 = new RemoteScope(right, "test-server:user");
        await Reject(remote1, source.Replace('\\', '/'), remote2, source.Replace('\\', '/'), true, ConflictAction.Overwrite);
    }

    private static async Task Reject(IFileSystem source, string sourcePath, IFileSystem target,
        string targetPath, bool move, ConflictAction conflict)
    {
        var error = await RegressionCases.ThrowsAsync<IOException>(() => new TransferEngine().CopyAsync(
            source, sourcePath, target, targetPath, new(Move: move, Conflict: conflict), null, CancellationToken.None));
        RegressionCases.Check(error.Message.Contains("same", StringComparison.OrdinalIgnoreCase) ||
            error.Message.Contains("descendant", StringComparison.OrdinalIgnoreCase), "unexpected rejection: " + error.Message);
    }

    private sealed class RemoteScope(IFileSystem inner, string scope) : DelegatingFileSystem(inner), IFileSystemNamespace
    {
        public override bool IsRemote => true;
        public string PathNamespace => scope;
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string oldPath, string newPath);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newPath, string existing, nint security);
}
