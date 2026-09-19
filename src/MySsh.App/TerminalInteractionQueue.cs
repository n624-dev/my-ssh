using MySsh.Infrastructure;

namespace MySsh.App;

/// <summary>
/// Workers enqueue handshakes without touching the console. The application
/// drains them only after its TUI has shut down. Established transfers keep
/// running; no password, key or OpenSSH configuration is cached here.
/// </summary>
internal sealed class TerminalInteractionQueue : IConnectionInteraction, IDisposable
{
    private readonly object _sync = new();
    private readonly Queue<IRequest> _pending = new();
    private readonly SemaphoreSlim _consumer = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private IRequest? _active;
    private bool _disposed;

    public bool HasPending
    {
        get { lock (_sync) return _pending.Any(request => !request.IsCompleted); }
    }

    public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> connect, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connect);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var request = new Request<T>(connect, cancellation);
            _pending.Enqueue(request);
            return request.WaitAsync();
        }
    }

    // Even if two callers accidentally pump, only one handshake owns the terminal.
    public async Task DrainAsync()
    {
        await _consumer.WaitAsync().ConfigureAwait(false);
        try
        {
            while (true)
            {
                IRequest request;
                lock (_sync)
                {
                    if (_disposed || !_pending.TryDequeue(out request!)) return;
                    _active = request;
                }
                try { await request.ExecuteAsync().ConfigureAwait(false); }
                finally { lock (_sync) _active = null; }
            }
        }
        finally { _consumer.Release(); }
    }

    public void CancelPending()
    {
        IRequest[] requests;
        lock (_sync)
            requests = _active is { } active ? [.. _pending, active] : [.. _pending];
        foreach (var request in requests) request.Cancel();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // Wakes jobs which never reached the console, including jobs queued in
        // a nested dialog. Shutdown must not wait for a UI pump that has exited.
        _lifetime.Cancel();
        lock (_sync) _pending.Clear();
        _lifetime.Dispose();
    }

    private interface IRequest
    {
        bool IsCompleted { get; }
        Task ExecuteAsync();
        void Cancel();
    }

    private sealed class Request<T>(Func<CancellationToken, Task<T>> connect,
        CancellationTokenSource cancellation) : IRequest
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;
        public bool IsCompleted => _completion.Task.IsCompleted;

        public async Task<T> WaitAsync()
        {
            using (cancellation)
            using (cancellation.Token.Register(() =>
            {
                // Once started, the operation owns its result/cleanup. Do not
                // orphan a successfully-created connection by cancelling its TCS.
                if (Interlocked.CompareExchange(ref _started, 2, 0) == 0)
                    _completion.TrySetCanceled(cancellation.Token);
            }))
                return await _completion.Task.ConfigureAwait(false);
        }

        public async Task ExecuteAsync()
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                _completion.TrySetResult(await connect(cancellation.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException ex) { _completion.TrySetCanceled(ex.CancellationToken); }
            catch (Exception ex) { _completion.TrySetException(ex); }
        }

        public void Cancel()
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* Completion already transferred ownership. */ }
        }
    }
}
