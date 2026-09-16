using System.Collections;
using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

namespace MySsh.App;

internal sealed partial class FileManagerWindow : Window
{
    private readonly Connection _connection;
    private readonly IFileSystem _local;
    private readonly IFileSystem _remote;
    private readonly BrowserState _state;
    private readonly SettingsStore _settings;
    private readonly TransferQueue _transfers;

    private readonly Label _localPath = new("");
    private readonly Label _remotePath = new("");
    private readonly ListView _localList = new();
    private readonly ListView _remoteList = new();
    private readonly ListView _queueList = new();
    private readonly Label _message = new("");

    private List<BrowserRow> _localRows = [];
    private List<BrowserRow> _remoteRows = [];
    private List<TransferJobSnapshot> _queueRows = [];
    private readonly HashSet<Guid> _reloadedCompleted = [];
    private string _currentLocal;
    private string _currentRemote;
    private string _localFilter = "";
    private string _remoteFilter = "";
    private SortMode _sortMode = SortMode.Name;
    private bool _sortDescending;
    private bool _activeLocal = true;

    public FileManagerWindow(
        Connection connection,
        IFileSystem local,
        IFileSystem remote,
        BrowserState state,
        SettingsStore settings,
        TransferQueue transfers)
        : base($"File Transfer: {connection.Key}")
    {
        _connection = connection;
        _local = local;
        _remote = remote;
        _state = state;
        _settings = settings;
        _transfers = transfers;
        _currentLocal = string.IsNullOrWhiteSpace(state.LocalPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : state.LocalPath;
        _currentRemote = string.IsNullOrWhiteSpace(state.RemotePath) ? "." : state.RemotePath;

        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();

        ConfigureLayout();
        ConfigureEvents();
        ReloadAll();

        Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(300), _ =>
        {
            RefreshQueue();
            return true;
        });
    }

    private void ConfigureLayout()
    {
        var localFrame = new FrameView("LOCAL")
        {
            X = 0,
            Y = 0,
            Width = Dim.Percent(50),
            Height = Dim.Fill(9)
        };
        var remoteFrame = new FrameView("REMOTE")
        {
            X = Pos.Right(localFrame),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(9)
        };

        _localPath.X = 1;
        _localPath.Y = 0;
        _localPath.Width = Dim.Fill(1);
        _localPath.Height = 1;
        _remotePath.X = 1;
        _remotePath.Y = 0;
        _remotePath.Width = Dim.Fill(1);
        _remotePath.Height = 1;

        ConfigureList(_localList);
        ConfigureList(_remoteList);
        _localList.X = 0;
        _localList.Y = 1;
        _localList.Width = Dim.Fill();
        _localList.Height = Dim.Fill();
        _remoteList.X = 0;
        _remoteList.Y = 1;
        _remoteList.Width = Dim.Fill();
        _remoteList.Height = Dim.Fill();

        localFrame.Add(_localPath, _localList);
        remoteFrame.Add(_remotePath, _remoteList);

        var queueFrame = new FrameView("Transfers")
        {
            X = 0,
            Y = Pos.Bottom(localFrame),
            Width = Dim.Fill(),
            Height = 6
        };
        _queueList.X = 0;
        _queueList.Y = 0;
        _queueList.Width = Dim.Fill();
        _queueList.Height = Dim.Fill();
        queueFrame.Add(_queueList);

        _message.X = 0;
        _message.Y = Pos.Bottom(queueFrame);
        _message.Width = Dim.Fill();
        _message.Height = 1;

        var status = new StatusBar([
            new StatusItem(KeyFor("help", Key.F1), "~F1~ Help", ShowHelp),
            new StatusItem(KeyFor("rename", Key.F2), "~F2~ Rename", RenameSelected),
            new StatusItem(KeyFor("copy", Key.F5), "~F5~ Copy", () => QueueSelected(move: false)),
            new StatusItem(KeyFor("move", Key.F6), "~F6~ Move", () => QueueSelected(move: true)),
            new StatusItem(KeyFor("mkdir", Key.F7), "~F7~ Mkdir", CreateDirectory),
            new StatusItem(KeyFor("actions", Key.F9), "~F9~ Actions", ShowActions),
            new StatusItem(KeyFor("delete", Key.DeleteChar), "~Del~ Delete", DeleteSelected),
            new StatusItem(Key.Esc, "~Esc~ Back", () => Application.RequestStop())
        ]);

        Add(localFrame, remoteFrame, queueFrame, _message, status);
    }

