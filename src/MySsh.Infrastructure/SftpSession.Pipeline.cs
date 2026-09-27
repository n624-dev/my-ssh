namespace MySsh.Infrastructure;

internal sealed partial class SftpSession
{
    internal const int DataChunkSize = 32 * 1024;
    internal const int PipelineRequests = 32;
    internal const int PipelineBytes = DataChunkSize * PipelineRequests;

    // Only data operations use batches. Holding the existing request lock keeps
    // CLOSE, metadata and commit operations behind every response in the batch.
    private async Task<Packet[]> DataBatchAsync(byte type, Action<MemoryStream>[] requests, CancellationToken ct)
    {
        if (requests.Length is < 1 or > PipelineRequests) throw new ArgumentOutOfRangeException(nameof(requests));
        ThrowIfUnavailable();
        await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            ct.ThrowIfCancellationRequested();
            var ids = new Dictionary<uint, int>();
            var payloads = new byte[requests.Length][];
            var firstId = unchecked(_requestId + 1);
            for (var i = 0; i < requests.Length; i++)
            {
                var id = unchecked(firstId + (uint)i);
                ids.Add(id, i);
                using var payload = new MemoryStream();
                WriteUInt32(payload, id);
                requests[i](payload);
                payloads[i] = payload.ToArray();
            }
            ct.ThrowIfCancellationRequested();
            _requestId = unchecked(firstId + (uint)requests.Length - 1);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Read while sending, so small SSH pipe buffers cannot deadlock when
            // the server replies before it has consumed all outstanding writes.
            var sending = SendAsync();
            var receiving = ReceiveAsync();
            try { await Task.WhenAll(sending, receiving).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or ArgumentException or OverflowException)
            {
                FaultTransport(ex);
                if (ct.IsCancellationRequested)
                    throw new SftpRequestCanceledException(firstId, type, type == FxpWrite, ex, ct);
                throw new SftpRequestInterruptedException(firstId, type, type == FxpWrite, ex);
            }
            return await receiving.ConfigureAwait(false);

            async Task SendAsync()
            {
                try
                {
                    foreach (var payload in payloads)
                        await SendPacketAsync(type, payload, stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) { FaultTransport(ex); stop.Cancel(); throw; }
            }

            async Task<Packet[]> ReceiveAsync()
            {
                try
                {
                    var responses = new Packet[requests.Length];
                    for (var i = 0; i < responses.Length; i++)
                    {
                        var packet = await ReadPacketAsync(stop.Token).ConfigureAwait(false);
                        var reader = new PacketReader(packet.Payload);
                        var id = reader.ReadUInt32();
                        if (!ids.Remove(id, out var index)) throw new IOException("Unknown or duplicate SFTP batch response ID.");
                        if (packet.Type != FxpStatus && !(type == FxpRead && packet.Type == FxpData))
                            throw Unexpected(packet, type == FxpRead ? "DATA or STATUS" : "STATUS");
                        var response = new Packet(packet.Type, reader.ReadRemaining());
                        if (response.Type == FxpStatus) _ = ParseStatus(response.Payload);
                        else
                        {
                            var data = new PacketReader(response.Payload);
                            var bytes = data.ReadBlob();
                            if (bytes.Length is < 1 or > DataChunkSize || !data.End)
                                throw new IOException("Invalid SFTP DATA payload in a pipelined read.");
                        }
                        responses[index] = response;
                    }
                    return responses;
                }
                catch (Exception ex) { FaultTransport(ex); stop.Cancel(); throw; }
            }
        }
        finally { _requestLock.Release(); }
    }

    internal async Task WritePipelinedAsync(byte[] handle, ulong offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        for (var start = 0; start < data.Length;)
        {
            var length = Math.Min(PipelineBytes, data.Length - start);
            var requests = new List<Action<MemoryStream>>();
            for (var part = 0; part < length; part += DataChunkSize)
            {
                var position = checked(offset + (ulong)(start + part));
                var chunk = data.Slice(start + part, Math.Min(DataChunkSize, length - part));
                requests.Add(writer => { WriteBlob(writer, handle); WriteUInt64(writer, position); WriteBlob(writer, chunk.Span); });
            }
            var responses = await DataBatchAsync(FxpWrite, requests.ToArray(), ct).ConfigureAwait(false);
            // Drain all replies before surfacing a valid server error. No failed
            // WRITE can turn into a successful flush, close, rename or Move.
            foreach (var response in responses) ExpectOk(response);
            start += length;
        }
    }

    internal async Task<int> ReadPipelinedAsync(byte[] handle, ulong offset, Memory<byte> buffer, CancellationToken ct)
    {
        var length = Math.Min(buffer.Length, PipelineBytes);
        if (length == 0) return 0;
        var parts = (length + DataChunkSize - 1) / DataChunkSize;
        var filled = new int[parts];
        var end = length;
        while (true)
        {
            var indexes = new List<int>();
            var sizes = new List<int>();
            var requests = new List<Action<MemoryStream>>();
            for (var i = 0; i < parts; i++)
            {
                var start = i * DataChunkSize + filled[i];
                var wanted = Math.Min(DataChunkSize - filled[i], end - start);
                if (wanted <= 0) continue;
                indexes.Add(i);
                sizes.Add(wanted);
                var position = checked(offset + (ulong)start);
                requests.Add(writer => { WriteBlob(writer, handle); WriteUInt64(writer, position); WriteUInt32(writer, (uint)wanted); });
            }
            if (requests.Count == 0) return end;
            var responses = await DataBatchAsync(FxpRead, requests.ToArray(), ct).ConfigureAwait(false);
            for (var i = 0; i < responses.Length; i++)
            {
                var response = responses[i];
                var part = indexes[i];
                var start = part * DataChunkSize + filled[part];
                if (response.Type == FxpStatus)
                {
                    var status = ParseStatus(response.Payload);
                    if (status.Code != 1) ThrowStatus(status);
                    end = Math.Min(end, start);
                    continue;
                }
                var reader = new PacketReader(response.Payload);
                var bytes = reader.ReadBlob();
                if (bytes.Length == 0 || bytes.Length > sizes[i] || !reader.End)
                {
                    var error = new IOException("Invalid SFTP DATA length in a pipelined read.");
                    FaultTransport(error);
                    throw error;
                }
                bytes.CopyTo(buffer.Slice(start, bytes.Length));
                filled[part] += bytes.Length;
            }
            // A short DATA response is not EOF. Request the missing tails before
            // returning contiguous data; later replies must never hide a gap.
        }
    }
}
