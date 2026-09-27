using System.Security.Cryptography;
using MySsh.Core;

namespace MySsh.Infrastructure;

/// <summary>
/// An optimistic destination version. All reads finish before returning; no live
/// original handle is held over the transfer. Standard SFTP rename is not CAS, so
/// this detects changes during the transfer but cannot lock out an uncooperative
/// writer between the final verification and rename.
/// </summary>
internal sealed class DestinationSnapshot(FileEntry? entry, byte[]? digest)
{
    internal static async Task<DestinationSnapshot> CaptureAsync(IFileSystem fs, string path,
        FileEntry? expected, CancellationToken ct, Action<string>? report = null)
    {
        var before = await fs.StatAsync(path, ct).ConfigureAwait(false);
        if (!SameMetadata(expected, before)) throw Changed(path);
        var hash = before is { Kind: EntryKind.File }
            ? await ReadDigestAsync(fs, path, before.Length, ct, report).ConfigureAwait(false) : null;
        var after = await fs.StatAsync(path, ct).ConfigureAwait(false);
        if (!SameMetadata(before, after)) throw Changed(path);
        return new(before, hash);
    }

    internal async Task VerifyAsync(IFileSystem fs, string path, CancellationToken ct, Action<string>? report = null)
    {
        var current = await fs.StatAsync(path, ct).ConfigureAwait(false);
        if (!SameMetadata(entry, current)) throw Changed(path);
        if (digest is not null && current is not null)
        {
            var hash = await ReadDigestAsync(fs, path, current.Length, ct, report).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(digest, hash)) throw Changed(path);
            if (!SameMetadata(current, await fs.StatAsync(path, ct).ConfigureAwait(false))) throw Changed(path);
        }
    }

    internal static bool SameMetadata(FileEntry? left, FileEntry? right) =>
        left is null ? right is null : right is not null &&
        left.Kind == right.Kind && left.Length == right.Length &&
        left.Modified == right.Modified && left.Mode == right.Mode && left.LinkTarget == right.LinkTarget;

    // Bound reads by the captured length plus one byte. A growing file must not
    // turn a version check into an unbounded read or hide a concurrent change.
    internal static async Task<byte[]> ReadDigestAsync(IFileSystem fs, string path, long length, CancellationToken ct,
        Action<string>? report = null)
    {
        if (length < 0) throw new IOException("Invalid file length.");
        if (length >= RemoteFileDigest.MinimumLength && fs is IFileDigestProvider provider)
        {
            report?.Invoke("server checksum...");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var pending = provider.TryReadDigestAsync(path, length, ct);
            while (!pending.IsCompleted)
            {
                if (await Task.WhenAny(pending, Task.Delay(1000, ct)).ConfigureAwait(false) == pending) break;
                // Await the provider on cancellation too, so no helper outlives verification.
                if (ct.IsCancellationRequested) break;
                report?.Invoke($"server checksum ({watch.Elapsed.TotalSeconds:F0}s)...");
            }
            var remote = await pending.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (remote is { Length: 32 }) { report?.Invoke("server checksum received"); return remote; }
        }
        var method = fs.IsRemote ? "reading via SFTP" : "reading local file";
        report?.Invoke($"{method} 0%");
        var stream = await fs.OpenReadAsync(path, ct).ConfigureAwait(false);
        await using var lifetime = stream.ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[SftpSession.PipelineBytes];
        var reading = System.Diagnostics.Stopwatch.StartNew();
        long remaining = length;
        while (remaining > 0)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), ct).ConfigureAwait(false);
            if (count == 0) throw Changed(path);
            hash.AppendData(buffer, 0, count);
            remaining -= count;
            var rate = (length - remaining) / Math.Max(0.001, reading.Elapsed.TotalSeconds) / 1_000_000;
            report?.Invoke($"{method} {(length - remaining) * 100.0 / length:F1}% ({rate:F1} MB/s)");
        }
        if (await stream.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false) != 0) throw Changed(path);
        return hash.GetHashAndReset();
    }

    private static IOException Changed(string path) => new(
        $"The destination changed during the transfer: {path}. It was not replaced; review the conflict before retrying. Partial data and any move source have been retained.");
}
