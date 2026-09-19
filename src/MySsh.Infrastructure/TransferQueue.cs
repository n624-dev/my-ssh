using MySsh.Core;

namespace MySsh.Infrastructure;

public sealed record TransferJobSnapshot(Guid Id, string Source, bool SourceIsRemote,
    string Destination, bool DestinationIsRemote, bool Move, TransferState State,
    long BytesTransferred, long? TotalBytes, int CompletedEntries, int TotalEntries,
    string Message, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt)
{
    public double? BytesPerSecond => StartedAt is { } started && BytesTransferred > 0
        ? BytesTransferred / Math.Max(0.001, ((FinishedAt ?? DateTimeOffset.UtcNow) - started).TotalSeconds) : null;
}

public sealed class TransferQueue : IAsyncDisposable
{
    private readonly TransferEngine _engine = new();
    private readonly SemaphoreSlim _parallel;
    private readonly Dictionary<Guid, Job> _jobs = new();
    private readonly object _sync = new();
    private readonly TransferJournal? _journal;
    private bool _disposed;
    public IReadOnlyList<string> RecoveryWarnings => _journal?.RecoveryWarnings.ToArray() ?? [];

    public TransferQueue(int parallelTransfers) => _parallel = new(parallelTransfers, parallelTransfers);

    // Recovery binds jobs only to these already selected endpoints. Loading a
    // checkpoint never opens a connection, starts a worker, or grants overwrite.
    public TransferQueue(int parallelTransfers, TransferJournal journal, IFileSystem local, IFileSystem remote)
        : this(parallelTransfers)
    {
        _journal = journal;
        foreach (var saved in journal.Recover())
        {
            var s = saved.Snapshot;
            var source = s.SourceIsRemote ? remote : local;
            var destination = s.DestinationIsRemote ? remote : local;
            if (source.IsRemote != s.SourceIsRemote || destination.IsRemote != s.DestinationIsRemote)
            {
                journal.RecoveryWarnings.Add("Checkpoint endpoint types do not match: " + s.Id);
                journal.Release(s.Id);
                continue;
            }
            var inspection = saved.Resume.SourceCleanupStarted && s.State != TransferState.Completed;
            var state = inspection ? TransferState.Partial : s.State is TransferState.Running or TransferState.Queued
                ? TransferState.Paused : s.State;
            var job = new Job(source, s.Source, destination, s.Destination, saved.Options with { Conflict = ConflictAction.Ask })
            {
                Id = s.Id, CreatedAt = s.CreatedAt, State = state, ResumeState = TransferResumeState.Restore(saved.Resume),
                BytesTransferred = s.BytesTransferred, TotalBytes = s.TotalBytes, CompletedEntries = s.CompletedEntries,
                TotalEntries = s.TotalEntries, FinishedAt = s.FinishedAt, RequiresInspection = inspection,
                Message = inspection ? "Source cleanup was interrupted. Inspect both sides; this job will not replay deletion. Remove it and explicitly queue remaining files." :
                    state == TransferState.Paused ? "Recovered paused job. Resume explicitly; receipts and partial data will be revalidated." : s.Message
            };
            BindCheckpoint(job);
            _jobs.Add(job.Id, job);
        }
    }

