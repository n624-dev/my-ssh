using MySsh.Core;

namespace MySsh.Infrastructure;

public sealed record TransferJobSnapshot(
    Guid Id,
    string Source,
    bool SourceIsRemote,
    string Destination,
    bool DestinationIsRemote,
    bool Move,
    TransferState State,
    long BytesTransferred,
    long? TotalBytes,
    int CompletedEntries,
    int TotalEntries,
    string Message,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt)
{
    public double? BytesPerSecond => StartedAt is { } started && BytesTransferred > 0
        ? BytesTransferred / Math.Max(0.001, ((FinishedAt ?? DateTimeOffset.UtcNow) - started).TotalSeconds)
        : null;
}

public sealed class TransferQueue : IAsyncDisposable
{
    private readonly TransferEngine _engine = new();
    private readonly SemaphoreSlim _parallel;
    private readonly Dictionary<Guid, Job> _jobs = new();
    private readonly object _sync = new();
    private bool _disposed;

    public TransferQueue(int parallelTransfers) =>
        _parallel = new SemaphoreSlim(parallelTransfers, parallelTransfers);

    public Guid Enqueue(IFileSystem source, string sourcePath, IFileSystem destination,
        string destinationPath, TransferOptions options)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var job = new Job(source, sourcePath, destination, destinationPath, options);
            _jobs.Add(job.Id, job);
            Start(job);
            return job.Id;
        }
    }

    public IReadOnlyList<TransferJobSnapshot> Snapshot()
    {
        lock (_sync) return _jobs.Values.OrderBy(x => x.CreatedAt).Select(x => x.Snapshot()).ToArray();
    }

    public bool Pause(Guid id)
    {
        lock (_sync)
        {
            if (_disposed || !_jobs.TryGetValue(id, out var job) ||
                job.State is not (TransferState.Running or TransferState.Queued)) return false;
            job.PauseRequested = true;
            job.Cancellation.Cancel();
            return true;
        }
    }

    public bool Cancel(Guid id)
    {
        lock (_sync)
        {
            if (_disposed || !_jobs.TryGetValue(id, out var job) ||
                job.State is TransferState.Completed or TransferState.Cancelled) return false;
            job.CancelRequested = true;
            job.PauseRequested = false;
            job.Cancellation.Cancel();
            if (job.State is not (TransferState.Running or TransferState.Queued))
            {
                job.State = TransferState.Cancelled;
                job.Message = "Cancelled. Partial data was retained for inspection or retry.";
                job.FinishedAt = DateTimeOffset.UtcNow;
            }
            return true;
        }
    }

    public bool Resume(Guid id)
    {
        lock (_sync)
        {
            if (_disposed || !_jobs.TryGetValue(id, out var job) || job.State != TransferState.Paused)
                return false;
            job.PrepareForRun(resetProgressClock: false);
            job.Message = "Queued for resume; completed and partial data will be verified using a fresh session.";
            Start(job);
            return true;
        }
    }

    public bool Retry(Guid id, ConflictAction? conflict = null, string? destinationPath = null)
    {
        lock (_sync)
        {
            if (_disposed || !_jobs.TryGetValue(id, out var job) ||
                job.State is not (TransferState.Failed or TransferState.Partial or TransferState.Cancelled))
                return false;
            job.PrepareForRun(resetProgressClock: true);
            if (conflict.HasValue) job.Options = job.Options with { Conflict = conflict.Value };
            if (!string.IsNullOrWhiteSpace(destinationPath) && destinationPath != job.DestinationPath)
            {
                job.DestinationPath = destinationPath;
                job.ResumeState = new();
            }
            job.Message = "Queued for retry with a fresh transfer session.";
            Start(job);
            return true;
        }
    }

    public bool Remove(Guid id)
    {
        lock (_sync)
        {
            if (_disposed || !_jobs.TryGetValue(id, out var job) ||
                job.State is TransferState.Running or TransferState.Queued) return false;
            _jobs.Remove(id);
            job.Cancellation.Dispose();
            return true;
        }
    }

    // Called under _sync: queueing the task keeps file I/O off the UI thread.
    private void Start(Job job)
    {
        job.State = TransferState.Queued;
        job.RunTask = Task.Run(() => RunAsync(job));
    }

    private async Task RunAsync(Job job)
    {
        var entered = false;
        IFileSystem? ownedSource = null;
        IFileSystem? ownedDestination = null;
        var finalState = TransferState.Failed;
        var finalMessage = "Transfer did not complete.";
        try
        {
            await _parallel.WaitAsync(job.Cancellation.Token).ConfigureAwait(false);
            entered = true;
            job.Cancellation.Token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                job.State = TransferState.Running;
                job.StartedAt ??= DateTimeOffset.UtcNow;
                job.Message = "Connecting transfer session...";
            }
            var source = job.Source;
            var destination = job.Destination;
            if (source is SftpFileSystem remoteSource)
                source = ownedSource = await remoteSource.CreateSiblingAsync(job.Cancellation.Token).ConfigureAwait(false);
            if (destination is SftpFileSystem remoteDestination)
                destination = ownedDestination = await remoteDestination.CreateSiblingAsync(job.Cancellation.Token).ConfigureAwait(false);
            var progress = new ImmediateProgress(value =>
            {
                lock (_sync)
                {
                    job.BytesTransferred = value.BytesTransferred;
                    job.TotalBytes = value.TotalBytes;
                    job.CompletedEntries = value.CompletedEntries;
                    job.TotalEntries = value.TotalEntries;
                    job.Message = value.Message;
                }
            });
            await _engine.CopyAsync(source, job.SourcePath, destination, job.DestinationPath,
                job.Options, progress, job.Cancellation.Token, job.ResumeState).ConfigureAwait(false);
            finalState = TransferState.Completed;
            lock (_sync) finalMessage = string.IsNullOrWhiteSpace(job.Message) ? "Completed" : job.Message;
        }
        catch (Exception ex) when (HasUnknownOutcome(ex))
        {
            // An interrupted state-changing request is not a clean Pause/Cancel.
            // Keep the warning even if Close/Move wraps the transport exception.
            finalState = TransferState.Partial;
            finalMessage = "Result unknown: the server may have performed an interrupted operation. " +
                "Reconnect and inspect source/destination before retrying. " + ex.Message;
        }
        catch (OperationCanceledException)
        {
            // Cancellation state is resolved after cleanup so a pending Cancel overrides Pause.
            finalState = TransferState.Cancelled;
        }
        catch (PartialMoveException ex)
        {
            finalState = TransferState.Partial;
            finalMessage = ex.Message + " " + ex.InnerException?.Message;
        }
        catch (TransferConflictException ex)
        {
            finalMessage = ex.Message + " Choose overwrite, skip, rename, or cancel before retrying.";
        }
        catch (Exception ex) { finalMessage = ex.Message; }
        finally
        {
            if (ownedDestination is not null)
            {
                try { await ownedDestination.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            if (ownedSource is not null)
            {
                try { await ownedSource.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            if (entered) _parallel.Release();
            // Publish a terminal state only when the old worker no longer uses its resources.
            lock (_sync)
            {
                if (finalState == TransferState.Cancelled)
                {
                    finalState = job.PauseRequested && !job.CancelRequested && !_disposed
                        ? TransferState.Paused : TransferState.Cancelled;
                    finalMessage = finalState == TransferState.Paused
                        ? "Paused. Resume will verify completed and partial data using a fresh session."
                        : "Cancelled. Partial data was retained for inspection or retry.";
                }
                job.State = finalState;
                job.Message = finalMessage;
                job.FinishedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    private static bool HasUnknownOutcome(Exception exception)
    {
        if (exception is ISftpRequestInterruption { OutcomeUnknown: true }) return true;
        if (exception is AggregateException aggregate) return aggregate.InnerExceptions.Any(HasUnknownOutcome);
        return exception.InnerException is { } inner && HasUnknownOutcome(inner);
    }

    public async ValueTask DisposeAsync()
    {
        Task[] workers;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var job in _jobs.Values) job.Cancellation.Cancel();
            workers = _jobs.Values.Select(x => x.RunTask).ToArray();
        }
        // Include queued workers and session cleanup, not just jobs labelled Running.
        await Task.WhenAll(workers).ConfigureAwait(false);
        lock (_sync)
        {
            foreach (var job in _jobs.Values) job.Cancellation.Dispose();
            _parallel.Dispose();
        }
    }

    private sealed class ImmediateProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }

    private sealed class Job(IFileSystem source, string sourcePath, IFileSystem destination,
        string destinationPath, TransferOptions options)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public IFileSystem Source { get; } = source;
        public string SourcePath { get; } = sourcePath;
        public IFileSystem Destination { get; } = destination;
        public string DestinationPath { get; set; } = destinationPath;
        public TransferOptions Options { get; set; } = options;
        public TransferResumeState ResumeState { get; set; } = new();
        public TransferState State { get; set; } = TransferState.Queued;
        public Task RunTask { get; set; } = Task.CompletedTask;
        public CancellationTokenSource Cancellation { get; private set; } = new();
        public bool PauseRequested { get; set; }
        public bool CancelRequested { get; set; }
        public long BytesTransferred { get; set; }
        public long? TotalBytes { get; set; }
        public int CompletedEntries { get; set; }
        public int TotalEntries { get; set; }
        public string Message { get; set; } = "Queued";
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }

        public void PrepareForRun(bool resetProgressClock)
        {
            Cancellation.Dispose();
            Cancellation = new CancellationTokenSource();
            PauseRequested = false;
            CancelRequested = false;
            FinishedAt = null;
            if (resetProgressClock)
            {
                StartedAt = null;
                BytesTransferred = 0;
                TotalBytes = null;
                CompletedEntries = 0;
                TotalEntries = 0;
            }
        }

        public TransferJobSnapshot Snapshot() => new(Id, SourcePath, Source.IsRemote,
            DestinationPath, Destination.IsRemote, Options.Move, State, BytesTransferred,
            TotalBytes, CompletedEntries, TotalEntries, Message, CreatedAt, StartedAt, FinishedAt);
    }
}
