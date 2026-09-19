using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using MySsh.Core;

namespace MySsh.Infrastructure;

/// <summary>Per-job receipts for acknowledged commits; recovered receipts are always revalidated.</summary>
public sealed class TransferResumeState
{
    private readonly object _sync = new();
    private readonly Dictionary<(string Source, string Destination), ResumeReceipt> _committed = new();
    private readonly HashSet<ResumeDirectory> _createdDirectories = new();
    private ResumeBinding? _binding;
    private bool _sourceCleanupStarted;
    private int _running;
    internal Action? Checkpoint { get; set; }

    internal IDisposable Enter(IFileSystem source, string sourcePath, IFileSystem destination, string destinationPath)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("Resume state cannot be shared by simultaneous transfers.");
        try
        {
            var binding = new ResumeBinding(Namespace(source), sourcePath, Namespace(destination), destinationPath);
            lock (_sync)
            {
                if (_binding is not null && _binding != binding)
                    throw new InvalidOperationException("Resume state belongs to a different transfer. Start a new job for a different destination.");
                _binding = binding;
            }
            Checkpoint?.Invoke();
            return new Lease(this);
        }
        catch { Volatile.Write(ref _running, 0); throw; }
    }

    private static string Namespace(IFileSystem fs) => fs is IFileSystemNamespace namespaced
        ? namespaced.PathNamespace : !fs.IsRemote ? "local"
        : fs.GetType().FullName + ":" + RuntimeHelpers.GetHashCode(fs);

    internal void Record(string sourcePath, FileEntry source, string destinationPath,
        FileEntry destination, byte[]? digest)
    {
        lock (_sync) _committed[(sourcePath, destinationPath)] = new(sourcePath, destinationPath, source, destination, digest?.ToArray());
        Checkpoint?.Invoke();
    }

    internal void RecordDirectory(string source, string destination)
    {
        lock (_sync) _createdDirectories.Add(new(source, destination));
        Checkpoint?.Invoke();
    }
    internal bool CreatedDirectory(string source, string destination)
    {
        lock (_sync) return _createdDirectories.Contains(new(source, destination));
    }

    // Must be durably recorded BEFORE deleting the first source. A recovered
    // cleanup is not replayed: the user inspects remaining entries instead.
    internal void BeginSourceCleanup()
    {
        lock (_sync) _sourceCleanupStarted = true;
        Checkpoint?.Invoke();
    }

    internal ResumeData Capture()
    {
        lock (_sync) return new(_binding, _committed.Values.Select(x => x with { Digest = x.Digest?.ToArray() }).ToArray(),
            _createdDirectories.ToArray(), _sourceCleanupStarted);
    }

    internal static TransferResumeState Restore(ResumeData data)
    {
        if (data is null || data.Receipts is null || data.Directories is null ||
            (data.Binding is null && (data.Receipts.Length != 0 || data.Directories.Length != 0 || data.SourceCleanupStarted)))
            throw new IOException("Malformed transfer receipt state.");
        var state = new TransferResumeState { _binding = data.Binding, _sourceCleanupStarted = data.SourceCleanupStarted };
        foreach (var receipt in data.Receipts)
        {
            if (receipt is null || receipt.SourceEntry is null || receipt.DestinationEntry is null ||
                string.IsNullOrEmpty(receipt.Source) || string.IsNullOrEmpty(receipt.Destination) ||
                (receipt.SourceEntry.Kind == EntryKind.File && receipt.Digest?.Length != 32) ||
                !Enum.IsDefined(receipt.SourceEntry.Kind) || receipt.SourceEntry.Length < 0 ||
                !state._committed.TryAdd((receipt.Source, receipt.Destination), receipt))
                throw new IOException("Malformed or duplicate transfer receipt.");
        }
        foreach (var directory in data.Directories)
        {
            if (directory is null || string.IsNullOrEmpty(directory.Source) || string.IsNullOrEmpty(directory.Destination) ||
                !state._createdDirectories.Add(directory)) throw new IOException("Malformed directory receipt.");
        }
        return state;
    }

    internal async Task<ResumeResult> VerifyAsync(IFileSystem source, string sourcePath,
        IFileSystem destination, string destinationPath, ConflictAction conflict, CancellationToken ct)
    {
        var key = (sourcePath, destinationPath);
        ResumeReceipt? receipt;
        lock (_sync) _committed.TryGetValue(key, out receipt);
        if (receipt is null) return ResumeResult.NotRecorded;
        if (await MatchesAsync(source, sourcePath, receipt.SourceEntry, receipt.Digest, ct).ConfigureAwait(false) &&
            await MatchesAsync(destination, destinationPath, receipt.DestinationEntry, receipt.Digest, ct).ConfigureAwait(false))
            return ResumeResult.Verified;
        if (conflict == ConflictAction.Overwrite)
        {
            lock (_sync) _committed.Remove(key);
            Checkpoint?.Invoke();
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
    internal sealed record ResumeReceipt(string Source, string Destination, FileEntry SourceEntry, FileEntry DestinationEntry, byte[]? Digest);
    internal sealed record ResumeDirectory(string Source, string Destination);
    internal sealed record ResumeBinding(string SourceNamespace, string Source, string DestinationNamespace, string Destination);
    internal sealed record ResumeData(ResumeBinding? Binding, ResumeReceipt[] Receipts, ResumeDirectory[] Directories, bool SourceCleanupStarted);
    private sealed class Lease(TransferResumeState owner) : IDisposable
    {
        public void Dispose() => Volatile.Write(ref owner._running, 0);
    }
}
