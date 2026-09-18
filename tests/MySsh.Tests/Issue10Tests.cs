using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class Issue10Tests : IRegressionCase
{
    public string Name => "#10 changed and mixed-version source data cannot be committed by copy or move";

    public async Task RunAsync()
    {
        foreach (var move in new[] { false, true })
        foreach (var change in new[] { "same-size-same-time", "timestamp", "growth" })
        {
            using var root = new TestDirectory();
            using var local = new LocalFileSystem();
            var path = root.File("source.bin");
            var target = root.File("destination.bin");
            await File.WriteAllBytesAsync(path, new byte[900_001]);
            await File.WriteAllTextAsync(target, "destination must survive");
            var originalTime = File.GetLastWriteTimeUtc(path);
            var changedBytes = Enumerable.Repeat((byte)0xA5, change == "growth" ? 900_002 : 900_001).ToArray();
            var changed = false;
            var completed = false;
            var progress = new InlineProgress(p =>
            {
                completed |= p.State == TransferState.Completed;
                if (changed || p.BytesTransferred < 256 * 1024) return;
                changed = true;
                File.WriteAllBytes(path, changedBytes);
                File.SetLastWriteTimeUtc(path, change == "timestamp" ? originalTime.AddMinutes(1) : originalTime);
            });
            // Read fresh chunks without a Windows FileShare lock, modeling a remote
            // or Unix file concurrently changed by another application. The first
            // chunk is old; subsequent chunks are new, not a coherent snapshot.
            var source = new LiveFileSystem(local);
            await RegressionCases.ThrowsAsync<IOException>(() => new TransferEngine().CopyAsync(source, path,
                local, target, new(Move: move, Conflict: ConflictAction.Overwrite), progress, CancellationToken.None));
            RegressionCases.Check(changed && !completed, "changed source was reported as completed");
            RegressionCases.Check(await File.ReadAllTextAsync(target) == "destination must survive", "mixed data was committed");
            RegressionCases.Check(File.Exists(path) && (await File.ReadAllBytesAsync(path)).SequenceEqual(changedBytes),
                "source was deleted or modified after failed verification");
        }
    }

    private sealed class InlineProgress(Action<TransferProgress> action) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => action(value);
    }

    private sealed class LiveFileSystem(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public override Task<Stream> OpenReadAsync(string path, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(new LiveReadStream(path));
        }
    }

    private sealed class LiveReadStream(string path) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => new FileInfo(path).Length;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            var count = (int)Math.Min(buffer.Length, Math.Max(0, bytes.LongLength - Position));
            if (count == 0) return 0;
            bytes.AsMemory(checked((int)Position), count).CopyTo(buffer);
            Position += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            var value = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(Position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (value < 0) throw new IOException("Negative seek.");
            return Position = value;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
