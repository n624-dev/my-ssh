namespace MySsh.Core;

/// <summary>Identifies a known path namespace, not a connection's lifetime.</summary>
/// <remarks>Equal paths on unrelated servers must not be considered the same file.</remarks>
public interface IFileSystemNamespace
{
    string PathNamespace { get; }
}
