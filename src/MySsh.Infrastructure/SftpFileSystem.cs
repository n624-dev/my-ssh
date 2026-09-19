using MySsh.Core;

namespace MySsh.Infrastructure;

public sealed class SftpFileSystem : IFileSystem, IFileSystemNamespace
{
    private readonly Connection _connection;
    private readonly IConnectionInteraction? _interaction;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private SftpSession _session;
    private int _disposed;

    internal SftpFileSystem(Connection connection, SftpSession session, IConnectionInteraction? interaction = null)
    {
        _connection = connection;
        _session = session;
        _interaction = interaction;
    }

    public bool IsRemote => true;
    // Reconnected and sibling sessions share a path namespace. User names are
    // case-sensitive; SSH aliases are not. Different aliases may still refer to
    // the same storage, but SFTP v3 cannot establish that cross-alias mapping.
    public string PathNamespace => $"sftp:{_connection.User}@{_connection.Host.ToLowerInvariant()}";
    public StringComparison PathComparison => StringComparison.Ordinal;

    public static async Task<SftpFileSystem> ConnectAsync(
        Connection connection,
        CancellationToken cancellationToken,
        IConnectionInteraction? interaction = null) =>
        new(connection, await ConnectSessionAsync(connection, cancellationToken, interaction).ConfigureAwait(false), interaction);

    private static Task<SftpSession> ConnectSessionAsync(Connection connection,
        CancellationToken cancellationToken, IConnectionInteraction? interaction) =>
        interaction is null
            ? SftpSession.ConnectAsync(connection, cancellationToken)
            : interaction.RunAsync(token => SftpSession.ConnectAsync(connection, token), cancellationToken);

    public Task<SftpFileSystem> CreateSiblingAsync(CancellationToken cancellationToken) =>
        ConnectAsync(_connection, cancellationToken, _interaction);

    public async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var replacement = await ConnectSessionAsync(_connection, cancellationToken, _interaction).ConfigureAwait(false);
            var old = _session;
            _session = replacement;
            await old.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public string Join(string directory, string name)
    {
        PathSafety.ValidateChildName(name, false);
        if (directory == "/") return "/" + name;
        return directory.TrimEnd('/') + "/" + name;
    }

    public string Parent(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    public Task<string> CanonicalAsync(string path, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _session.RealPathAsync(path, cancellationToken);
    }

    public async Task<FileEntry?> StatAsync(string path, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var session = _session;
        var attributes = await session.StatAsync(path, followLinks: false, cancellationToken).ConfigureAwait(false);
        if (attributes is null) return null;
        var kind = Kind(attributes.Value.Mode);
        string? target = null;
        if (kind == EntryKind.SymbolicLink)
        {
            try { target = await session.ReadLinkAsync(path, cancellationToken).ConfigureAwait(false); }
            catch (IOException) { }
        }
        return new FileEntry(
            path,
            Name(path),
            kind,
            checked((long)(attributes.Value.Size ?? 0)),
            attributes.Value.Modified ?? DateTimeOffset.UnixEpoch,
            attributes.Value.Mode,
            target);
    }

    public async Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var session = _session;
        var names = await session.ListAsync(path, cancellationToken).ConfigureAwait(false);
        var result = new List<FileEntry>(names.Count);
        foreach (var item in names)
        {
            if (item.Name is "." or "..") continue;
            var itemPath = Join(path, item.Name);
            var kind = Kind(item.Attributes.Mode);
            string? linkTarget = null;
            if (kind == EntryKind.SymbolicLink)
            {
                try { linkTarget = await session.ReadLinkAsync(itemPath, cancellationToken).ConfigureAwait(false); }
                catch (IOException) { }
            }
            result.Add(new FileEntry(
                itemPath,
                item.Name,
                kind,
                checked((long)(item.Attributes.Size ?? 0)),
                item.Attributes.Modified ?? DateTimeOffset.UnixEpoch,
                item.Attributes.Mode,
                linkTarget));
        }
        return result;
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var session = _session;
        if (await StatAsync(path, cancellationToken).ConfigureAwait(false) is not { Kind: EntryKind.File } entry)
            throw new IOException("Only regular files can be transferred.");
        var handle = await session.OpenAsync(path, SftpSession.OpenRead, cancellationToken).ConfigureAwait(false);
        return new SftpStream(session, handle, writable: false, entry.Length);
    }

    public async Task<Stream> OpenWriteAsync(string path, bool createNew, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var session = _session;
        var flags = SftpSession.OpenWrite;
        if (createNew) flags |= SftpSession.OpenCreate | SftpSession.OpenExclusive;
        var handle = await session.OpenAsync(path, flags, cancellationToken).ConfigureAwait(false);
        var length = createNew ? 0 : (await StatAsync(path, cancellationToken).ConfigureAwait(false))?.Length ?? 0;
        return new SftpStream(session, handle, writable: true, length);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _session.CreateDirectoryAsync(path, cancellationToken);
    }

