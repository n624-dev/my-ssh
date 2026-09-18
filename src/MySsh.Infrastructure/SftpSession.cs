using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using MySsh.Core;

namespace MySsh.Infrastructure;

internal sealed class SftpSession : IAsyncDisposable
{
    private const byte FxpInit = 1;
    private const byte FxpVersion = 2;
    private const byte FxpOpen = 3;
    private const byte FxpClose = 4;
    private const byte FxpRead = 5;
    private const byte FxpWrite = 6;
    private const byte FxpLstat = 7;
    private const byte FxpSetstat = 9;
    private const byte FxpOpendir = 11;
    private const byte FxpReaddir = 12;
    private const byte FxpRemove = 13;
    private const byte FxpMkdir = 14;
    private const byte FxpRmdir = 15;
    private const byte FxpRealpath = 16;
    private const byte FxpStat = 17;
    private const byte FxpRename = 18;
    private const byte FxpReadlink = 19;
    private const byte FxpSymlink = 20;
    private const byte FxpStatus = 101;
    private const byte FxpHandle = 102;
    private const byte FxpData = 103;
    private const byte FxpName = 104;
    private const byte FxpAttrs = 105;
    private const byte FxpExtended = 200;

    internal const uint OpenRead = 1;
    internal const uint OpenWrite = 2;
    internal const uint OpenCreate = 8;
    internal const uint OpenTruncate = 16;
    internal const uint OpenExclusive = 32;

    private const uint AttrSize = 0x00000001;
    private const uint AttrUidGid = 0x00000002;
    private const uint AttrPermissions = 0x00000004;
    private const uint AttrTimes = 0x00000008;
    private const uint AttrExtended = 0x80000000;

    private readonly Process? _process;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly Dictionary<string, string> _extensions = new(StringComparer.Ordinal);
    private uint _requestId;
    private bool _disposed;

    private SftpSession(Process process)
        : this(process.StandardInput.BaseStream, process.StandardOutput.BaseStream)
    {
        _process = process;
    }

