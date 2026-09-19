using MySsh.Core;
using Terminal.Gui;

namespace MySsh.App;

internal sealed class FileManagerKeyMap
{
    internal sealed record Binding(string Id, Key Key, string Label, string Description)
    {
        public string Display => Format(Key);
    }
    private static readonly (string Id, string Label, string Description)[] Definitions =
    [
        ("help", "Help", "Help"), ("rename", "Rename", "Rename"),
        ("copy", "Copy", "Copy to the opposite pane"), ("move", "Move", "Move to the opposite pane"),
        ("mkdir", "Mkdir", "Create directory"), ("actions", "Actions", "File and transfer actions"),
        ("delete", "Delete", "Permanent delete (confirmation required)")
    ];
    private readonly Dictionary<string, Binding> _bindings;
    private FileManagerKeyMap(Dictionary<string, Binding> bindings) => _bindings = bindings;
    internal Binding this[string action] => _bindings[action];
    internal static FileManagerKeyMap Default => Create(new AppConfig().Keys);

    internal static FileManagerKeyMap Create(IReadOnlyDictionary<string, string> configured)
    {
        var defaults = new AppConfig().Keys;
        if (configured.Keys.Any(action => !defaults.ContainsKey(action)))
            throw new IOException("Unknown action in key bindings. Supported actions: " + string.Join(", ", defaults.Keys));
        var bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        var occupied = new Dictionary<Key, string>();
        foreach (var definition in Definitions)
        {
            var value = configured.TryGetValue(definition.Id, out var setting) ? setting : defaults[definition.Id];
            var key = Parse(value, definition.Id);
            if (!occupied.TryAdd(key, definition.Id))
                throw new IOException($"Key binding conflict: {definition.Id} and {occupied[key]} both use {Format(key)}.");
            bindings.Add(definition.Id, new(definition.Id, key, definition.Label, definition.Description));
        }
        return new(bindings);
    }

    private static Key Parse(string? text, string action)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl))
            throw new IOException("Empty or invalid key binding for " + action + ".");
        var modifiers = Key.Null;
        Key? baseKey = null;
        foreach (var part in text.Replace('+', ',').Replace('|', ',').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" or "CTRLMASK" => Key.CtrlMask,
                "ALT" or "ALTMASK" => Key.AltMask,
                "SHIFT" or "SHIFTMASK" => Key.ShiftMask,
                _ => Key.Null
            };
            if (modifier != Key.Null) { modifiers |= modifier; continue; }
            if (baseKey.HasValue || !char.IsLetter(part[0]) || !Enum.TryParse<Key>(part, true, out var parsed) || !Enum.IsDefined(parsed))
                throw new IOException("Invalid key binding for " + action + ". Use F1-F12, DeleteChar, or a modified named key such as Ctrl+X.");
            baseKey = parsed;
        }
        if (!baseKey.HasValue) throw new IOException("A key binding needs a key, not just modifiers: " + action);
        var basis = baseKey.Value;
        var function = basis >= Key.F1 && basis <= Key.F12;
        var character = basis >= Key.A && basis <= Key.Z || basis >= Key.D0 && basis <= Key.D9;
        if (!(function || basis == Key.DeleteChar || basis == Key.InsertChar || character && modifiers != Key.Null))
            throw new IOException("Unsupported or reserved navigation key for " + action + ".");
        var result = basis | modifiers;
        if (result == (Key.A | Key.AltMask) || result == (Key.Q | Key.CtrlMask))
            throw new IOException("Alt+A and Ctrl+Q are reserved for Actions and application navigation.");
        return result;
    }

    internal static string Format(Key key)
    {
        var parts = new List<string>();
        if ((key & Key.CtrlMask) != 0) parts.Add("Ctrl");
        if ((key & Key.AltMask) != 0) parts.Add("Alt");
        if ((key & Key.ShiftMask) != 0) parts.Add("Shift");
        var basis = key & ~(Key.CtrlMask | Key.AltMask | Key.ShiftMask);
        var name = basis switch { Key.DeleteChar => "Del", Key.InsertChar => "Insert", _ => basis.ToString() };
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name[1..];
        parts.Add(name);
        return string.Join("+", parts);
    }

    internal StatusItem Status(string action, Action execute)
    {
        var binding = this[action];
        var suffix = action == "actions" ? " (Alt+A)" : "";
        return new(binding.Key, $"~{binding.Display}~ {binding.Label}{suffix}", execute);
    }

    internal string HelpText =>
        "Tab: switch panes / transfer list\nSpace: mark multiple entries\nEnter: open directory or preview file\n" +
        string.Join("\n", Definitions.Select(definition => $"{this[definition.Id].Display}: {this[definition.Id].Description}")) +
        "\nAlt+A: open Actions without function keys\nEsc: return to action selection\n\n" +
        "Transfers use a dedicated SFTP session, verified partial resume and same-directory temporary files before commit.";
}
