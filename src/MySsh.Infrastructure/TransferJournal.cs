using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MySsh.Core;

namespace MySsh.Infrastructure;

/// <summary>Private per-job checkpoints. Each active owner holds a separate cross-process lease.</summary>
public sealed class TransferJournal : IDisposable
{
    private const int MaximumBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() }, MaxDepth = 32
    };
    private readonly Dictionary<Guid, FileStream> _leases = new();
    private readonly string _connection;
    internal string DirectoryPath { get; }
    public List<string> RecoveryWarnings { get; } = [];

    public TransferJournal(string root, string connectionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
        _connection = connectionKey;
        PrivateStorage.EnsureDirectory(root);
        DirectoryPath = Path.Combine(Path.GetFullPath(root), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connectionKey))));
        PrivateStorage.EnsureDirectory(DirectoryPath);
    }

    internal sealed record SavedJob(TransferJobSnapshot Snapshot, TransferOptions Options, TransferResumeState.ResumeData Resume);
    private sealed record Envelope(int Version, string Connection, string Payload, string Sha256);
    private string Filename(Guid id) => Path.Combine(DirectoryPath, id.ToString("N") + ".json");

    internal void Claim(Guid id)
    {
        if (id == Guid.Empty || _leases.ContainsKey(id)) throw new IOException("Duplicate transfer job identity.");
        var path = Path.Combine(DirectoryPath, id.ToString("N") + ".lock");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("A transfer lease must not be a link.");
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        _leases.Add(id, new FileStream(path, options));
    }

    internal IReadOnlyList<SavedJob> Recover()
    {
        var jobs = new List<SavedJob>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json").Order(StringComparer.Ordinal))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id))
            {
                RecoveryWarnings.Add("Unrecognized transfer checkpoint retained: " + Path.GetFileName(path));
                continue;
            }
            try { Claim(id); }
            catch (IOException)
            {
                RecoveryWarnings.Add("Transfer owned by another window, or its lease could not be opened: " + id);
                continue;
            }
            try
            {
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).Length > MaximumBytes)
                    throw new IOException("Unsafe or oversized transfer checkpoint.");
                var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path), Json)
                    ?? throw new IOException("Empty checkpoint.");
                if (envelope.Version != 1 || envelope.Connection != _connection || envelope.Payload is null ||
                    envelope.Sha256 != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Payload))))
                    throw new IOException("Checkpoint version, connection or checksum is invalid.");
                var job = JsonSerializer.Deserialize<SavedJob>(envelope.Payload, Json) ?? throw new IOException("Empty job.");
                Validate(job, id);
                _ = TransferResumeState.Restore(job.Resume);
                jobs.Add(job);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                RecoveryWarnings.Add("Checkpoint was not executed or overwritten: " + id + ". " + ex.Message);
                Release(id);
            }
        }
        return jobs;
    }

    internal void Save(SavedJob job)
    {
        var id = job.Snapshot.Id;
        if (!_leases.ContainsKey(id)) throw new IOException("This process does not own the transfer checkpoint.");
        Validate(job, id);
        var payload = JsonSerializer.Serialize(job, Json);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, _connection, payload,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))), Json);
        if (bytes.Length > MaximumBytes) throw new IOException("Transfer checkpoint exceeds its size limit; no old checkpoint was replaced.");
        var temporary = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = PrivateStorage.CreateFile(temporary)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, Filename(id), true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(SavedJob job, Guid id)
    {
        if (job.Snapshot is not { } snapshot || job.Options is null || job.Resume is null || snapshot.Id != id || id == Guid.Empty ||
            !Enum.IsDefined(snapshot.State) || !Enum.IsDefined(job.Options.Conflict) ||
            snapshot.Move != job.Options.Move || snapshot.BytesTransferred < 0 || snapshot.TotalBytes < 0 ||
            !Absolute(snapshot.Source, snapshot.SourceIsRemote) || !Absolute(snapshot.Destination, snapshot.DestinationIsRemote))
            throw new IOException("Malformed transfer job.");
        if (job.Resume.Binding is { } binding &&
            (binding.Source != snapshot.Source || binding.Destination != snapshot.Destination))
            throw new IOException("Receipt binding does not match this job.");
        if (job.Resume.Receipts is null || job.Resume.Directories is null) throw new IOException("Missing receipt arrays.");
        foreach (var item in job.Resume.Receipts)
            if (item is null || !Within(item.Source, snapshot.Source, snapshot.SourceIsRemote) ||
                !Within(item.Destination, snapshot.Destination, snapshot.DestinationIsRemote))
                throw new IOException("Receipt path escapes its transfer root.");
        foreach (var item in job.Resume.Directories)
            if (item is null || !Within(item.Source, snapshot.Source, snapshot.SourceIsRemote) ||
                !Within(item.Destination, snapshot.Destination, snapshot.DestinationIsRemote))
                throw new IOException("Directory receipt escapes its transfer root.");
    }

    private static bool Absolute(string? path, bool remote) => !string.IsNullOrWhiteSpace(path) && !path.Contains('\0') &&
        (remote ? path.StartsWith('/') && !path.Split('/').Any(part => part is "." or "..") : Path.IsPathFullyQualified(path));
    private static bool Within(string path, string root, bool remote)
    {
        if (!Absolute(path, remote)) return false;
        var comparison = !remote && OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var separator = remote ? '/' : Path.DirectorySeparatorChar;
        if (!remote) { path = Path.GetFullPath(path); root = Path.GetFullPath(root); }
        return path.Equals(root, comparison) || path.StartsWith(root.TrimEnd(separator) + separator, comparison);
    }

    internal void Remove(Guid id) { File.Delete(Filename(id)); Release(id); }
    internal void Release(Guid id)
    {
        if (_leases.Remove(id, out var lease)) lease.Dispose();
        // Do not delete the lock pathname: doing so races another process's open.
    }
    public void Dispose()
    {
        foreach (var lease in _leases.Values) lease.Dispose();
        _leases.Clear();
    }
}