    private SftpSession(Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!input.CanWrite || !output.CanRead)
            throw new ArgumentException("SFTP transport requires writable input and readable output.");
        _input = input;
        _output = output;
    }

    public static async Task<SftpSession> ConnectAsync(Connection connection, CancellationToken cancellationToken)
    {
        var session = new SftpSession(OpenSsh.StartSftpSubsystem(connection));
        try
        {
            await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // The session owns these streams. This transport seam exercises the real
    // packet parser and stream lifecycle without a process or network connection.
    internal static async Task<SftpSession> ConnectAsync(
        Stream input, Stream output, CancellationToken cancellationToken)
    {
        var session = new SftpSession(input, output);
        try
        {
            await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var payload = new MemoryStream();
        WriteUInt32(payload, 3);
        await SendPacketAsync(FxpInit, payload.ToArray(), cancellationToken).ConfigureAwait(false);
        var packet = await ReadPacketAsync(cancellationToken).ConfigureAwait(false);
        if (packet.Type != FxpVersion)
            throw new IOException($"SFTP server returned packet type {packet.Type} instead of VERSION.");

        var reader = new PacketReader(packet.Payload);
        var version = reader.ReadUInt32();
        if (version != 3)
            throw new IOException($"Unsupported SFTP protocol version {version}; version 3 is required.");
        while (!reader.End)
        {
            var name = reader.ReadString();
            var value = reader.ReadString();
            _extensions[name] = value;
        }
    }

    public async Task<string> RealPathAsync(string path, CancellationToken cancellationToken)
    {
        var packet = await RequestAsync(FxpRealpath, writer => WriteString(writer, path), cancellationToken).ConfigureAwait(false);
        var names = ParseNames(packet);
        return names.Count == 0 ? throw new IOException("SFTP REALPATH returned no path.") : names[0].Name;
    }

    public async Task<SftpAttributes?> StatAsync(string path, bool followLinks, CancellationToken cancellationToken)
    {
        var packet = await RequestAsync(followLinks ? FxpStat : FxpLstat,
            writer => WriteString(writer, path), cancellationToken, allowStatus: true).ConfigureAwait(false);
        if (packet.Type == FxpStatus)
        {
            var status = ParseStatus(packet.Payload);
            if (status.Code == 2) return null;
            ThrowStatus(status);
        }
        if (packet.Type != FxpAttrs) throw Unexpected(packet, "ATTRS");
        var reader = new PacketReader(packet.Payload);
        return ReadAttributes(ref reader);
    }

    public async Task<IReadOnlyList<SftpName>> ListAsync(string path, CancellationToken cancellationToken)
    {
        var open = await RequestAsync(FxpOpendir, writer => WriteString(writer, path), cancellationToken).ConfigureAwait(false);
        var handle = ParseHandle(open);
        var result = new List<SftpName>();
        try
        {
            while (true)
            {
                var packet = await RequestAsync(FxpReaddir, writer => WriteBlob(writer, handle), cancellationToken, allowStatus: true).ConfigureAwait(false);
                if (packet.Type == FxpStatus)
                {
                    var status = ParseStatus(packet.Payload);
                    if (status.Code == 1) break;
                    ThrowStatus(status);
                }
                result.AddRange(ParseNames(packet));
            }
        }
        finally
        {
            await CloseAsync(handle, CancellationToken.None).ConfigureAwait(false);
        }
        return result;
    }

    public async Task<byte[]> OpenAsync(string path, uint flags, CancellationToken cancellationToken)
    {
        var packet = await RequestAsync(FxpOpen, writer =>
        {
            WriteString(writer, path);
            WriteUInt32(writer, flags);
            WriteUInt32(writer, 0);
        }, cancellationToken).ConfigureAwait(false);
        return ParseHandle(packet);
    }

    public async Task CloseAsync(byte[] handle, CancellationToken cancellationToken)
    {
        var packet = await RequestAsync(FxpClose, writer => WriteBlob(writer, handle), cancellationToken).ConfigureAwait(false);
        ExpectOk(packet);
    }

    public async Task<byte[]> ReadAsync(byte[] handle, ulong offset, int count, CancellationToken cancellationToken)
    {
        var packet = await RequestAsync(FxpRead, writer =>
        {
            WriteBlob(writer, handle);
            WriteUInt64(writer, offset);
            WriteUInt32(writer, checked((uint)count));
        }, cancellationToken, allowStatus: true).ConfigureAwait(false);
        if (packet.Type == FxpStatus)
        {
            var status = ParseStatus(packet.Payload);
            if (status.Code == 1) return [];
            ThrowStatus(status);
        }
        if (packet.Type != FxpData) throw Unexpected(packet, "DATA");
        var reader = new PacketReader(packet.Payload);
        return reader.ReadBlob();
    }

    public async Task WriteAsync(byte[] handle, ulong offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var packet = await RequestAsync(FxpWrite, writer =>
        {
            WriteBlob(writer, handle);
            WriteUInt64(writer, offset);
            WriteBlob(writer, data.Span);
        }, cancellationToken).ConfigureAwait(false);
        ExpectOk(packet);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var packet = await RequestAsync(FxpMkdir, writer =>
        {
            WriteString(writer, path);
            WriteUInt32(writer, 0);
        }, cancellationToken).ConfigureAwait(false);
        ExpectOk(packet);
    }

    public async Task RemoveAsync(string path, bool directory, CancellationToken cancellationToken)
    {
        var packet = await RequestAsync(directory ? FxpRmdir : FxpRemove,
            writer => WriteString(writer, path), cancellationToken).ConfigureAwait(false);
        ExpectOk(packet);
    }

    public async Task RenameAsync(string source, string destination, bool replace, CancellationToken cancellationToken)
    {
        Packet packet;
        if (replace)
        {
            if (!_extensions.ContainsKey("posix-rename@openssh.com"))
                throw new IOException("The server does not support atomic replacement (posix-rename@openssh.com).");
            packet = await RequestAsync(FxpExtended, writer =>
            {
                WriteString(writer, "posix-rename@openssh.com");
                WriteString(writer, source);
                WriteString(writer, destination);
            }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            packet = await RequestAsync(FxpRename, writer =>
            {
                WriteString(writer, source);
                WriteString(writer, destination);
            }, cancellationToken).ConfigureAwait(false);
        }
        ExpectOk(packet);
    }

    public async Task SetMetadataAsync(string path, DateTimeOffset modified, uint? mode, CancellationToken cancellationToken)
    {
        var packet = await RequestAsync(FxpSetstat, writer =>
        {
            WriteString(writer, path);
            var flags = AttrTimes | (mode.HasValue ? AttrPermissions : 0u);
            WriteUInt32(writer, flags);
            if (mode.HasValue) WriteUInt32(writer, mode.Value & 0x1FF);
            var seconds = checked((uint)Math.Clamp(modified.ToUnixTimeSeconds(), 0, uint.MaxValue));
            WriteUInt32(writer, seconds);
            WriteUInt32(writer, seconds);
        }, cancellationToken).ConfigureAwait(false);
        ExpectOk(packet);
    }

    public async Task<string> ReadLinkAsync(string path, CancellationToken cancellationToken)
    {
        var packet = await RequestAsync(FxpReadlink, writer => WriteString(writer, path), cancellationToken).ConfigureAwait(false);
        var names = ParseNames(packet);
        return names.Count == 0 ? throw new IOException("READLINK returned no target.") : names[0].Name;
    }

    public async Task CreateLinkAsync(string path, string target, CancellationToken cancellationToken)
    {
        var packet = await RequestAsync(FxpSymlink, writer =>
        {
            // OpenSSH deliberately uses targetpath, linkpath for SFTP v3.
            WriteString(writer, target);
            WriteString(writer, path);
        }, cancellationToken).ConfigureAwait(false);
        ExpectOk(packet);
    }

    private async Task<Packet> RequestAsync(byte type, Action<MemoryStream> write,
        CancellationToken cancellationToken, bool allowStatus = false)
    {
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = unchecked(++_requestId);
            using var payload = new MemoryStream();
            WriteUInt32(payload, id);
            write(payload);
            await SendPacketAsync(type, payload.ToArray(), cancellationToken).ConfigureAwait(false);
            var packet = await ReadPacketAsync(cancellationToken).ConfigureAwait(false);
            var reader = new PacketReader(packet.Payload);
            var responseId = reader.ReadUInt32();
            if (responseId != id)
                throw new IOException($"SFTP response id mismatch: expected {id}, received {responseId}.");
            var body = reader.ReadRemaining();
            var response = new Packet(packet.Type, body);
            if (response.Type == FxpStatus && !allowStatus)
            {
                var status = ParseStatus(response.Payload);
                if (status.Code != 0) ThrowStatus(status);
            }
            return response;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task SendPacketAsync(byte type, byte[] payload, CancellationToken cancellationToken)
    {
        var length = checked(1 + payload.Length);
        var header = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), checked((uint)length));
        header[4] = type;
        await _input.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (payload.Length > 0) await _input.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Packet> ReadPacketAsync(CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        await ReadExactlyAsync(_output, lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
        if (length is < 1 or > 64 * 1024 * 1024)
            throw new IOException($"Invalid SFTP packet length {length}.");
        var packet = new byte[checked((int)length)];
        await ReadExactlyAsync(_output, packet, cancellationToken).ConfigureAwait(false);
        return new Packet(packet[0], packet[1..]);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("SSH SFTP subsystem closed unexpectedly.");
            offset += count;
        }
    }

    private static byte[] ParseHandle(Packet packet)
    {
        if (packet.Type != FxpHandle) throw Unexpected(packet, "HANDLE");
        var reader = new PacketReader(packet.Payload);
        return reader.ReadBlob();
    }

    private static List<SftpName> ParseNames(Packet packet)
    {
        if (packet.Type != FxpName) throw Unexpected(packet, "NAME");
        var reader = new PacketReader(packet.Payload);
        var count = reader.ReadUInt32();
        if (count > 1_000_000) throw new IOException("SFTP directory entry count is unreasonable.");
        var result = new List<SftpName>(checked((int)count));
        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadString();
            _ = reader.ReadString();
            result.Add(new SftpName(name, ReadAttributes(ref reader)));
        }
        return result;
    }

    private static SftpAttributes ReadAttributes(ref PacketReader reader)
    {
        var flags = reader.ReadUInt32();
        ulong? size = null;
        uint? mode = null;
        DateTimeOffset? modified = null;
        if ((flags & AttrSize) != 0) size = reader.ReadUInt64();
        if ((flags & AttrUidGid) != 0) { _ = reader.ReadUInt32(); _ = reader.ReadUInt32(); }
        if ((flags & AttrPermissions) != 0) mode = reader.ReadUInt32();
        if ((flags & AttrTimes) != 0)
        {
            _ = reader.ReadUInt32();
            modified = DateTimeOffset.FromUnixTimeSeconds(reader.ReadUInt32());
        }
        if ((flags & AttrExtended) != 0)
        {
            var count = reader.ReadUInt32();
            for (var i = 0; i < count; i++) { _ = reader.ReadString(); _ = reader.ReadString(); }
        }
        return new SftpAttributes(size, mode, modified);
    }

    private static SftpStatus ParseStatus(byte[] payload)
    {
        var reader = new PacketReader(payload);
        var code = reader.ReadUInt32();
        var message = reader.End ? "" : reader.ReadString();
        if (!reader.End) _ = reader.ReadString();
        return new SftpStatus(code, message);
    }

    private static void ExpectOk(Packet packet)
    {
        if (packet.Type != FxpStatus) throw Unexpected(packet, "STATUS");
        var status = ParseStatus(packet.Payload);
        if (status.Code != 0) ThrowStatus(status);
    }

    private static void ThrowStatus(SftpStatus status) =>
        throw new IOException($"SFTP error {status.Code}: {status.Message}");

    private static IOException Unexpected(Packet packet, string expected) =>
        new($"Unexpected SFTP packet type {packet.Type}; expected {expected}.");

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteString(Stream stream, string value) => WriteBlob(stream, Encoding.UTF8.GetBytes(value));

    private static void WriteBlob(Stream stream, ReadOnlySpan<byte> value)
    {
        WriteUInt32(stream, checked((uint)value.Length));
        stream.Write(value);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { _input.Close(); } catch { }
        if (_process is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            }
            _process.Dispose();
        }
        try { _output.Close(); } catch { }
        _requestLock.Dispose();
    }

    private readonly record struct Packet(byte Type, byte[] Payload);
    internal readonly record struct SftpName(string Name, SftpAttributes Attributes);
    internal readonly record struct SftpAttributes(ulong? Size, uint? Mode, DateTimeOffset? Modified);
    private readonly record struct SftpStatus(uint Code, string Message);

    private ref struct PacketReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;

        public PacketReader(ReadOnlySpan<byte> data) { _data = data; _offset = 0; }
        public bool End => _offset == _data.Length;
        public uint ReadUInt32()
        {
            Ensure(4);
            var value = BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(_offset, 4));
            _offset += 4;
            return value;
        }
        public ulong ReadUInt64()
        {
            Ensure(8);
            var value = BinaryPrimitives.ReadUInt64BigEndian(_data.Slice(_offset, 8));
            _offset += 8;
            return value;
        }
        public byte[] ReadBlob()
        {
            var length = checked((int)ReadUInt32());
            Ensure(length);
            var value = _data.Slice(_offset, length).ToArray();
            _offset += length;
            return value;
        }
        public string ReadString() => new UTF8Encoding(false, true).GetString(ReadBlob());
        public byte[] ReadRemaining()
        {
            var value = _data[_offset..].ToArray();
            _offset = _data.Length;
            return value;
        }
        private void Ensure(int count)
        {
            if (count < 0 || _offset > _data.Length - count)
                throw new IOException("Malformed SFTP packet.");
        }
    }
}