    private Key KeyFor(string action, Key fallback)
    {
        if (_settings.Config.Keys.TryGetValue(action, out var configured) &&
            Enum.TryParse<Key>(configured, ignoreCase: true, out var parsed))
            return parsed;
        return fallback;
    }

    private static void ConfigureList(ListView list)
    {
        list.AllowsMarking = true;
        list.AllowsMultipleSelection = true;
    }

    private void ConfigureEvents()
    {
        _localList.Enter += _ => _activeLocal = true;
        _remoteList.Enter += _ => _activeLocal = false;
        _localList.OpenSelectedItem += _ => OpenSelected(localSide: true);
        _remoteList.OpenSelectedItem += _ => OpenSelected(localSide: false);
        _queueList.OpenSelectedItem += _ => QueueActions();
    }

    private void ReloadAll()
    {
        try
        {
            _currentLocal = _local.CanonicalAsync(_currentLocal, CancellationToken.None).GetAwaiter().GetResult();
            _currentRemote = _remote.CanonicalAsync(_currentRemote, CancellationToken.None).GetAwaiter().GetResult();
            ReloadPane(localSide: true);
            ReloadPane(localSide: false);
            SaveBrowserState();
        }
        catch (Exception ex)
        {
            _message.Text = ex.Message;
        }
    }

    private void ReloadPane(bool localSide)
    {
        var fs = localSide ? _local : _remote;
        var path = localSide ? _currentLocal : _currentRemote;
        var filter = localSide ? _localFilter : _remoteFilter;
        var list = localSide ? _localList : _remoteList;

        var entries = fs.ListAsync(path, CancellationToken.None).GetAwaiter().GetResult()
            .Where(x => _state.ShowHidden || !x.Name.StartsWith(".", StringComparison.Ordinal))
            .Where(x => string.IsNullOrWhiteSpace(filter) ||
                        x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var rows = new List<BrowserRow> { BrowserRow.Parent };
        rows.AddRange(SortEntries(entries).Select(x => new BrowserRow(x)));
        list.SetSource((IList)rows);

        var label = Safe(path);
        if (!string.IsNullOrWhiteSpace(filter)) label += $"  [filter: {Safe(filter)}]";
        label += $"  [sort: {_sortMode}{(_sortDescending ? " desc" : "")}]";

        if (localSide)
        {
            _localRows = rows;
            _localPath.Text = label;
        }
        else
        {
            _remoteRows = rows;
            _remotePath.Text = label;
        }
    }

    private IEnumerable<FileEntry> SortEntries(IEnumerable<FileEntry> entries)
    {
        var materialized = entries.ToArray();
        var directories = SortGroup(materialized.Where(x => x.Kind == EntryKind.Directory));
        var others = SortGroup(materialized.Where(x => x.Kind != EntryKind.Directory));
        return directories.Concat(others);
    }

    private IEnumerable<FileEntry> SortGroup(IEnumerable<FileEntry> entries) => _sortMode switch
    {
        SortMode.Size => _sortDescending
            ? entries.OrderByDescending(x => x.Length).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            : entries.OrderBy(x => x.Length).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
        SortMode.Modified => _sortDescending
            ? entries.OrderByDescending(x => x.Modified).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            : entries.OrderBy(x => x.Modified).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
        _ => _sortDescending
            ? entries.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase)
            : entries.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
    };

    private void SaveBrowserState()
    {
        _state.LocalPath = _currentLocal;
        _state.RemotePath = _currentRemote;
        _settings.SaveState();
    }

    private void SetCurrent(bool localSide, string value)
    {
        if (localSide) _currentLocal = value;
        else _currentRemote = value;
    }

    private enum SortMode { Name, Size, Modified }
}
