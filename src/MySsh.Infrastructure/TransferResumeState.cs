using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using MySsh.Core;

namespace MySsh.Infrastructure;

/// <summary>
/// Per-job receipts for acknowledged commits. This is in-memory resume state,
/// not a claim of crash recovery. Never infer ownership from an existing filename.
/// </summary>
public sealed class TransferResumeState
{
    private readonly Dictionary<(string Source, string Destination), Receipt> _committed = new();
    private readonly HashSet<(string Source, string Destination)> _createdDirectories = new();
    private Binding? _binding;
    private int _running;

    internal IDisposable Enter(IFileSystem source, string sourcePath, IFileSystem destination, string destinationPath)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("Resume state cannot be shared by simultaneous transfers.");
        try
        {
            var binding = new Binding(Namespace(source), sourcePath, Namespace(destination), destinationPath);
            if (_binding is not null && _binding != binding)
                throw new InvalidOperationException("Resume state belongs to a different transfer. Start a new job for a different destination.");
            _binding = binding;
            return new Lease(this);
        }
        catch { Volatile.Write(ref _running, 0); throw; }
    }

    private static string Namespace(IFileSystem fs) => fs is IFileSystemNamespace namespaced
        ? namespaced.PathNamespace : !fs.IsRemote ? "local"
        : fs.GetType().FullName + ":" + RuntimeHelpers.GetHashCode(fs);

    internal void Record(string sourcePath, FileEntry source, string destinationPath,
        FileEntry destination, byte[]? digest) =>
        _committed[(sourcePath, destinationPath)] = new(source, destination, digest?.ToArray());

    internal void RecordDirectory(string source, string destination) => _createdDirectories.Add((source, destination));
    internal bool CreatedDirectory(string source, string destination) => _createdDirectories.Contains((source, destination));

    internal async Task<ResumeResult> VerifyAsync(IFileSystem source, string sourcePath,
        IFileSystem destination, string destinationPath, ConflictAction conflict, CancellationToken ct)
    {
        var key = (sourcePath, destinationPath);
        if (!_committed.TryGetValue(key, out var receipt)) return ResumeResult.NotRecorded;
        if (await MatchesAsync(source, sourcePath, receipt.Source, receipt.Digest, ct).ConfigureAwait(false) &&
            await MatchesAsync(destination, destinationPath, receipt.Destination, receipt.Digest, ct).ConfigureAwait(false))
            return ResumeResult.Verified;

        // The prior receipt is not a blanket overwrite authorization. Only an
        // explicit conflict decision may replace externally changed contents.
        if (conflict == ConflictAction.Overwrite)
        {
            _committed.Remove(key);
            return ResumeResult.NotRecorded;
        }
        if (conflict == ConflictAction.Skip) return ResumeResult.Skipped;
        if (conflict == ConflictAction.Cancel) throw new OperationCanceledException(ct);
        throw new TransferConflictException(destinationPath);
    }

    private static async Task<bool> MatchesAsync(IFileSystem fs, string path, FileEntry expected,
        byte[]? expectedDigest, CancellationToken ct)
    {
        var actual = await fs.StatAsync(path, ct).ConfigureAwait(false);
        if (!DestinationSnapshot.SameMetadata(expected, actual)) return false;
        if (expectedDigest is not null)
        {
            var digest = await DestinationSnapshot.ReadDigestAsync(fs, path, expected.Length, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(expectedDigest, digest)) return false;
        }
        return DestinationSnapshot.SameMetadata(expected, await fs.StatAsync(path, ct).ConfigureAwait(false));
    }

    internal enum ResumeResult { NotRecorded, Verified, Skipped }
    private sealed record Receipt(FileEntry Source, FileEntry Destination, byte[]? Digest);
    private sealed record Binding(string SourceNamespace, string Source, string DestinationNamespace, string Destination);
    private sealed class Lease(TransferResumeState owner) : IDisposable
    {
        public void Dispose() => Volatile.Write(ref owner._running, 0);
    }
}
