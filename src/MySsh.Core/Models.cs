using System.Text.Json.Serialization;

namespace MySsh.Core;

public sealed record Connection(string Host, string User)
{
    public string Key => $"{User}@{Host}";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(User))
            throw new ArgumentException("Host and user are required.");
        if (Host.StartsWith('-') || Host.Contains('@') || Host.Any(char.IsWhiteSpace) || Host.Any(char.IsControl))
            throw new ArgumentException("Host must be an SSH host alias or hostname.");
        if (User.StartsWith('-') || User.Any(char.IsWhiteSpace) || User.Any(char.IsControl))
            throw new ArgumentException("Invalid SSH user name.");
    }
}

public sealed class AppConfig
{
    public int Version { get; set; } = 2;
    public List<ServerConfig> Servers { get; set; } = [];
    public int ParallelTransfers { get; set; } = 2;
    public bool PreserveMetadata { get; set; } = true;
    public string Editor { get; set; } = "";
    public List<string> EditorArguments { get; set; } = [];
    public Dictionary<string, string> Keys { get; set; } = new()
    {
        ["copy"] = "F5",
        ["move"] = "F6",
        ["rename"] = "F2",
        ["mkdir"] = "F7",
        ["delete"] = "DeleteChar",
        ["actions"] = "F9",
        ["help"] = "F1"
    };
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

public sealed class ServerConfig
{
    public string Host { get; set; } = "";
    public List<string> Users { get; set; } = [];
}

public sealed class AppState
{
    public int Version { get; set; } = 1;
    public Dictionary<string, BrowserState> Connections { get; set; } = [];
}

public sealed class BrowserState
{
    public string LocalPath { get; set; } = "";
    public string RemotePath { get; set; } = ".";
    public string? LocalSelection { get; set; }
    public string? RemoteSelection { get; set; }
    public bool ShowHidden { get; set; }
    public List<Bookmark> Bookmarks { get; set; } = [];
}

public sealed record Bookmark(string Name, string LocalPath, string RemotePath);

public enum EntryKind { File, Directory, SymbolicLink, Other }

public sealed record FileEntry(
    string Path,
    string Name,
    EntryKind Kind,
    long Length,
    DateTimeOffset Modified,
    uint? Mode = null,
    string? LinkTarget = null)
{
    public bool IsDirectory => Kind == EntryKind.Directory;
}

public interface IFileSystem : IAsyncDisposable
{
    bool IsRemote { get; }
    StringComparison PathComparison { get; }
    string Join(string directory, string name);
    string Parent(string path);
    Task<string> CanonicalAsync(string path, CancellationToken cancellationToken);
    Task<FileEntry?> StatAsync(string path, CancellationToken cancellationToken);
    Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);
    Task<Stream> OpenWriteAsync(string path, bool createNew, CancellationToken cancellationToken);
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken);
    Task RenameAsync(string source, string destination, bool replace, CancellationToken cancellationToken);
    Task DeleteAsync(string path, bool directory, CancellationToken cancellationToken);
    Task SetMetadataAsync(string path, DateTimeOffset modified, uint? mode, CancellationToken cancellationToken);
    Task<string> ReadLinkAsync(string path, CancellationToken cancellationToken);
    Task CreateLinkAsync(string path, string target, CancellationToken cancellationToken);
}

public static class PathSafety
{
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"
    };

    public static void ValidateChildName(string name, bool windows)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains('/') || name.Contains('\0'))
            throw new IOException("Invalid filename component.");
        if (!windows) return;

        if (name.Any(c => c < 32 || "<>:\"\\|?*".Contains(c)) || name.EndsWith(' ') || name.EndsWith('.'))
            throw new IOException("The filename is not valid on Windows.");

        var stem = name.Split('.', 2)[0];
        if (WindowsReservedNames.Contains(stem) || IsReservedComOrLpt(stem))
            throw new IOException($"Reserved Windows device filename: {name}");
    }

    private static bool IsReservedComOrLpt(string stem)
    {
        if (stem.Length != 4) return false;
        if (!stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
            !stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            return false;
        return stem[3] is >= '1' and <= '9' or '¹' or '²' or '³';
    }

    public static void ProtectRoot(string path, IFileSystem fileSystem)
    {
        if (string.IsNullOrWhiteSpace(path) || path is "." or ".." ||
            fileSystem.Parent(path).Equals(path, fileSystem.PathComparison))
            throw new IOException("The root directory or parent entry cannot be modified.");
    }
}

public static class Completion
{
    public static IReadOnlyList<string> Matching(IEnumerable<string> values, string prefix) => values
        .Where(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static string LongestCommonPrefix(IEnumerable<string> values, string typed)
    {
        var candidates = Matching(values, typed);
        if (candidates.Count == 0) return typed;
        var first = candidates[0];
        var length = first.Length;
        foreach (var value in candidates.Skip(1))
        {
            var i = 0;
            while (i < length && i < value.Length &&
                   char.ToUpperInvariant(first[i]) == char.ToUpperInvariant(value[i])) i++;
            length = i;
        }
        return length > typed.Length ? first[..length] : typed;
    }
}
