using System.Diagnostics;
using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class SftpPipelineTests : IRegressionCase
{
    public string Name => "SFTP bounded pipelines preserve ordering, failures, cancellation and improve latency throughput";

    public async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await ReorderedAsync(ct);
        await ShortReadsAsync(ct);
        await ErrorsAsync(ct);
        await CancellationAsync(ct);
        await BenchmarkAsync(ct);
        await CompareWindowsAsync(ct);
    }

    private static async Task ReorderedAsync(CancellationToken ct)
    {
        var first = Enumerable.Repeat((byte)1, 32768).ToArray();
        var second = Enumerable.Repeat((byte)2, 123).ToArray();
        var packets = new[] { ScriptedSftp.Version(),
            ScriptedSftp.Packet(103, ScriptedSftp.U32(2), ScriptedSftp.Blob(second)),
            ScriptedSftp.Packet(103, ScriptedSftp.U32(1), ScriptedSftp.Blob(first)) };
        await using var session = await SftpSession.ConnectAsync(new MemoryStream(),
            new MemoryStream(packets.SelectMany(p => p).ToArray()), ct);
        var buffer = new byte[first.Length + second.Length];
        RegressionCases.Check(await session.ReadPipelinedAsync([1], 0, buffer, ct) == buffer.Length &&
            buffer.SequenceEqual(first.Concat(second)), "Out-of-order DATA replies corrupted file offsets.");
        var wrongIds = new[] { ScriptedSftp.Version(), ScriptedSftp.Status(1, 0), ScriptedSftp.Status(1, 0) };
        await using var broken = await SftpSession.ConnectAsync(new MemoryStream(),
            new MemoryStream(wrongIds.SelectMany(p => p).ToArray()), ct);
        await RegressionCases.ThrowsAsync<SftpRequestInterruptedException>(() => broken.WritePipelinedAsync([1], 0, buffer, ct));
        RegressionCases.Check(broken.IsFaulted, "Duplicate batch response IDs did not discard the session.");
        foreach (var payload in new[] { ScriptedSftp.U32(10), ScriptedSftp.Blob([]) })
        {
            var malformed = new[] { ScriptedSftp.Version(), ScriptedSftp.Packet(103, ScriptedSftp.U32(1), payload) };
            await using var invalid = await SftpSession.ConnectAsync(new MemoryStream(),
                new MemoryStream(malformed.SelectMany(p => p).ToArray()), ct);
            await RegressionCases.ThrowsAsync<SftpRequestInterruptedException>(() => invalid.ReadPipelinedAsync([1], 0, new byte[20], ct));
            RegressionCases.Check(invalid.IsFaulted, "Malformed or empty DATA left the session reusable.");
        }
    }

    private static async Task ShortReadsAsync(CancellationToken ct)
    {
        var bytes = new byte[SftpSession.PipelineBytes + 123];
        new Random(84).NextBytes(bytes);
        await using var peer = new PipelinePeer(bytes) { MaximumRead = 7001 };
        await using var session = await SftpSession.ConnectAsync(peer.Input, peer.Output, ct);
        var buffer = new byte[SftpSession.PipelineBytes];
        using var copy = new MemoryStream();
        while (true)
        {
            var count = await session.ReadPipelinedAsync([1], (ulong)copy.Length, buffer, ct);
            if (count == 0) break;
            copy.Write(buffer, 0, count);
        }
        RegressionCases.Check(copy.ToArray().SequenceEqual(bytes), "Short DATA responses or EOF introduced gaps or duplicated bytes.");
        var slice = new byte[19];
        await session.ReadPipelinedAsync([1], 32760, slice, ct);
        RegressionCases.Check(slice.SequenceEqual(bytes.Skip(32760).Take(19)), "A read after seeking returned stale prefetched data.");
    }

    private static async Task ErrorsAsync(CancellationToken ct)
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        var source = root.File("source");
        await File.WriteAllBytesAsync(source, new byte[SftpSession.PipelineBytes + 123], ct);
        foreach (var closeFails in new[] { false, true })
        {
            await using var peer = new PipelinePeer { FailWriteNumber = closeFails ? 0 : SftpSession.PipelineRequests, FailClose = closeFails };
            var session = await SftpSession.ConnectAsync(peer.Input, peer.Output, ct);
            await using var remote = new SftpFileSystem(new("fixture", "user"), session);
            var destination = new StreamDestination(local, remote);
            await RegressionCases.ThrowsAsync<IOException>(() => new TransferEngine().CopyAsync(local, source, destination,
                root.File("destination"), new(Move: true), null, ct));
            RegressionCases.Check(File.Exists(source) && destination.Renames == 0 && peer.CloseCount == 1,
                "A failed WRITE/CLOSE committed the destination or deleted the source.");
            RegressionCases.Check(!session.IsFaulted, "A fully drained server error unnecessarily poisoned the session.");
            // The stream's CLOSE consumed the reply after all batch errors.
            await session.WriteAsync([1], 0, new byte[] { 1 }, ct);
        }
        await using var failing = new PipelinePeer { FailSendNumber = 3 };
        await using var interrupted = await SftpSession.ConnectAsync(failing.Input, failing.Output, ct);
        var error = await RegressionCases.ThrowsAsync<SftpRequestInterruptedException>(() =>
            interrupted.WritePipelinedAsync([1], 0, new byte[150_000], ct));
        RegressionCases.Check(error.OutcomeUnknown && interrupted.IsFaulted, "Partial batch send did not preserve unknown outcome.");
    }

    private static async Task CancellationAsync(CancellationToken ct)
    {
        foreach (var write in new[] { false, true })
        {
            await using var peer = new PipelinePeer(new byte[SftpSession.PipelineBytes]) { HoldReplies = true };
            await using var session = await SftpSession.ConnectAsync(peer.Input, peer.Output, ct);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // More than one window must not send request 65 before any reply.
            var buffer = new byte[SftpSession.PipelineBytes + SftpSession.DataChunkSize];
            Task active = write ? session.WritePipelinedAsync([1], 0, buffer, stop.Token)
                : session.ReadPipelinedAsync([1], 0, buffer, stop.Token);
            await peer.WindowReceived.Task.WaitAsync(ct);
            RegressionCases.Check(peer.DataRequestCount == SftpSession.PipelineRequests &&
                peer.MaximumOutstanding == SftpSession.PipelineRequests, "Unacknowledged requests exceeded the window.");
            using var queued = new CancellationTokenSource();
            var waiting = session.WritePipelinedAsync([1], 0, buffer, queued.Token);
            queued.Cancel();
            await RegressionCases.ThrowsAsync<OperationCanceledException>(() => waiting);
            RegressionCases.Check(!session.IsFaulted, "Queued cancellation broke an active batch.");
            stop.Cancel();
            var error = await RegressionCases.ThrowsAsync<SftpRequestCanceledException>(() => active.WaitAsync(ct));
            RegressionCases.Check(session.IsFaulted && error.OutcomeUnknown == write, "In-flight cancellation did not invalidate the batch correctly.");
            await RegressionCases.ThrowsAsync<IOException>(() => session.CloseAsync([1], ct));
        }
    }

    private static async Task BenchmarkAsync(CancellationToken ct)
    {
        var bytes = new byte[1024 * 1024];
        new Random(85).NextBytes(bytes);
        foreach (var write in new[] { false, true })
        {
            var elapsed = new List<double>();
            foreach (var pipelined in new[] { false, true })
            {
                await using var peer = new PipelinePeer(write ? null : bytes) { DelayMilliseconds = 30 };
                await using var session = await SftpSession.ConnectAsync(peer.Input, peer.Output, ct);
                var read = new byte[bytes.Length];
                var watch = Stopwatch.StartNew();
                if (pipelined)
                {
                    if (write) await session.WritePipelinedAsync([1], 0, bytes, ct);
                    else RegressionCases.Check(await session.ReadPipelinedAsync([1], 0, read, ct) == bytes.Length, "Short benchmark read.");
                }
                else for (var offset = 0; offset < bytes.Length; offset += 32768)
                {
                    if (write) await session.WriteAsync([1], (ulong)offset, bytes.AsMemory(offset, 32768), ct);
                    else (await session.ReadAsync([1], (ulong)offset, 32768, ct)).CopyTo(read, offset);
                }
                elapsed.Add(watch.Elapsed.TotalMilliseconds);
                RegressionCases.Check(bytes.SequenceEqual(write ? peer.Contents : read), "Benchmark data was corrupted.");
                RegressionCases.Check(peer.MaximumOutstanding >= (pipelined ? 2 : 1) && peer.MaximumOutstanding <= SftpSession.PipelineRequests,
                    "Requests were serialized or exceeded the pipeline bound.");
            }
            Console.WriteLine($"BENCH SFTP {(write ? "upload" : "download")} 1 MiB / 30 ms response delay: serial {elapsed[0]:F0} ms, pipeline {elapsed[1]:F0} ms ({elapsed[0] / elapsed[1]:F1}x)");
            RegressionCases.Check(elapsed[1] < elapsed[0] / 2, "Pipelining did not improve the controlled-latency transfer.");
        }
    }

    private static async Task CompareWindowsAsync(CancellationToken ct)
    {
        var bytes = new byte[8 * 1024 * 1024];
        new Random(86).NextBytes(bytes);
        foreach (var write in new[] { false, true })
        {
            var samples = new[] { new List<double>(), new List<double>() };
            for (var trial = 0; trial < 3; trial++)
            for (var profile = 0; profile < 2; profile++)
            {
                // Alternate order and take medians to reduce warm-up/timer noise.
                var index = (trial + profile) % 2;
                var window = index == 0 ? 1024 * 1024 : SftpSession.PipelineBytes;
                await using var peer = new PipelinePeer(write ? null : bytes) { DelayMilliseconds = 30 };
                await using var session = await SftpSession.ConnectAsync(peer.Input, peer.Output, ct);
                var read = new byte[bytes.Length];
                var watch = Stopwatch.StartNew();
                for (var offset = 0; offset < bytes.Length; offset += window)
                {
                    var count = Math.Min(window, bytes.Length - offset);
                    if (write) await session.WritePipelinedAsync([1], (ulong)offset, bytes.AsMemory(offset, count), ct);
                    else RegressionCases.Check(await session.ReadPipelinedAsync([1], (ulong)offset,
                        read.AsMemory(offset, count), ct) == count, "Window comparison read was short.");
                }
                samples[index].Add(watch.Elapsed.TotalMilliseconds);
                RegressionCases.Check(bytes.SequenceEqual(write ? peer.Contents : read), "Window comparison corrupted data.");
                RegressionCases.Check(peer.MaximumOutstanding <= window / SftpSession.DataChunkSize,
                    "Window comparison exceeded its request bound.");
            }
            var previous = samples[0].Order().ElementAt(1);
            var current = samples[1].Order().ElementAt(1);
            Console.WriteLine($"BENCH SFTP {(write ? "upload" : "download")} 8 MiB / 30 ms response delay (median of 3): 32 requests {previous:F0} ms, 64 requests {current:F0} ms ({previous / current:F2}x)");
            // Timings are measurements, not a brittle pass/fail threshold on busy CI.
        }
    }

    private sealed class StreamDestination(IFileSystem local, IFileSystem remote) : DelegatingFileSystem(local)
    {
        public int Renames;
        public override Task<Stream> OpenWriteAsync(string path, bool createNew, CancellationToken ct) => remote.OpenWriteAsync(path, createNew, ct);
        public override Task RenameAsync(string source, string destination, bool replace, CancellationToken ct)
        { Renames++; return base.RenameAsync(source, destination, replace, ct); }
    }
}
