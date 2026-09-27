using System.Buffers.Binary;
using System.Threading.Channels;

// Protocol fixture with independently delayed responses. It never connects to a
// real server and can reorder replies, return short reads and reject writes.
internal sealed class PipelinePeer : IAsyncDisposable
{
    private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _tasks = [];
    private readonly object _sync = new();
    private readonly MemoryStream _file = new();
    private int _outstanding;
    private int _received;
    public int MaximumOutstanding { get; private set; }
    public int DataRequestCount => _received;
    public int DelayMilliseconds { get; init; }
    public int MaximumRead { get; init; } = int.MaxValue;
    public int FailWriteNumber { get; init; }
    public int FailSendNumber { get; init; }
    public bool HoldReplies { get; init; }
    public bool FailClose { get; init; }
    public int WriteCount { get; private set; }
    public int CloseCount { get; private set; }
    public TaskCompletionSource WindowReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Stream Input { get; }
    public Stream Output { get; }

    public PipelinePeer(byte[]? initial = null)
    {
        if (initial is not null) _file.Write(initial);
        Input = new Incoming(this);
        Output = new Outgoing(this);
    }

    public byte[] Contents { get { lock (_sync) return _file.ToArray(); } }

    private void Accept(byte[] packet)
    {
        var type = packet[0];
        if (type == 1) { _replies.Writer.TryWrite(ScriptedSftp.Version()); return; }
        var id = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(1));
        if (type == 3) { _replies.Writer.TryWrite(ScriptedSftp.Handle(id)); return; }
        if (type == 4)
        {
            CloseCount++;
            _replies.Writer.TryWrite(ScriptedSftp.Status(id, FailClose ? 4u : 0u, "close rejected"));
            return;
        }
        if (type is not (5 or 6)) throw new IOException("Unsupported fixture request.");
        var handleLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(5)));
        var offsetIndex = 9 + handleLength;
        var offset = checked((long)BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(offsetIndex)));
        var count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(offsetIndex + 8)));
        RegressionCases.Check(count <= 32 * 1024, "A data packet exceeded the interoperable 32 KiB limit.");
        if (++_received == FailSendNumber) throw new IOException("Injected send failure.");
        byte[] response;
        lock (_sync)
        {
            MaximumOutstanding = Math.Max(MaximumOutstanding, ++_outstanding);
            if (type == 6)
            {
                var failed = ++WriteCount == FailWriteNumber;
                if (!failed) { _file.Position = offset; _file.Write(packet, offsetIndex + 12, count); }
                response = ScriptedSftp.Status(id, failed ? 4u : 0u, failed ? "write rejected" : "");
            }
            else if (offset >= _file.Length) response = ScriptedSftp.Status(id, 1);
            else
            {
                _file.Position = offset;
                var data = new byte[(int)Math.Min(Math.Min(count, MaximumRead), _file.Length - offset)];
                _file.ReadExactly(data);
                response = ScriptedSftp.Packet(103, ScriptedSftp.U32(id), ScriptedSftp.Blob(data));
            }
        }
        if (_received >= MySsh.Infrastructure.SftpSession.PipelineRequests) WindowReceived.TrySetResult();
        _tasks.Add(ReplyAsync(response));
    }

    private async Task ReplyAsync(byte[] response)
    {
        try
        {
            if (HoldReplies) await Task.Delay(Timeout.Infinite, _stop.Token);
            else if (DelayMilliseconds > 0) await Task.Delay(DelayMilliseconds, _stop.Token);
            lock (_sync)
            {
                _outstanding--;
                _replies.Writer.TryWrite(response);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await Task.WhenAll(_tasks);
        Input.Dispose();
        Output.Dispose();
        _file.Dispose();
        _stop.Dispose();
    }

    private sealed class Incoming(PipelinePeer peer) : MemoryStream
    {
        private readonly MemoryStream _packet = new();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(!CanWrite, this);
            _packet.Write(buffer.Span);
            var bytes = _packet.ToArray();
            if (bytes.Length >= 4 && bytes.Length == 4 + BinaryPrimitives.ReadUInt32BigEndian(bytes))
            {
                _packet.SetLength(0);
                _packet.Position = 0;
                peer.Accept(bytes[4..]);
            }
            return ValueTask.CompletedTask;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _packet.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class Outgoing(PipelinePeer peer) : MemoryStream
    {
        private byte[] _packet = [];
        private int _offset;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(!CanRead, this);
            if (_offset == _packet.Length)
            {
                try { _packet = await peer._replies.Reader.ReadAsync(ct); }
                catch (ChannelClosedException ex) { throw new IOException("Fixture transport closed.", ex); }
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _packet.Length - _offset);
            _packet.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) peer._replies.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }
}
