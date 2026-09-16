using System.Collections;
using System.Globalization;
using System.Text;
using MySsh.Core;
using Terminal.Gui;

namespace MySsh.App;

internal sealed partial class FileManagerWindow
{
    private static string? Prompt(string title, string label, string initial)
    {
        string? value = null;
        var save = new Button("Save") { IsDefault = true };
        var cancel = new Button("Cancel");
        var dialog = new Dialog(title, 70, 9, save, cancel);
        var field = new TextField(initial)
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
            Height = 1
        };

        dialog.Add(new Label(label + ":") { X = 1, Y = 1 }, field);
        save.Clicked += () =>
        {
            value = field.Text?.ToString();
            Application.RequestStop();
        };
        cancel.Clicked += () => Application.RequestStop();
        field.SetFocus();
        Application.Run(dialog);
        return value;
    }

    private static int Choose(string title, IList items)
    {
        var result = -1;
        var list = new ListView(items)
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        var dialog = new Dialog(title, 90, Math.Min(24, Math.Max(8, items.Count + 5)));
        list.OpenSelectedItem += args =>
        {
            result = args.Item;
            Application.RequestStop();
        };
        dialog.Add(list);
        Application.Run(dialog);
        return result;
    }

    internal static void ShowHelp()
    {
        var ok = new Button("OK") { IsDefault = true };
        using var dialog = new Dialog("Help",
            Math.Min(82, Math.Max(1, Application.Driver.Cols - 2)),
            Math.Min(21, Math.Max(1, Application.Driver.Rows - 2)), ok);
        var text = new TextView
        {
            X = 1, Y = 0, Width = Dim.Fill(1), Height = Dim.Fill(2),
            ReadOnly = true, WordWrap = true,
            Text =
        "Tab: switch panes / transfer list\n" +
        "Space: mark multiple entries\n" +
        "Enter: open directory or preview file\n" +
        "F2: rename\n" +
        "F5: copy to the opposite pane\n" +
        "F6: move to the opposite pane\n" +
        "F7: create directory\n" +
        "F9: file, view, permission, reconnect and transfer actions\n" +
        "Delete: permanent delete\n" +
        "Esc: return to action selection\n\n" +
        "Transfers use a dedicated SFTP session, verified partial resume and " +
        "same-directory temporary files before commit."
        };
        dialog.Add(text);
        ok.Clicked += () => Application.RequestStop();
        ok.SetFocus();
        Application.Run(dialog);
    }

    private static string Properties(FileEntry entry) =>
        $"Name: {Safe(entry.Name)}\n" +
        $"Type: {entry.Kind}\n" +
        $"Size: {FormatSize(entry.Length)}\n" +
        $"Modified: {entry.Modified.LocalDateTime:G}\n" +
        $"Mode: {(entry.Mode.HasValue ? Convert.ToString(entry.Mode.Value & 0x1FF, 8).PadLeft(3, '0') : "n/a")}\n" +
        $"Link: {Safe(entry.LinkTarget ?? "")}";

    private static string FormatSize(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double number = value;
        var unit = 0;
        while (number >= 1024 && unit < units.Length - 1)
        {
            number /= 1024;
            unit++;
        }
        return $"{number:0.##} {units[unit]}";
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1)
            return $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}";
        return $"{value.Minutes}:{value.Seconds:00}";
    }

    private static string Safe(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value)
        {
            var category = char.GetUnicodeCategory(c);
            if (char.IsControl(c) || category == UnicodeCategory.Format)
                builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            else
                builder.Append(c);
        }
        return builder.ToString();
    }

    private static string PathLikeName(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var index = normalized.LastIndexOf('/');
        return index < 0 ? normalized : normalized[(index + 1)..];
    }

    private static string SanitizeTempName(string name) => string.Concat(name.Select(c =>
        Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private sealed class BrowserRow
    {
        public static readonly BrowserRow Parent = new();
        public FileEntry? Entry { get; }
        public bool IsParent => Entry is null;

        private BrowserRow() { }
        public BrowserRow(FileEntry entry) => Entry = entry;

        public override string ToString()
        {
            if (IsParent) return "../";
            var suffix = Entry!.Kind switch
            {
                EntryKind.Directory => "/",
                EntryKind.SymbolicLink => " -> " + Safe(Entry.LinkTarget ?? "?"),
                _ => ""
            };
            var size = Entry.Kind == EntryKind.File ? FormatSize(Entry.Length) : "";
            return $"{Safe(Entry.Name)}{suffix,-24} {size,10} " +
                   $"{Entry.Modified.LocalDateTime:yyyy-MM-dd HH:mm}";
        }
    }
}
