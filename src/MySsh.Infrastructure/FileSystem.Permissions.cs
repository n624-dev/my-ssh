using MySsh.Core;

namespace MySsh.Infrastructure;

public sealed partial class LocalFileSystem : IPermissionFileSystem
{
    public async Task SetPermissionsAsync(string path, uint mode, CancellationToken cancellationToken)
    {
        PermissionMode.Validate(mode);
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("POSIX permissions are not supported on Windows.");
        var entry = await StatAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException(path);
        if (entry.Kind is not (EntryKind.File or EntryKind.Directory))
            throw new IOException("Permissions may only be changed on a regular file or directory, not a link.");
        cancellationToken.ThrowIfCancellationRequested();
        File.SetUnixFileMode(path, (UnixFileMode)mode);
    }
}

public sealed partial class SftpFileSystem : IPermissionFileSystem
{
    public async Task SetPermissionsAsync(string path, uint mode, CancellationToken cancellationToken)
    {
        PermissionMode.Validate(mode);
        ThrowIfDisposed();
        var session = _session;
        var entry = await session.StatAsync(path, followLinks: false, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException(path);
        if (Kind(entry.Mode) is not (EntryKind.File or EntryKind.Directory))
            throw new IOException("Permissions may only be changed on a regular file or directory, not a link.");
        await session.SetPermissionsAsync(path, mode, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed partial class SftpSession
{
    internal async Task SetPermissionsAsync(string path, uint mode, CancellationToken cancellationToken)
    {
        PermissionMode.Validate(mode);
        var packet = await RequestAsync(FxpSetstat, writer =>
        {
            WriteString(writer, path);
            WriteUInt32(writer, AttrPermissions);
            WriteUInt32(writer, mode);
        }, cancellationToken).ConfigureAwait(false);
        ExpectOk(packet);
    }
}
