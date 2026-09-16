using System.Text.Json;
using System.Text.Json.Serialization;
using MySsh.Core;

namespace MySsh.Infrastructure;

public sealed class SettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _sync = new();

    public string DirectoryPath { get; }
    public AppConfig Config { get; private set; }
    public AppState State { get; private set; }

    public static string DefaultDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "my-ssh");

            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = !string.IsNullOrWhiteSpace(xdg) && Path.IsPathFullyQualified(xdg)
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "my-ssh");
        }
    }

    public SettingsStore(string? directory = null)
    {
        DirectoryPath = directory ?? DefaultDirectory;
        Directory.CreateDirectory(DirectoryPath);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(DirectoryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Config = LoadConfig();
        State = LoadState();
    }

    private AppConfig LoadConfig()
    {
        var path = Path.Combine(DirectoryPath, "config.json");
        if (!File.Exists(path))
        {
            var created = new AppConfig();
            AtomicJson(path, created);
            return created;
        }

        var text = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(text);
        var version = doc.RootElement.TryGetProperty("version", out var value) ? value.GetInt32() : 1;
        if (version > 2)
            throw new IOException("config.json was written by a newer version of my-ssh.");

        var config = JsonSerializer.Deserialize<AppConfig>(text, Json)
            ?? throw new IOException("config.json is empty.");
        Validate(config);

        if (version < 2)
        {
            var backup = path + ".v1.bak";
            if (!File.Exists(backup))
            {
                try
                {
                    File.Copy(path, backup);
                }
                catch (IOException) when (File.Exists(backup))
                {
                    // Another concurrently-starting instance created the same migration backup.
                }
            }
            config.Version = 2;
            AtomicJson(path, config);
        }

        return config;
    }

    private AppState LoadState()
    {
        var path = Path.Combine(DirectoryPath, "state.json");
        if (!File.Exists(path))
        {
            var created = new AppState();
            AtomicJson(path, created);
            return created;
        }

        var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path), Json)
            ?? throw new IOException("state.json is empty.");
        if (state.Version != 1 || state.Connections is null)
            throw new IOException("Unsupported or malformed state.json; it was not overwritten.");

        foreach (var browser in state.Connections.Values)
            browser.Bookmarks ??= [];
        return state;
    }

    private static void Validate(AppConfig config)
    {
        if (config.Servers is null || config.Keys is null)
            throw new IOException("Malformed configuration; it was not overwritten.");
        if (config.ParallelTransfers is < 1 or > 4)
            throw new IOException("parallelTransfers must be between 1 and 4.");

        config.Editor ??= "";
        config.EditorArguments ??= [];

        foreach (var server in config.Servers)
        {
            server.Users ??= [];
            if (string.IsNullOrWhiteSpace(server.Host))
                throw new IOException("A configured server has an empty host.");
            foreach (var user in server.Users)
                new Connection(server.Host, user).Validate();
        }
    }

    public BrowserState Browser(Connection connection)
    {
        if (!State.Connections.TryGetValue(connection.Key, out var state))
        {
            state = new BrowserState();
            State.Connections[connection.Key] = state;
        }
        state.Bookmarks ??= [];
        return state;
    }

    public void SaveConfig()
    {
        Validate(Config);
        lock (_sync) AtomicJson(Path.Combine(DirectoryPath, "config.json"), Config);
    }

    public void SaveState()
    {
        lock (_sync) AtomicJson(Path.Combine(DirectoryPath, "state.json"), State);
    }

    private static void AtomicJson<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using (var stream = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Dispose()
    {
        // Settings are stored atomically; no process-wide lock is intentionally held so
        // multiple SSH/file-manager sessions can run at the same time.
    }
}