    public Guid Enqueue(IFileSystem source, string sourcePath, IFileSystem destination, string destinationPath, TransferOptions options)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var job = new Job(source, sourcePath, destination, destinationPath, options);
            _journal?.Claim(job.Id);
            try
            {
                BindCheckpoint(job);
                SaveJob(job);
                _jobs.Add(job.Id, job);
                Start(job);
                return job.Id;
            }
            catch { _jobs.Remove(job.Id); _journal?.Release(job.Id); throw; }
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
            if (_disposed || !_jobs.TryGetValue(id, out var job) || job.State is not (TransferState.Running or TransferState.Queued)) return false;
            job.PauseRequested = true;
            job.Cancellation.Cancel();
            return true;
        }
    }

    public bool Cancel(Guid id)
    {
        lock (_sync)
        {
            if (_disposed || !_jobs.TryGetValue(id, out var job) || job.State is TransferState.Completed or TransferState.Cancelled) return false;
            job.CancelRequested = true;
            job.PauseRequested = false;
            job.Cancellation.Cancel();
            if (job.State is not (TransferState.Running or TransferState.Queued))
            {
                job.State = TransferState.Cancelled;
                job.Message = "Cancelled. Partial data was retained for inspection or retry.";
                job.FinishedAt = DateTimeOffset.UtcNow;
                SaveJob(job);
            }
            return true;
        }
    }

    public bool Resume(Guid id)
    {
        lock (_sync)
        {
            if (_disposed || !_jobs.TryGetValue(id, out var job) || job.State != TransferState.Paused || job.RequiresInspection) return false;
            job.PrepareForRun(false);
            job.Message = "Queued for resume; completed and partial data will be verified using a fresh session.";
            Start(job);
            return true;
        }
    }

    public bool Retry(Guid id, ConflictAction? conflict = null, string? destinationPath = null)
    {
        lock (_sync)
        {
            if (_disposed || !_jobs.TryGetValue(id, out var job) || job.RequiresInspection ||
                job.State is not (TransferState.Failed or TransferState.Partial or TransferState.Cancelled)) return false;
            job.PrepareForRun(true);
            if (conflict.HasValue) job.Options = job.Options with { Conflict = conflict.Value };
            if (!string.IsNullOrWhiteSpace(destinationPath) && destinationPath != job.DestinationPath)
            {
                job.DestinationPath = destinationPath;
                job.ResumeState = new();
                BindCheckpoint(job);
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
            if (_disposed || !_jobs.TryGetValue(id, out var job) || job.State is TransferState.Running or TransferState.Queued) return false;
            _journal?.Remove(id);
            _jobs.Remove(id);
            job.Cancellation.Dispose();
            return true;
        }
    }

    private void BindCheckpoint(Job job) => job.ResumeState.Checkpoint = () => { lock (_sync) SaveJob(job); };
    private void SaveJob(Job job)
    {
        _journal?.Save(new(job.Snapshot(), job.Options, job.ResumeState.Capture()));
        job.LastCheckpoint = Environment.TickCount64;
    }

    private void Start(Job job)
    {
        var previous = job.State;
        job.State = TransferState.Queued;
        try { SaveJob(job); }
        catch { job.State = previous; throw; }
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
                SaveJob(job);
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
                    if (Environment.TickCount64 - job.LastCheckpoint >= 500) SaveJob(job);
                }
            });
            await _engine.CopyAsync(source, job.SourcePath, destination, job.DestinationPath,
                job.Options, progress, job.Cancellation.Token, job.ResumeState).ConfigureAwait(false);
            finalState = TransferState.Completed;
            lock (_sync) finalMessage = string.IsNullOrWhiteSpace(job.Message) ? "Completed" : job.Message;
        }
        catch (Exception ex) when (HasUnknownOutcome(ex))
        {
            finalState = TransferState.Partial;
            finalMessage = "Result unknown: the server may have performed an interrupted operation. " +
                "Reconnect and inspect source/destination before retrying. " + ex.Message;
        }
        catch (OperationCanceledException) { finalState = TransferState.Cancelled; }
        catch (PartialMoveException ex) { finalState = TransferState.Partial; finalMessage = ex.Message + " " + ex.InnerException?.Message; }
        catch (TransferConflictException ex) { finalMessage = ex.Message + " Choose overwrite, skip, rename, or cancel before retrying."; }
        catch (Exception ex) { finalMessage = ex.Message; }
        finally
        {
            if (ownedDestination is not null) { try { await ownedDestination.DisposeAsync().ConfigureAwait(false); } catch { } }
            if (ownedSource is not null) { try { await ownedSource.DisposeAsync().ConfigureAwait(false); } catch { } }
            if (entered) _parallel.Release();
            lock (_sync)
            {
                if (finalState == TransferState.Cancelled)
                {
                    var pause = !job.CancelRequested && (job.PauseRequested && !_disposed || _disposed && _journal is not null);
                    finalState = pause ? TransferState.Paused : TransferState.Cancelled;
                    finalMessage = pause ? "Paused. Resume will revalidate completed and partial data." : "Cancelled. Partial data was retained for inspection or retry.";
                }
                job.State = finalState;
                job.Message = finalMessage;
                job.FinishedAt = DateTimeOffset.UtcNow;
                try { SaveJob(job); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    job.State = TransferState.Partial;
                    job.Message = "Checkpoint could not be saved. Inspect files before retrying. " + ex.Message;
                }
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
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        finally
        {
            lock (_sync)
            {
                foreach (var job in _jobs.Values) job.Cancellation.Dispose();
                _parallel.Dispose();
                _journal?.Dispose();
            }
        }
    }

    private sealed class ImmediateProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }

    private sealed class Job(IFileSystem source, string sourcePath, IFileSystem destination, string destinationPath, TransferOptions options)
    {
        public Guid Id { get; init; } = Guid.NewGuid();
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
        public bool RequiresInspection { get; init; }
        public long LastCheckpoint { get; set; }
        public long BytesTransferred { get; set; }
        public long? TotalBytes { get; set; }
        public int CompletedEntries { get; set; }
        public int TotalEntries { get; set; }
        public string Message { get; set; } = "Queued";
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public void PrepareForRun(bool resetProgressClock)
        {
            Cancellation.Dispose();
            Cancellation = new();
            PauseRequested = false;
            CancelRequested = false;
            FinishedAt = null;
            if (resetProgressClock)
            {
                StartedAt = null; BytesTransferred = 0; TotalBytes = null; CompletedEntries = 0; TotalEntries = 0;
            }
        }
        public TransferJobSnapshot Snapshot() => new(Id, SourcePath, Source.IsRemote, DestinationPath, Destination.IsRemote,
            Options.Move, State, BytesTransferred, TotalBytes, CompletedEntries, TotalEntries, Message, CreatedAt, StartedAt, FinishedAt);
    }
}
