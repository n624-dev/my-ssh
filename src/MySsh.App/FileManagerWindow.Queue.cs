using System.Collections;
using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

namespace MySsh.App;

internal sealed partial class FileManagerWindow
{
    private void QueueActions()
    {
        RefreshQueue();
        if (_queueList.SelectedItem < 0 || _queueList.SelectedItem >= _queueRows.Count) return;
        var job = _queueRows[_queueList.SelectedItem];
        var actions = new List<(string Name, Action Run)>
        {
            ("Pause", () => _transfers.Pause(job.Id)),
            ("Resume", () => _transfers.Resume(job.Id)),
            ("Retry", () => _transfers.Retry(job.Id)),
            ("Overwrite and retry", () => RetryOverwrite(job)),
            ("Skip conflict and retry", () => _transfers.Retry(job.Id, ConflictAction.Skip)),
            ("Rename destination and retry", () => RenameTransferDestination(job)),
            ("Cancel job", () => _transfers.Cancel(job.Id)),
            ("Remove finished job", () => _transfers.Remove(job.Id))
        };
        var selected = Choose("Transfer Actions", actions.Select(x => (object)x.Name).ToList());
        if (selected >= 0) actions[selected].Run();
        RefreshQueue();
    }

    private void RetryOverwrite(TransferJobSnapshot job)
    {
        if (MessageBox.Query(70, 8, "Overwrite",
                "Replace the destination only after the new data has fully transferred?",
                "Overwrite", "Back") == 0) _transfers.Retry(job.Id, ConflictAction.Overwrite);
    }

    private void RenameTransferDestination(TransferJobSnapshot job)
    {
        var oldName = PathLikeName(job.Destination);
        var newName = Prompt("Rename Transfer", "Destination name", oldName);
        if (string.IsNullOrWhiteSpace(newName) || newName == oldName) return;
        var destinationFs = job.DestinationIsRemote ? _remote : _local;
        try
        {
            var newPath = destinationFs.Join(destinationFs.Parent(job.Destination), newName);
            _transfers.Retry(job.Id, ConflictAction.Ask, newPath);
        }
        catch (Exception ex) { ShowOperationError("Rename transfer", ex); }
    }

    private void RefreshQueue()
    {
        // Modal operation loops also run timers. Never start a second directory
        // read from inside an operation's progress/cancellation dialog.
        if (UiFileOperation.IsBusy) return;
        _queueRows = _transfers.Snapshot().ToList();
        var display = _queueRows.Select(x => (object)QueueText(x)).ToList();
        var selected = _queueList.SelectedItem;
        _queueList.SetSource((IList)display);
        if (display.Count > 0) _queueList.SelectedItem = Math.Clamp(selected, 0, display.Count - 1);
        var newlyCompleted = _queueRows
            .Where(x => x.State is TransferState.Completed or TransferState.Partial)
            .Where(x => _reloadedCompleted.Add(x.Id)).Any();
        if (!newlyCompleted) return;
        try { ReloadPane(true); ReloadPane(false); }
        catch (Exception ex) { _message.Text = "Refresh failed: " + Safe(ex.Message); }
    }

    private static string QueueText(TransferJobSnapshot job)
    {
        var progress = job.TotalBytes is > 0 ? $" {job.BytesTransferred * 100.0 / job.TotalBytes:0.0}%" : "";
        var speed = job.BytesPerSecond is > 0 ? $" {FormatSize((long)job.BytesPerSecond.Value)}/s" : "";
        var eta = job.BytesPerSecond is > 0 && job.TotalBytes is { } total && total > job.BytesTransferred
            ? $" ETA {FormatDuration(TimeSpan.FromSeconds((total - job.BytesTransferred) / job.BytesPerSecond.Value))}" : "";
        return $"{job.State,-9} {Safe(PathLikeName(job.Source))} -> " +
               $"{Safe(job.Destination)}{progress}{speed}{eta} {Safe(job.Message)}";
    }
}
