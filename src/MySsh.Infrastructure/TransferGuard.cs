using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MySsh.Core;

namespace MySsh.Infrastructure;

internal static class TransferGuard
{
    public static async Task ValidateAsync(IFileSystem source, string sourcePath,
        IFileSystem destination, string destinationPath, EntryKind sourceKind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Equals(Scope(source), Scope(destination))) return;

        var sourceName = await ResolveAsync(source, sourcePath, ct).ConfigureAwait(false);
        var targetName = await ResolveAsync(destination, destinationPath, ct).ConfigureAwait(false);
        var comparison = source.PathComparison;
        if (sourceName.Equals(targetName, comparison))
            throw new IOException("Source and destination are the same entry; no data was changed.");

        // Native local identities additionally detect hard links and bind-mounted aliases.
        var local = !source.IsRemote && !destination.IsRemote;
        var sourceId = local ? LocalIdentity(sourceName) : null;
        if (sourceId is not null && sourceId == LocalIdentity(targetName))
            throw new IOException("Source and destination identify the same file; no data was changed.");

        if (sourceKind != EntryKind.Directory) return;
        var current = targetName;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (current.Equals(sourceName, comparison) ||
                (sourceId is not null && sourceId == LocalIdentity(current)))
                throw new IOException("A directory cannot be copied or moved into itself or a descendant.");
            var parent = destination.Parent(current);
            if (parent.Equals(current, comparison)) break;
            current = parent;
        }
    }

    private static object Scope(IFileSystem fs) => fs is IFileSystemNamespace named
        ? named.PathNamespace : fs.IsRemote ? fs : "local";

    private static async Task<string> ResolveAsync(IFileSystem fs, string path, CancellationToken ct)
    {
        if (!fs.IsRemote) return ResolveLocal(path);
        // Resolve existing ancestor links with REALPATH, without following a final
        // symlink (copying a link is not copying its target). Destinations can be new.
        var leaf = path.TrimEnd('/').Split('/').Last();
        if (path == "/") return await fs.CanonicalAsync(path, ct).ConfigureAwait(false);
        var pending = new Stack<string>();
        pending.Push(leaf);
        var current = fs.Parent(path);
        while (await fs.StatAsync(current, ct).ConfigureAwait(false) is null)
        {
            var parent = fs.Parent(current);
            if (parent == current) throw new IOException("No existing destination ancestor.");
            pending.Push(current.TrimEnd('/').Split('/').Last());
            current = parent;
        }
        current = await fs.CanonicalAsync(current, ct).ConfigureAwait(false);
        while (pending.TryPop(out var component)) current = fs.Join(current, component);
        return current;
    }

    private static string ResolveLocal(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            if (index == parts.Length - 1) break;
            var directory = new DirectoryInfo(current);
            if (directory.LinkTarget is not null)
                current = directory.ResolveLinkTarget(true)?.FullName
                    ?? throw new IOException("Cannot resolve a directory link in the transfer path.");
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    private static string? LocalIdentity(string path)
    {
        // Missing ancestors are normal for a new destination; permission errors
        // are not treated as evidence that two objects differ.
        if (OperatingSystem.IsLinux())
        {
            var data = new byte[256];
            try
            {
                if (Statx(-100, path, 0x100, 0x100, data) == 0)
                {
                    if ((BitConverter.ToUInt32(data, 0) & 0x100) == 0) return null;
                    return $"{BitConverter.ToUInt32(data, 136)}:{BitConverter.ToUInt32(data, 140)}:{BitConverter.ToUInt64(data, 32)}";
                }
                var error = Marshal.GetLastPInvokeError();
                if (error is 2 or 20 or 38 or 95) return null;
                throw new IOException("Cannot verify local file identity.", new Win32Exception(error));
            }
            catch (EntryPointNotFoundException) { return null; }
        }
        if (OperatingSystem.IsWindows())
        {
            // Zero desired access reads metadata only. Share delete to avoid
            // unnecessarily locking an application's document. Do not follow links.
            using var handle = CreateFile(path, 0, 7, nint.Zero, 3, 0x02200000, nint.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is 2 or 3) return null;
                throw new IOException("Cannot verify local file identity.", new Win32Exception(error));
            }
            var information = new byte[52];
            if (!GetFileInformationByHandle(handle, information))
                throw new IOException("Cannot read local file identity.", new Win32Exception(Marshal.GetLastPInvokeError()));
            return $"{BitConverter.ToUInt32(information, 28)}:{BitConverter.ToUInt32(information, 44)}:{BitConverter.ToUInt32(information, 48)}";
        }
        return null;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int fd, string path, int flags, uint mask, [Out] byte[] data);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        nint attributes, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, [Out] byte[] data);
}
