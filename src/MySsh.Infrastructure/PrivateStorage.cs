using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MySsh.Infrastructure;

/// <summary>Creates staging data with restrictive permissions before writing its first byte.</summary>
internal static class PrivateStorage
{
    internal static void EnsureDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        RejectLink(directory);
        if (OperatingSystem.IsWindows())
        {
            var security = WindowsDirectorySecurity();
            directory.Create(security);
            RejectLink(directory);
            // Create does not update an existing directory's ACL.
            directory.SetAccessControl(security);
        }
        else
        {
            const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(path, mode);
            RejectLink(directory);
            File.SetUnixFileMode(path, mode);
        }
    }

    internal static FileStream CreateFile(string path, FileOptions options = FileOptions.None)
    {
        if (OperatingSystem.IsWindows())
            return new FileInfo(path).Create(FileMode.CreateNew,
                FileSystemRights.Read | FileSystemRights.Write, FileShare.None, 64 * 1024,
                options, WindowsFileSecurity());
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
            BufferSize = 64 * 1024, Options = options,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }

    private static void RejectLink(DirectoryInfo directory)
    {
        directory.Refresh();
        if (directory.LinkTarget is not null ||
            (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            throw new IOException("The private draft directory must not be a link or reparse point.");
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity WindowsDirectorySecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("Cannot determine the current Windows user.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity WindowsFileSecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("Cannot determine the current Windows user.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
