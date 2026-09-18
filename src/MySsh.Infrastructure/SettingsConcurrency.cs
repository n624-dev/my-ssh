using System.Diagnostics;
using System.Text.Json.Nodes;

namespace MySsh.Infrastructure;

internal static class SettingsConcurrency
{
    // Held only while loading/merging/publishing JSON, never for an SSH session.
    // Keep the lock file in place: deleting it would allow locking different inodes.
    internal static FileStream Acquire(string directory, TimeSpan? timeout = null)
    {
        var elapsed = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None
                };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                return new FileStream(Path.Combine(directory, ".settings.lock"), options);
            }
            catch (IOException ex)
            {
                if (elapsed.Elapsed >= limit)
                    throw new IOException("Settings are busy or cannot be locked; no settings were overwritten.", ex);
                Thread.Sleep(20);
            }
        }
    }

    internal static JsonNode Merge(JsonNode baseline, JsonNode local, JsonNode disk, string file)
    {
        if (baseline is not JsonObject || disk is not JsonObject ||
            !JsonNode.DeepEquals(baseline["version"], disk["version"]))
            throw new IOException(file + " changed its format or version; it was not overwritten.");
        return MergeNode(new(true, baseline), new(true, local), new(true, disk), file).Value
            ?? throw new IOException("Settings root cannot be deleted.");
    }

    private static Node MergeNode(Node baseline, Node local, Node disk, string path)
    {
        if (Same(local, baseline)) return Clone(disk);
        if (Same(disk, baseline) || Same(local, disk)) return Clone(local);
        if (local.Value is JsonObject localObject && disk.Value is JsonObject diskObject &&
            (baseline.Value is JsonObject || !baseline.Exists))
        {
            var original = baseline.Value as JsonObject;
            var keys = (original?.Select(x => x.Key) ?? Enumerable.Empty<string>())
                .Concat(localObject.Select(x => x.Key)).Concat(diskObject.Select(x => x.Key))
                .Distinct(StringComparer.Ordinal);
            var merged = new JsonObject();
            foreach (var key in keys)
            {
                var value = MergeNode(Get(original, key), Get(localObject, key), Get(diskObject, key), path + "/" + key);
                if (value.Exists) merged.Add(key, value.Value);
            }
            return new(true, merged);
        }
        // Arrays are atomic values. In particular, do not silently resurrect a
        // removed server/user by taking a naive union of two changed arrays.
        throw new IOException($"Settings conflict at {path}. Another instance changed the same setting. No file was overwritten; reopen the application to load the latest settings before retrying.");
    }

    private static Node Get(JsonObject? obj, string key) =>
        obj is not null && obj.TryGetPropertyValue(key, out var value) ? new(true, value) : new(false, null);
    private static bool Same(Node first, Node second) =>
        first.Exists == second.Exists && JsonNode.DeepEquals(first.Value, second.Value);
    private static Node Clone(Node node) => new(node.Exists, node.Value?.DeepClone());
    private readonly record struct Node(bool Exists, JsonNode? Value);
}
