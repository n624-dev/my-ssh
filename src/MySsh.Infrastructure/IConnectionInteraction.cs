namespace MySsh.Infrastructure;

/// <summary>
/// Gives a host application ownership of the terminal while a connection may
/// ask for credentials. Applies to the complete handshake, not just Process.Start.
/// </summary>
public interface IConnectionInteraction
{
    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> connect, CancellationToken cancellationToken);
}
