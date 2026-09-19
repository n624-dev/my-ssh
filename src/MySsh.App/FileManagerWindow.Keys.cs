using Terminal.Gui;

namespace MySsh.App;

internal sealed partial class FileManagerWindow
{
    private FileManagerKeyMap? _keyMap;
    private FileManagerKeyMap KeyMap => _keyMap ??= FileManagerKeyMap.Create(_settings.Config.Keys);

    private StatusBar CreateStatusBar() => new([
        KeyMap.Status("help", () => ShowHelp(KeyMap)),
        KeyMap.Status("rename", RenameSelected),
        KeyMap.Status("copy", () => QueueSelected(false)),
        KeyMap.Status("move", () => QueueSelected(true)),
        KeyMap.Status("mkdir", CreateDirectory),
        KeyMap.Status("actions", ShowActions),
        KeyMap.Status("delete", DeleteSelected),
        new StatusItem(Key.Esc, "~Esc~ Back", () => Application.RequestStop())
    ]);

    private static void ShowHelp(FileManagerKeyMap bindings)
    {
        var ok = new Button("OK") { IsDefault = true };
        using var dialog = new Dialog("Help",
            Math.Min(82, Math.Max(1, Application.Driver.Cols - 2)),
            Math.Min(23, Math.Max(1, Application.Driver.Rows - 2)), ok);
        var text = new TextView
        {
            X = 1, Y = 0, Width = Dim.Fill(1), Height = Dim.Fill(2),
            ReadOnly = true, WordWrap = true, Text = bindings.HelpText
        };
        dialog.Add(text);
        ok.Clicked += () => Application.RequestStop();
        ok.SetFocus();
        Application.Run(dialog);
    }
}
