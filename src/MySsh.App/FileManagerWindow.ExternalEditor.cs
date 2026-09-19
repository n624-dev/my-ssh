using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

namespace MySsh.App;

internal sealed partial class FileManagerWindow
{
    internal ExternalEditorRequest? RequestedEditor { get; private set; }

    internal void RequestExternalEdit(EditSession session, IFileSystem fileSystem)
    {
        RequestedEditor = new(session, fileSystem, _settings.Config.Editor,
            _settings.Config.EditorArguments.ToArray());
        // Return from the event handler and unwind Application.Run first.
        // FileManagerSession owns shutdown, process execution and recreation.
        Application.RequestStop();
    }

    internal void CompleteExternalEdit(ExternalEditorRequest request, string? error)
    {
        var session = request.Session;
        var fs = request.FileSystem;
        if (error is not null)
            MessageBox.ErrorQuery(75, 10, "Editor", Safe(error) + "\nYour draft has been retained.", "OK");
        while (!session.Saved)
        {
            var action = Choose("Edited file", new List<object>
            {
                "Save to original", "Save as", "Re-edit", "Keep draft", "Discard draft"
            });
            if (action < 0 || action == 3) { ShowDraftLocation(session); return; }
            try
            {
                if (action == 4) { if (ConfirmDiscard(session)) return; continue; }
                if (action == 2) { RequestExternalEdit(session, fs); return; }
                var destination = action == 1 ? EditDestination(session, fs) : session.Original.Path;
                if (destination is null) continue;
                UiFileOperation.Run("Save edited file", ct => session.SaveAsync(destination, ct));
            }
            catch (Exception ex)
            {
                MessageBox.ErrorQuery(75, 12, "Save failed", Safe(ex.Message) + "\nDraft: " + Safe(session.DraftPath), "OK");
            }
        }
        ReloadPane(ReferenceEquals(fs, _local));
    }
}
