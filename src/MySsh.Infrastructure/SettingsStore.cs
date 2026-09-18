using System.Text.Json;
using System.Text.Json.Nodes;
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
    private JsonNode _configBaseline;
    private JsonNode _stateBaseline;
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
                ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "my-ssh");
        }
    }

    public SettingsStore(string? directory = null)
    {
        DirectoryPath = Path.GetFullPath(directory ?? DefaultDirectory);
        Directory.CreateDirectory(DirectoryPath);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var fileLock = SettingsConcurrency.Acquire(DirectoryPath);
        Config = LoadConfig();
        State = LoadState();
        _configBaseline = Snapshot(Config);
        _stateBaseline = Snapshot(State);
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
        if (version > 2) throw new IOException("config.json was written by a newer version of my-ssh.");
        var config = JsonSerializer.Deserialize<AppConfig>(text, Json) ?? throw new IOException("config.json is empty.");
        Validate(config);
        if (version < 2)
        {
            var backup = path + ".v1.bak";
            if (!File.Exists(backup)) File.Copy(path, backup);
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
        {
            if (browser is null) throw new IOException("A saved browser state is null.");
            browser.Bookmarks ??= [];
        }
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
            if (server is null) throw new IOException("A configured server is null.");
            server.Users ??= [];
            new Connection(server.Host, "validation").Validate();
            foreach (var user in server.Users) new Connection(server.Host, user).Validate();
        }
    }

    public BrowserState Browser(Connection connection)
    {
        lock (_sync)
        {
            if (!State.Connections.TryGetValue(connection.Key, out var state))
                State.Connections[connection.Key] = state = new BrowserState();
            state.Bookmarks ??= [];
            return state;
        }
    }

    public void SaveConfig()
    {
        lock (_sync)
        {
            Validate(Config);
            _configBaseline = SaveMerged("config.json", _configBaseline, Snapshot(Config));
        }
    }

    public void SaveState()
    {
        lock (_sync) _stateBaseline = SaveMerged("state.json", _stateBaseline, Snapshot(State));
    }

    private JsonNode SaveMerged(string name, JsonNode baseline, JsonNode local)
    {
        using var fileLock = SettingsConcurrency.Acquire(DirectoryPath);
        var path = Path.Combine(DirectoryPath, name);
        if (!File.Exists(path))
            throw new IOException(name + " was removed by another process; it was not recreated from stale state.");
        var disk = JsonNode.Parse(File.ReadAllText(path)) ?? throw new IOException(name + " is empty.");
        var merged = SettingsConcurrency.Merge(baseline, local, disk, name);
        AtomicJson(path, merged);
        // Track what this instance actually knows, not the merged disk value.
        // That lets subsequent saves preserve fields changed by other instances
        // without replacing BrowserState references already owned by a live UI.
        return local;
    }

    private static JsonNode Snapshot<T>(T value) => JsonSerializer.SerializeToNode(value, Json)
        ?? throw new IOException("Cannot snapshot settings.");

    private static void AtomicJson<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
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

    public void Dispose() { }
}