    public Task RenameAsync(string source, string destination, bool replace, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        PathSafety.ProtectRoot(source, this);
        return _session.RenameAsync(source, destination, replace, cancellationToken);
    }

    public async Task DeleteAsync(string path, bool directory, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        PathSafety.ProtectRoot(path, this);
        var session = _session;
        var current = await StatAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException(path);
        if (current.Kind == EntryKind.SymbolicLink)
            await session.RemoveAsync(path, directory: false, cancellationToken).ConfigureAwait(false);
        else if (directory && current.Kind == EntryKind.Directory)
            await session.RemoveAsync(path, directory: true, cancellationToken).ConfigureAwait(false);
        else if (!directory && current.Kind == EntryKind.File)
            await session.RemoveAsync(path, directory: false, cancellationToken).ConfigureAwait(false);
        else
            throw new IOException("The entry type changed; delete was stopped.");
    }

    public Task SetMetadataAsync(
        string path,
        DateTimeOffset modified,
        uint? mode,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _session.SetMetadataAsync(path, modified, mode, cancellationToken);
    }

    public Task<string> ReadLinkAsync(string path, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _session.ReadLinkAsync(path, cancellationToken);
    }

    public Task CreateLinkAsync(string path, string target, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _session.CreateLinkAsync(path, target, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
            _lifecycle.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SftpFileSystem));
    }

    private static EntryKind Kind(uint? mode)
    {
        if (mode is null) return EntryKind.Other;
        return (mode.Value & 0xF000) switch
        {
            0x4000 => EntryKind.Directory,
            0x8000 => EntryKind.File,
            0xA000 => EntryKind.SymbolicLink,
            _ => EntryKind.Other
        };
    }

    private static string Name(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0) return "/";
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    private sealed class SftpStream : Stream
    {
        // Leave room for packet headers and handles within OpenSSH's 256 KiB limit.
        private const int DataChunkSize = 32 * 1024;
        private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
        private readonly SftpSession _session;
        private readonly byte[] _handle;
        private readonly bool _writable;
        private long _position;
        private long _length;
        private int _closed;

        public SftpStream(SftpSession session, byte[] handle, bool writable, long length)
        {
            _session = session;
            _handle = handle;
            _writable = writable;
            _length = length;
        }

        public override bool CanRead => !_writable && Volatile.Read(ref _closed) == 0;
        public override bool CanSeek => Volatile.Read(ref _closed) == 0;
        public override bool CanWrite => _writable && Volatile.Read(ref _closed) == 0;
        public override long Length => _length;
        public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            if (!CanRead) throw new NotSupportedException();
            if (buffer.Length == 0) return 0;
            var data = await _session.ReadAsync(
                _handle,
                checked((ulong)_position),
                Math.Min(buffer.Length, DataChunkSize),
                cancellationToken).ConfigureAwait(false);
            data.AsSpan().CopyTo(buffer.Span);
            _position += data.Length;
            return data.Length;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            if (!CanWrite) throw new NotSupportedException();
            var offset = 0;
            while (offset < buffer.Length)
            {
                var count = Math.Min(DataChunkSize, buffer.Length - offset);
                await _session.WriteAsync(
                    _handle,
                    checked((ulong)_position),
                    buffer.Slice(offset, count),
                    cancellationToken).ConfigureAwait(false);
                offset += count;
                _position += count;
                _length = Math.Max(_length, _position);
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            var next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (next < 0) throw new IOException("Cannot seek before the start of a file.");
            _position = next;
            return next;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
                    CloseHandleAsync().GetAwaiter().GetResult();
            }
            finally { base.Dispose(disposing); }
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                if (Interlocked.Exchange(ref _closed, 1) == 0)
                    await CloseHandleAsync().ConfigureAwait(false);
            }
            finally { GC.SuppressFinalize(this); }
        }

        private async Task CloseHandleAsync()
        {
            using var timeout = new CancellationTokenSource(CloseTimeout);
            try
            {
                await _session.CloseAsync(_handle, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // A write can fail when the server flushes buffered data on CLOSE.
                // Normal write completion must propagate this failure before rename
                // or Move source deletion. Read-only handles remain cleanup-only.
                if (_writable)
                    throw new IOException(
                        "SFTP write CLOSE was not acknowledged successfully; completion is unconfirmed. " +
                        "The destination must not be committed and the source must be retained.", ex);
            }
        }
    }
}
