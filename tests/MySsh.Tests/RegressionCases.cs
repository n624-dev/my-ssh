using System.Reflection;
using MySsh.Core;
using MySsh.Infrastructure;

internal interface IRegressionCase
{
    string Name { get; }
    Task RunAsync();
}

internal static class RegressionCases
{
    public static IEnumerable<(string Name, Func<Task> Run)> Load() =>
        typeof(RegressionCases).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IRegressionCase).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => (IRegressionCase)(Activator.CreateInstance(t)
                ?? throw new InvalidOperationException($"Cannot create {t.Name}.")))
            .Select(test => (test.Name, (Func<Task>)test.RunAsync));

    public static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

internal sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "myssh-regression-" + Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal class DelegatingFileSystem(IFileSystem inner) : IFileSystem
{
    public virtual bool IsRemote => inner.IsRemote;
    public virtual StringComparison PathComparison => inner.PathComparison;
    public virtual string Join(string directory, string name) => inner.Join(directory, name);
    public virtual string Parent(string path) => inner.Parent(path);
    public virtual Task<string> CanonicalAsync(string path, CancellationToken ct) => inner.CanonicalAsync(path, ct);
    public virtual Task<FileEntry?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);
    public virtual Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct) => inner.ListAsync(path, ct);
    public virtual Task<Stream> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
    public virtual Task<Stream> OpenWriteAsync(string path, bool createNew, CancellationToken ct) => inner.OpenWriteAsync(path, createNew, ct);
    public virtual Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
    public virtual Task RenameAsync(string source, string destination, bool replace, CancellationToken ct) => inner.RenameAsync(source, destination, replace, ct);
    public virtual Task DeleteAsync(string path, bool directory, CancellationToken ct) => inner.DeleteAsync(path, directory, ct);
    public virtual Task SetMetadataAsync(string path, DateTimeOffset modified, uint? mode, CancellationToken ct) => inner.SetMetadataAsync(path, modified, mode, ct);
    public virtual Task<string> ReadLinkAsync(string path, CancellationToken ct) => inner.ReadLinkAsync(path, ct);
    public virtual Task CreateLinkAsync(string path, string target, CancellationToken ct) => inner.CreateLinkAsync(path, target, ct);
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
