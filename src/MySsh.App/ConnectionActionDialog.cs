using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

namespace MySsh.App;

internal enum ConnectionAction { FileTransfer, OpenSsh, Leave, Authenticate }

internal sealed class ConnectionActionDialog : Dialog
{
    private readonly TransferQueue _transfers;
    private readonly Func<bool> _confirmLeave;
    private readonly Label _status = new("");
    private bool _decided;
    internal ConnectionAction Result { get; private set; } = ConnectionAction.Leave;

    internal ConnectionActionDialog(Connection connection, TransferQueue transfers, Func<bool>? confirmLeave = null)
        : base("Action", Math.Min(82, Math.Max(1, Application.Driver.Cols - 2)),
            Math.Min(15, Math.Max(1, Application.Driver.Rows - 2)))
    {
        _transfers = transfers;
        _confirmLeave = confirmLeave ?? (() => MessageBox.Query(75, 10, "Leave connection",
            "Transfers are still running or queued. Pause unfinished work and return to server selection?\n" +
            "Completed changes are not rolled back; jobs will remain available for explicit resume.",
            "Stay", "Pause transfers and leave") == 1);
        Add(new Label(connection.Key) { X = 1, Y = 1, Width = Dim.Fill(1) });
        var list = new ListView(new List<object> { "File Transfer", "Open SSH", "Back to servers" })
        {
            X = 1, Y = 3, Width = Dim.Fill(1), Height = 3
        };
        list.OpenSelectedItem += args => TryChoose((ConnectionAction)args.Item);
        _status.X = 1;
        _status.Y = 7;
        _status.Width = Dim.Fill(1);
        _status.Height = 2;
        Add(list, _status);
        list.SetFocus();
        UpdateStatus();
        Closing += args =>
        {
            if (_decided) return;
            if (!MayLeave()) { args.Cancel = true; return; }
            Result = ConnectionAction.Leave;
            _decided = true;
        };
    }

    internal bool TryChoose(ConnectionAction action)
    {
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        if (action == ConnectionAction.Leave && !MayLeave()) return false;
        Result = action;
        _decided = true;
        Application.RequestStop(this);
        return true;
    }

    private bool MayLeave() => !_transfers.Snapshot().Any(job => job.State is TransferState.Running or TransferState.Queued) || _confirmLeave();

    internal void UpdateStatus()
    {
        var jobs = _transfers.Snapshot();
        _status.Text = $"Transfers: {jobs.Count(job => job.State == TransferState.Running)} running, " +
            $"{jobs.Count(job => job.State == TransferState.Queued)} queued, " +
            $"{jobs.Count(job => job.State == TransferState.Paused)} paused.\n" +
            "The queue stays alive while choosing an action or using SSH.";
    }
}
