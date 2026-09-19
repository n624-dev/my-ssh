using System.Security.Cryptography;
using MySsh.Core;

namespace MySsh.Infrastructure;

internal static class SourceIntegrity
{
    /// <summary>
    /// Verify both the planned source version and the actual temporary contents
    /// before publication. Hashing catches same-size writes within coarse timestamp
    /// resolution; comparing the temporary data also rejects mixed-version reads.
    /// This is not a filesystem snapshot or a lock on external writers.
    /// </summary>
    public static async Task<byte[]> VerifyAsync(IFileSystem source, string sourcePath, FileEntry expected,
        IFileSystem destination, string partialPath, CancellationToken ct)
    {
        var before = await source.StatAsync(sourcePath, ct).ConfigureAwait(false);
        if (!DestinationSnapshot.SameMetadata(expected, before)) throw Changed(sourcePath);
        var copied = await DestinationSnapshot.ReadDigestAsync(destination, partialPath, expected.Length, ct).ConfigureAwait(false);
        var current = await DestinationSnapshot.ReadDigestAsync(source, sourcePath, expected.Length, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(copied, current) ||
            !DestinationSnapshot.SameMetadata(expected, await source.StatAsync(sourcePath, ct).ConfigureAwait(false)))
            throw Changed(sourcePath);
        return copied;
    }

    private static IOException Changed(string path) => new(
        $"The source changed or transferred data does not match: {path}. No destination was committed; the source and temporary data were retained.");
}
