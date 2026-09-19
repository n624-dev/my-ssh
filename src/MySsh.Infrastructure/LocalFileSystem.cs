using MySsh.Core;

namespace MySsh.Infrastructure;

public sealed class LocalFileSystem : IFileSystem, IDisposable
{
    public bool IsRemote => false;
    public StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public string Join(string directory, string name)
    {
        PathSafety.ValidateChildName(name, OperatingSystem.IsWindows());
        return Path.Combine(directory, name);
    }

    public string Parent(string path)
    {
        var full = Path.GetFullPath(path);
        var trimmed = Path.TrimEndingDirectorySeparator(full);
        return Path.GetDirectoryName(trimmed) ?? Path.GetPathRoot(full)!;
    }

    public Task<string> CanonicalAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
    }

    public Task<FileEntry?> StatAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var attributes = File.GetAttributes(path);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var isLink = attributes.HasFlag(FileAttributes.ReparsePoint);
            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            var kind = isLink ? EntryKind.SymbolicLink : isDirectory ? EntryKind.Directory : EntryKind.File;
            var length = kind == EntryKind.File ? ((FileInfo)info).Length : 0;
            uint? mode = !OperatingSystem.IsWindows() && !isLink ? (uint)File.GetUnixFileMode(path) : null;
            return Task.FromResult<FileEntry?>(new(
                path,
                info.Name,
                kind,
                length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                mode,
                isLink ? info.LinkTarget : null));
        }
        catch (FileNotFoundException) { return Task.FromResult<FileEntry?>(null); }
        catch (DirectoryNotFoundException) { return Task.FromResult<FileEntry?>(null); }
    }

    public async Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken)
    {
        var result = new List<FileEntry>();
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await StatAsync(child, cancellationToken).ConfigureAwait(false) is { } entry) result.Add(entry);
        }
        return result;
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        if (await StatAsync(path, cancellationToken).ConfigureAwait(false) is not { Kind: EntryKind.File })
            throw new IOException("Only regular files can be transferred.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public async Task<Stream> OpenWriteAsync(string path, bool createNew, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (createNew) return PrivateStorage.CreateFile(path, FileOptions.Asynchronous);
        if (await StatAsync(path, cancellationToken).ConfigureAwait(false) is not { Kind: EntryKind.File })
            throw new IOException("The partial file is missing or is not a regular file.");
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open, Access = FileAccess.ReadWrite, Share = FileShare.None,
            BufferSize = 64 * 1024, Options = FileOptions.Asynchronous
        });
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    public async Task RenameAsync(string source, string destination, bool replace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PathSafety.ProtectRoot(source, this);
        var entry = await StatAsync(source, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException(source);
        if (entry.Kind == EntryKind.Directory)
        {
            if (replace) throw new IOException("Replacing a directory is not supported. Merge it or choose a new name.");
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination, replace);
        }
    }

    public async Task DeleteAsync(string path, bool directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PathSafety.ProtectRoot(path, this);
        var entry = await StatAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException(path);
        if (entry.Kind == EntryKind.SymbolicLink)
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.Directory)) Directory.Delete(path, false);
            else File.Delete(path);
            return;
        }
        if (directory && entry.Kind == EntryKind.Directory) Directory.Delete(path, false);
        else if (!directory && entry.Kind == EntryKind.File) File.Delete(path);
        else throw new IOException("The entry type changed; delete was stopped.");
    }

    public async Task SetMetadataAsync(string path, DateTimeOffset modified, uint? mode, CancellationToken cancellationToken)
    {
        var entry = await StatAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException(path);
        if (entry.Kind == EntryKind.SymbolicLink)
            throw new IOException("Changing link metadata is not supported.");
        if (entry.Kind == EntryKind.Directory) Directory.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        else File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        if (!OperatingSystem.IsWindows() && mode is { } unixMode)
            File.SetUnixFileMode(path, (UnixFileMode)(unixMode & 0x1FF));
    }

    public Task<string> ReadLinkAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(path);
        FileSystemInfo info = attributes.HasFlag(FileAttributes.Directory)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        return Task.FromResult(info.LinkTarget ?? throw new IOException("Not a symbolic link."));
    }

    public Task CreateLinkAsync(string path, string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
            throw new IOException("Creating imported symbolic links on Windows is disabled because their target type and required privilege cannot be inferred safely.");
        File.CreateSymbolicLink(path, target);
        return Task.CompletedTask;
    }

    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
