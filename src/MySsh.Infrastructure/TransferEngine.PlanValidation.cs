using MySsh.Core;

namespace MySsh.Infrastructure;

public sealed partial class TransferEngine
{
    // Validate the whole recursive plan BEFORE creating any directory or partial
    // file. Overwrite/Skip cannot resolve two source entries that map to one target:
    // a later move would otherwise delete both originals but retain only one value.
    internal static void ValidateDestinationNames(
        IEnumerable<(string Source, string Destination)> plan, StringComparison comparison)
    {
        var names = new Dictionary<string, string>(StringComparer.FromComparison(comparison));
        foreach (var item in plan)
        {
            if (names.TryGetValue(item.Destination, out var previous))
                throw new IOException($"Destination name collision: '{previous}' and '{item.Source}' both map to '{item.Destination}'. Rename or deselect one source entry before retrying. No transfer has started.");
            names.Add(item.Destination, item.Source);
        }
    }
}
