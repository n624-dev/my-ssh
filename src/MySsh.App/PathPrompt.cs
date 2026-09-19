using System.Globalization;
using MySsh.Core;
using Terminal.Gui;

namespace MySsh.App;

internal static class PathPrompt
{
    internal static string? Show(IFileSystem fileSystem, string currentDirectory)
    {
        string? result = null;
        var ok = new Button("Open") { IsDefault = true };
        var cancel = new Button("Cancel");
        using var dialog = new Dialog("Go to Path", Math.Min(90, Math.Max(1, Application.Driver.Cols - 2)),
            Math.Min(13, Math.Max(1, Application.Driver.Rows - 2)), ok, cancel);
        var candidates = new Label("Tab: complete path / Shift+Tab: move focus")
        {
            X = 1, Y = 3, Width = Dim.Fill(1), Height = Dim.Fill(2)
        };
        var field = new PathCompletingTextField(currentDirectory, text =>
            UiFileOperation.Run("Complete path", ct => PathCompletion.CompleteAsync(fileSystem, currentDirectory, text, ct)),
            message => candidates.Text = message)
        {
            X = 1, Y = 1, Width = Dim.Fill(1)
        };
        ok.Clicked += () => { result = field.Text.ToString(); Application.RequestStop(); };
        cancel.Clicked += () => Application.RequestStop();
        dialog.Add(field, candidates);
        field.SetFocus();
        Application.Run(dialog);
        return result;
    }
}

internal sealed class PathCompletingTextField(string initial,
    Func<string, PathCompletionResult> complete, Action<string> report) : TextField(initial)
{
    public override bool ProcessKey(KeyEvent keyEvent)
    {
        if (keyEvent.Key != Key.Tab) return base.ProcessKey(keyEvent);
        try
        {
            var result = complete(Text?.ToString() ?? "");
            Text = result.Text;
            CursorPosition = Text.RuneCount;
            var shown = result.Candidates.Take(12).Select(Escape);
            report(result.Candidates.Length == 0 ? "No matching paths." :
                string.Join("  ", shown) + (result.Candidates.Length > 12 ? "  ..." : ""));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or ArgumentException)
        {
            report("Completion failed: " + Escape(ex.Message));
        }
        return true;
    }

    private static string Escape(string value) => string.Concat(value.Select(c =>
        char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format
            ? "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture) : c.ToString()));
}
