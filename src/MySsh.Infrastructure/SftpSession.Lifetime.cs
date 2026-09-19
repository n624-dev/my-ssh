namespace MySsh.Infrastructure;

internal sealed partial class SftpSession : IAsyncDisposable
{
    private static readonly TimeSpan DirectoryCloseTimeout = TimeSpan.FromSeconds(2);

    private async Task CloseDirectoryAsync(byte[] handle, Exception? enumerationError)
    {
        if (IsFaulted) return;
        using var timeout = new CancellationTokenSource(DirectoryCloseTimeout);
        try
        {
            // An already-cancelled caller must not skip cleanup. Use a separate,
            // bounded token so a silent server cannot prevent shutdown forever.
            await CloseAsync(handle, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // If CLOSE was rejected or its reply never arrived, discard the
            // session to release server handles. Preserve a preceding read error.
            FaultTransport(ex);
            if (enumerationError is null)
                throw new IOException("SFTP directory cleanup failed or timed out; the session was discarded.", ex);
        }
    }
}
