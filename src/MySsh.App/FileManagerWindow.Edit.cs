using System.Text;
using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

namespace MySsh.App;

internal sealed partial class FileManagerWindow
{
    private void Preview(FileEntry entry, IFileSystem fs)
    {
        if (entry.Kind != EntryKind.File)
        {
            MessageBox.Query(65, 9, "Properties", Properties(entry), "OK");
            return;
        }
        if (entry.Length > 1024 * 1024)
        {
            MessageBox.Query(65, 9, "Preview", "Text preview is limited to 1 MiB.\n" + Properties(entry), "OK");
            return;
        }
        var bytes = UiFileOperation.Run("Read preview", async ct =>
        {
            await using var stream = await fs.OpenReadAsync(entry.Path, ct).ConfigureAwait(false);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, ct).ConfigureAwait(false);
            return memory.ToArray();
        });
        if (bytes.Take(Math.Min(bytes.Length, 4096)).Any(x => x == 0))
        {
            MessageBox.Query(65, 9, "Preview", "Binary file\n" + Properties(entry), "OK");
            return;
        }
        var text = new UTF8Encoding(false, false).GetString(bytes);
        using var dialog = CreatePreviewDialog(entry.Name, text);
        Application.Run(dialog);
    }

    internal static Dialog CreatePreviewDialog(string name, string text)
    {
        var width = Math.Min(100, Math.Max(1, Application.Driver.Cols - 4));
        var maximumHeight = Math.Max(1, Application.Driver.Rows - 4);
        var markdown = Path.GetExtension(name).Equals(".md", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(name).Equals(".markdown", StringComparison.OrdinalIgnoreCase);
        var view = new PreviewTextView
        {
            Frame = new Rect(0, 0, Math.Max(1, width - 2), 1),
            ReadOnly = true, WordWrap = markdown, Text = text
        };
        var height = Math.Min(maximumHeight, Math.Max(5, view.Lines + 4));
        var close = new Button("Close") { IsDefault = true };
        var dialog = new Dialog(Safe(name), width, height, close);
        view.Width = Dim.Fill();
        view.Height = Dim.Fill(2);
        close.Clicked += () => Application.RequestStop();
        dialog.Add(view);
        return dialog;
    }

    private sealed class PreviewTextView : TextView
    {
        public override void Redraw(Rect bounds)
        {
            TopRow = Math.Clamp(TopRow, 0, Math.Max(0, Lines - Frame.Height));
            base.Redraw(bounds);
        }
    }

    private void EditSelected()
    {
        var selected = SelectedRows(_activeLocal);
        if (selected.Count != 1 || selected[0].Kind != EntryKind.File)
        {
            MessageBox.Query(60, 7, "Edit", "Select exactly one regular file.", "OK");
            return;
        }
        var entry = selected[0];
        var external = !string.IsNullOrWhiteSpace(_settings.Config.Editor);
        if (!external && entry.Length > 4 * 1024 * 1024)
        {
            MessageBox.Query(75, 8, "Edit", "Built-in editing is limited to 4 MiB. Configure an external editor for larger files.", "OK");
            return;
        }
        EditSession? session = null;
        try
        {
            var fs = _activeLocal ? _local : _remote;
            var location = _activeLocal ? "LOCAL" : _connection.Key;
            session = UiFileOperation.Run("Prepare edit draft", ct => EditSession.OpenAsync(fs, entry.Path,
                Path.Combine(_settings.DirectoryPath, "edit-drafts"), location, ct));
            if (external) RequestExternalEdit(session, fs);
            else BuiltInEdit(session, fs);
            if (session.Saved) ReloadPane(_activeLocal);
        }
        catch (Exception ex)
        {
            var recovery = session is null ? "" : "\nDraft retained at: " + Safe(session.DraftPath);
            MessageBox.ErrorQuery(75, 12, "Edit", Safe(ex.Message) + recovery, "OK");
        }
    }

    private void BuiltInEdit(EditSession session, IFileSystem fs)
    {
        var text = session.ReadText();
        while (!session.Saved)
        {
            var action = EditAction.Keep;
            var save = new Button("Save") { IsDefault = true };
            var saveAs = new Button("Save as");
            var keep = new Button("Keep draft");
            var discard = new Button("Discard");
            using var dialog = new Dialog("Edit: " + Safe(session.Original.Name),
                Math.Min(95, Math.Max(1, Application.Driver.Cols - 2)),
                Math.Min(30, Math.Max(1, Application.Driver.Rows - 2)), save, saveAs, keep, discard);
            var editor = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2), Text = text };
            save.Clicked += () => { action = EditAction.Save; Application.RequestStop(); };
            saveAs.Clicked += () => { action = EditAction.SaveAs; Application.RequestStop(); };
            keep.Clicked += () => Application.RequestStop();
            discard.Clicked += () => { action = EditAction.Discard; Application.RequestStop(); };
            dialog.Add(editor);
            editor.SetFocus();
            Application.Run(dialog);
            text = editor.Text?.ToString() ?? "";
            if (action == EditAction.Discard)
            {
                if (ConfirmDiscard(session)) return;
                continue;
            }
            try { session.WriteText(text); }
            catch (Exception ex)
            {
                MessageBox.ErrorQuery(75, 10, "Draft not saved", Safe(ex.Message) + "\nThe editor will reopen with your text.", "OK");
                continue;
            }
            if (action == EditAction.Keep) { ShowDraftLocation(session); return; }
            try
            {
                var destination = action == EditAction.SaveAs ? EditDestination(session, fs) : session.Original.Path;
                if (destination is null) continue;
                UiFileOperation.Run("Save edited file", ct => session.SaveAsync(destination, ct));
            }
            catch (Exception ex)
            {
                MessageBox.ErrorQuery(75, 12, "Save failed", Safe(ex.Message) + "\nDraft: " + Safe(session.DraftPath), "OK");
            }
        }
    }

    private static string? EditDestination(EditSession session, IFileSystem fs)
    {
        var name = Prompt("Save as", "New filename (existing files are not overwritten)", session.Original.Name);
        if (name is null || name == session.Original.Name) return null;
        return fs.Join(fs.Parent(session.Original.Path), name);
    }

    private static bool ConfirmDiscard(EditSession session)
    {
        if (MessageBox.Query(70, 8, "Discard draft", "Permanently discard this edited draft?", "Keep", "Discard") != 1) return false;
        session.Discard();
        return true;
    }

    private static void ShowDraftLocation(EditSession session) =>
        MessageBox.Query(75, 10, "Draft retained", "Not uploaded. Recovery file:\n" + Safe(session.DraftPath), "OK");

    private enum EditAction { Save, SaveAs, Keep, Discard }
}
