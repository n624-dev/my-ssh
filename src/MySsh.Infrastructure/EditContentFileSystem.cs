using MySsh.Core;

namespace MySsh.Infrastructure;

/// <summary>Reads draft bytes while supplying the original destination's permission bits.</summary>
/// <remarks>The recovery file itself remains private; its on-disk mode is never widened.</remarks>
internal sealed class EditContentFileSystem(uint? originalMode) : IFileSystem
{
    private readonly LocalFileSystem _local = new();
    public bool IsRemote => false;
    public StringComparison PathComparison => _local.PathComparison;
    public string Join(string directory, string name) => _local.Join(directory, name);
    public string Parent(string path) => _local.Parent(path);
    public Task<string> CanonicalAsync(string path, CancellationToken ct) => _local.CanonicalAsync(path, ct);
    public async Task<FileEntry?> StatAsync(string path, CancellationToken ct)
    {
        var entry = await _local.StatAsync(path, ct).ConfigureAwait(false);
        return entry is null ? null : entry with { Mode = originalMode ?? entry.Mode };
    }
    public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => _local.OpenReadAsync(path, ct);
    public Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct) => throw new NotSupportedException();
    public Task<Stream> OpenWriteAsync(string path, bool createNew, CancellationToken ct) => throw new NotSupportedException();
    public Task CreateDirectoryAsync(string path, CancellationToken ct) => throw new NotSupportedException();
    public Task RenameAsync(string source, string destination, bool replace, CancellationToken ct) => throw new NotSupportedException();
    public Task DeleteAsync(string path, bool directory, CancellationToken ct) => throw new NotSupportedException();
    public Task SetMetadataAsync(string path, DateTimeOffset modified, uint? mode, CancellationToken ct) => throw new NotSupportedException();
    public Task<string> ReadLinkAsync(string path, CancellationToken ct) => throw new NotSupportedException();
    public Task CreateLinkAsync(string path, string target, CancellationToken ct) => throw new NotSupportedException();
    public ValueTask DisposeAsync() => _local.DisposeAsync();
}
