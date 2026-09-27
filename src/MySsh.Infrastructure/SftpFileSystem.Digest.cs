namespace MySsh.Infrastructure;

public sealed partial class SftpFileSystem : IFileDigestProvider
{
    private bool _digestUnavailable;

    async Task<byte[]?> IFileDigestProvider.TryReadDigestAsync(string path, long length, CancellationToken ct)
    {
        ThrowIfDisposed();
        // Scripted/in-process SFTP fixtures must never start an external SSH client.
        if (_digestUnavailable || !_session.HasProcess) return null;
        var before = await StatAsync(path, ct).ConfigureAwait(false);
        if (before is not { Kind: MySsh.Core.EntryKind.File } || before.Length != length)
            throw new IOException("File changed before remote checksum verification.");
        var digest = await RemoteFileDigest.TryReadAsync(_connection, path, length, ct).ConfigureAwait(false);
        if (digest is null) { _digestUnavailable = true; return null; }
        if (!DestinationSnapshot.SameMetadata(before, await StatAsync(path, ct).ConfigureAwait(false)))
            throw new IOException("File changed during remote checksum verification.");
        return digest;
    }
}
