using MySsh.App;

internal sealed class Issue29Tests : IRegressionCase
{
    public string Name => "#29 preview reads at most 1 MiB plus one overflow probe";

    public async Task RunAsync()
    {
        foreach (var length in new[] { 0, 8, PreviewReader.Limit - 1, PreviewReader.Limit })
        {
            using var stream = new GrowingStream(length);
            var result = await PreviewReader.ReadAsync(stream, CancellationToken.None);
            RegressionCases.Check(result.Length == length && result.All(b => b == 65), "Bounded preview changed valid content.");
        }
        // Non-seekable stream advertises no length and can grow without an EOF.
        using var growing = new GrowingStream(long.MaxValue);
        await RegressionCases.ThrowsAsync<IOException>(() => PreviewReader.ReadAsync(growing, CancellationToken.None));
        RegressionCases.Check(growing.ReadCount == PreviewReader.Limit + 1, "Preview read past its overflow probe.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var unread = new GrowingStream(long.MaxValue);
        await RegressionCases.ThrowsAsync<OperationCanceledException>(() => PreviewReader.ReadAsync(unread, cancelled.Token));
        RegressionCases.Check(unread.ReadCount == 0, "Cancelled preview consumed data.");
    }

    private sealed class GrowingStream(long available) : Stream
    {
        public long ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var count = (int)Math.Min(Math.Min(buffer.Length, 733), available - ReadCount);
            buffer.Span[..count].Fill(65);
            ReadCount += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
