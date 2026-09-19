using System.Buffers.Binary;

namespace MySsh.Infrastructure;

internal sealed partial class SftpSession
{
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private uint _requestId;
    private int _disposed;
    private int _transportClosed;
    private Exception? _fault;

    internal bool IsFaulted => Volatile.Read(ref _fault) is not null;

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _fault) is { } fault)
            throw new IOException("This SFTP session is unusable after an interrupted request; reconnect first.", fault);
    }

    private async Task<Packet> RequestAsync(byte type, Action<MemoryStream> write,
        CancellationToken cancellationToken, bool allowStatus = false)
    {
        ThrowIfUnavailable();
        // Cancellation while queued cannot have changed the wire state.
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            var id = unchecked(_requestId + 1);
            using var payload = new MemoryStream();
            WriteUInt32(payload, id);
            write(payload);
            var bytes = payload.ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            _requestId = id;

            Packet response;
            SftpStatus? status = null;
            try
            {
                // Once this call starts we conservatively assume even a partial
                // send might have reached the server. Never reuse such a stream.
                await SendPacketAsync(type, bytes, cancellationToken).ConfigureAwait(false);
                var packet = await ReadPacketAsync(cancellationToken).ConfigureAwait(false);
                var reader = new PacketReader(packet.Payload);
                var responseId = reader.ReadUInt32();
                if (responseId != id)
                    throw new IOException($"SFTP response id mismatch: expected {id}, received {responseId}.");
                if (packet.Type is < FxpStatus or > FxpAttrs && packet.Type != 201)
                    throw new IOException($"Unexpected SFTP response type {packet.Type}.");
                response = new Packet(packet.Type, reader.ReadRemaining());
                if (response.Type == FxpStatus) status = ParseStatus(response.Payload);
            }
            catch (OperationCanceledException ex)
            {
                FaultTransport(ex);
                throw new SftpRequestCanceledException(id, type, MayChangeServerState(type), ex, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OverflowException or ArgumentException)
            {
                FaultTransport(ex);
                throw new SftpRequestInterruptedException(id, type, MayChangeServerState(type), ex);
            }

            // A valid error reply (e.g. permission denied) is not a transport
            // failure. It consumed exactly one reply and the session is reusable.
            if (!allowStatus && status is { Code: not 0 } error) ThrowStatus(error);
            return response;
        }
        finally { _requestLock.Release(); }
    }

    private static bool MayChangeServerState(byte type) => type is
        FxpOpen or FxpClose or FxpWrite or FxpSetstat or FxpRemove or FxpMkdir or
        FxpRmdir or FxpRename or FxpSymlink or FxpExtended;

    private void FaultTransport(Exception error)
    {
        Interlocked.CompareExchange(ref _fault, error, null);
        CloseTransport();
    }

    private void CloseTransport()
    {
        if (Interlocked.Exchange(ref _transportClosed, 1) != 0) return;
        try { _input.Close(); } catch { }
        try { _output.Close(); } catch { }
        try { if (_process is not null && !_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch { /* Disposal below will still wait for process exit with a deadline. */ }
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CloseTransport();
        if (_process is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch { /* Pipes are closed and process termination was requested. */ }
            _process.Dispose();
        }
        // Request continuations/waiters may still release this semaphore. It has
        // no WaitHandle: leave the managed-only object for GC instead of racing
        // Dispose with an in-flight request's finally block.
    }
}
