using System.Diagnostics;
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
            MessageBox.Query(65, 9, "Preview",
                "Text preview is limited to 1 MiB.\n" + Properties(entry), "OK");
            return;
        }

        using var stream = fs.OpenReadAsync(entry.Path, CancellationToken.None).GetAwaiter().GetResult();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();

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
            // Establish the actual content width before measuring wrapped lines.
            Frame = new Rect(0, 0, Math.Max(1, width - 2), 1),
            ReadOnly = true,
            WordWrap = markdown,
            Text = text
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
            // TextView permits scrolling the final line to the top, leaving an
            // almost empty viewport. Keep the last page filled when browsing.
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
        try
        {
            if (!string.IsNullOrWhiteSpace(_settings.Config.Editor))
            {
                ExternalEdit(entry);
                return;
            }

            if (entry.Length > 4 * 1024 * 1024)
            {
                MessageBox.Query(75, 8, "Edit",
                    "Built-in text editing is limited to 4 MiB. Configure an external editor for larger files.",
                    "OK");
                return;
            }

            BuiltInEdit(entry);
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery(75, 9, "Edit", ex.Message, "OK");
        }
    }

    private void BuiltInEdit(FileEntry entry)
    {
        var fs = _activeLocal ? _local : _remote;
        using var source = fs.OpenReadAsync(entry.Path, CancellationToken.None).GetAwaiter().GetResult();
        using var reader = new StreamReader(
            source,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true);
        var original = reader.ReadToEnd();
        var before = fs.StatAsync(entry.Path, CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new FileNotFoundException(entry.Path);

        var saved = false;
        var save = new Button("Save") { IsDefault = true };
        var cancel = new Button("Cancel");
        var dialog = new Dialog("Edit: " + Safe(entry.Name), 95, 30, save, cancel);
        var editor = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Text = original
        };
        save.Clicked += () => { saved = true; Application.RequestStop(); };
        cancel.Clicked += () => Application.RequestStop();
        dialog.Add(editor);
        Application.Run(dialog);
        if (!saved) return;

        EnsureUnchanged(fs, entry.Path, before);
        var temporary = CreateTemporaryEditPath(entry.Name);
        try
        {
            File.WriteAllText(temporary, editor.Text?.ToString() ?? "", new UTF8Encoding(false));
            using var localTemp = new LocalFileSystem();
            new TransferEngine().CopyAsync(
                    localTemp,
                    temporary,
                    fs,
                    entry.Path,
                    new TransferOptions(PreserveMetadata: false, Conflict: ConflictAction.Overwrite),
                    null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }

        ReloadPane(_activeLocal);
    }

    private void ExternalEdit(FileEntry entry)
    {
        var fs = _activeLocal ? _local : _remote;
        var before = fs.StatAsync(entry.Path, CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new FileNotFoundException(entry.Path);
        var temporary = CreateTemporaryEditPath(entry.Name);

        using var localTemp = new LocalFileSystem();
        new TransferEngine().CopyAsync(
                fs,
                entry.Path,
                localTemp,
                temporary,
                new TransferOptions(PreserveMetadata: false),
                null,
                CancellationToken.None)
            .GetAwaiter().GetResult();

        try
        {
            var info = new ProcessStartInfo(_settings.Config.Editor) { UseShellExecute = false };
            foreach (var argument in _settings.Config.EditorArguments)
                info.ArgumentList.Add(argument);
            info.ArgumentList.Add(temporary);

            using var process = Process.Start(info)
                ?? throw new IOException("Could not start the configured editor.");
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new IOException($"Editor exited with code {process.ExitCode}.");

            EnsureUnchanged(fs, entry.Path, before);
            new TransferEngine().CopyAsync(
                    localTemp,
                    temporary,
                    fs,
                    entry.Path,
                    new TransferOptions(PreserveMetadata: false, Conflict: ConflictAction.Overwrite),
                    null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }

        ReloadPane(_activeLocal);
    }

    private static void EnsureUnchanged(IFileSystem fs, string path, FileEntry before)
    {
        var after = fs.StatAsync(path, CancellationToken.None).GetAwaiter().GetResult();
        if (after is null || after.Length != before.Length || after.Modified != before.Modified)
            throw new IOException(
                "The file changed while it was being edited. The edited data was not uploaded.");
    }

    private static string CreateTemporaryEditPath(string name)
    {
        var directory = Path.Combine(Path.GetTempPath(), "my-ssh-edit");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Guid.NewGuid().ToString("N") + "-" + SanitizeTempName(name));
    }
}
