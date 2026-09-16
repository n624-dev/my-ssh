using System.Collections;
using MySsh.Core;
using MySsh.Infrastructure;
using Terminal.Gui;

namespace MySsh.App;

internal enum RequestedAction { Exit, OpenSsh, FileTransfer }
internal sealed record SelectionResult(Connection Connection, RequestedAction Action);

internal static class SelectionUi
{
    public static SelectionResult? Run(SettingsStore settings)
    {
        while (true)
        {
            var servers = settings.Config.Servers
                .OrderBy(x => x.Host, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var items = servers.Select(x => x.Host).Cast<object>().ToList();
            items.Add("+ Add server");
            items.Add("- Remove server");
            items.Add("Exit");

            var selected = Choose("SSH Server", items);
            if (selected < 0 || selected == items.Count - 1) return null;

            if (selected == servers.Count)
            {
                AddServer(settings);
                continue;
            }

            if (selected == servers.Count + 1)
            {
                RemoveServer(settings, servers);
                continue;
            }

            var server = servers[selected];
            var user = ChooseUser(settings, server);
            if (user is null) continue;

            var connection = new Connection(server.Host, user);
            try
            {
                connection.Validate();
            }
            catch (Exception ex)
            {
                MessageBox.ErrorQuery(60, 7, "SSH Login", ex.Message, "OK");
                continue;
            }

            var action = MessageBox.Query(55, 8, "Action", connection.Key,
                "Open SSH", "File Transfer", "Back");
            if (action == 0) return new(connection, RequestedAction.OpenSsh);
            if (action == 1) return new(connection, RequestedAction.FileTransfer);
        }
    }

    private static void AddServer(SettingsStore settings)
    {
        var value = Prompt("Add Server", "Server", "", []);
        if (value is null) return;
        value = value.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            MessageBox.ErrorQuery(50, 7, "Add Server", "Server name cannot be empty.", "OK");
            return;
        }
        if (settings.Config.Servers.Any(x =>
                x.Host.Equals(value, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.ErrorQuery(50, 7, "Add Server", "Server already exists.", "OK");
            return;
        }

        try
        {
            new Connection(value, "check").Validate();
            settings.Config.Servers.Add(new ServerConfig { Host = value });
            settings.SaveConfig();
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery(60, 7, "Add Server", ex.Message, "OK");
        }
    }

    private static void RemoveServer(SettingsStore settings, IReadOnlyList<ServerConfig> servers)
    {
        if (servers.Count == 0)
        {
            MessageBox.Query(50, 7, "Remove Server", "No servers are configured.", "OK");
            return;
        }

        var remove = Choose("Remove Server", servers.Select(x => (object)x.Host).ToList());
        if (remove < 0) return;
        if (MessageBox.Query(55, 7, "Remove Server",
                $"Remove '{servers[remove].Host}'?", "Remove", "Cancel") != 0)
            return;

        settings.Config.Servers.Remove(servers[remove]);
        settings.SaveConfig();
    }

    private static string? ChooseUser(SettingsStore settings, ServerConfig server)
    {
        var typed = server.Users.Count == 1 ? server.Users[0] : "";
        while (true)
        {
            var result = UserDialog(server, typed);
            typed = result.Value;

            switch (result.Action)
            {
                case UserDialogAction.Back:
                    return null;
                case UserDialogAction.Connect:
                    typed = typed.Trim();
                    if (string.IsNullOrWhiteSpace(typed))
                    {
                        MessageBox.ErrorQuery(50, 7, "SSH Login", "User name cannot be empty.", "OK");
                        continue;
                    }
                    return typed;
                case UserDialogAction.Add:
                    AddUser(settings, server);
                    typed = server.Users.Count == 1 ? server.Users[0] : typed;
                    break;
                case UserDialogAction.Remove:
                    RemoveUser(settings, server);
                    typed = server.Users.Count == 1 ? server.Users[0] : "";
                    break;
            }
        }
    }

    private static void AddUser(SettingsStore settings, ServerConfig server)
    {
        var value = Prompt("Add User", "User", "", server.Users);
        if (value is null) return;
        value = value.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            MessageBox.ErrorQuery(50, 7, "Add User", "User name cannot be empty.", "OK");
            return;
        }
        if (server.Users.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.ErrorQuery(50, 7, "Add User", "User already exists.", "OK");
            return;
        }

        try
        {
            new Connection(server.Host, value).Validate();
            server.Users.Add(value);
            settings.SaveConfig();
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery(60, 7, "Add User", ex.Message, "OK");
        }
    }

    private static void RemoveUser(SettingsStore settings, ServerConfig server)
    {
        var users = server.Users.Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (users.Count == 0)
        {
            MessageBox.Query(55, 7, "Remove User", "No users are configured for this server.", "OK");
            return;
        }

        var remove = Choose("Remove User", users.Cast<object>().ToList());
        if (remove < 0) return;
        if (MessageBox.Query(55, 7, "Remove User",
                $"Remove '{users[remove]}' from '{server.Host}'?", "Remove", "Cancel") != 0)
            return;

        server.Users.RemoveAll(x => x.Equals(users[remove], StringComparison.OrdinalIgnoreCase));
        settings.SaveConfig();
    }

    private static UserDialogResult UserDialog(ServerConfig server, string initial)
    {
        var result = new UserDialogResult(UserDialogAction.Back, initial);
        var connect = new Button("Connect") { IsDefault = true };
        var add = new Button("+ Add user");
        var remove = new Button("- Remove user");
        var back = new Button("Back");
        var dialog = new Dialog("SSH Login", 68, 14, connect, add, remove, back);

        var label = new Label($"User for {server.Host}:")
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = 1
        };
        var field = new CompletingTextField(initial, server.Users)
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
            Height = 1
        };
        var candidatesLabel = new Label("")
        {
            X = 1,
            Y = 4,
            Width = Dim.Fill(1),
            Height = 2
        };
        var hint = new Label("Tab Complete   Enter Connect   Add/Remove are explicit actions")
        {
            X = 1,
            Y = 7,
            Width = Dim.Fill(1),
            Height = 1
        };

        void RefreshCandidates() => candidatesLabel.Text = string.Join(", ",
            Completion.Matching(server.Users, field.Text?.ToString() ?? ""));
        field.Changed = RefreshCandidates;
        RefreshCandidates();

        connect.Clicked += () =>
        {
            result = new(UserDialogAction.Connect, field.Text?.ToString() ?? "");
            Application.RequestStop();
        };
        add.Clicked += () =>
        {
            result = new(UserDialogAction.Add, field.Text?.ToString() ?? "");
            Application.RequestStop();
        };
        remove.Clicked += () =>
        {
            result = new(UserDialogAction.Remove, field.Text?.ToString() ?? "");
            Application.RequestStop();
        };
        back.Clicked += () => Application.RequestStop();

        dialog.Add(label, field, candidatesLabel, hint);
        field.SetFocus();
        Application.Run(dialog);
        return result;
    }

    private static int Choose(string title, IList items)
    {
        var result = -1;
        var list = new ListView(items)
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1)
        };
        var dialog = new Dialog(title, 64, Math.Min(24, Math.Max(8, items.Count + 5)));
        list.OpenSelectedItem += args =>
        {
            result = args.Item;
            Application.RequestStop();
        };
        dialog.Add(list);
        Application.Run(dialog);
        return result;
    }

    private static string? Prompt(
        string title,
        string labelText,
        string initial,
        IEnumerable<string> candidates)
    {
        string? result = null;
        var save = new Button("Save") { IsDefault = true };
        var cancel = new Button("Cancel");
        var dialog = new Dialog(title, 64, 12, save, cancel);
        var label = new Label(labelText + ":")
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = 1
        };
        var field = new CompletingTextField(initial, candidates)
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
            Height = 1
        };
        var candidatesLabel = new Label("")
        {
            X = 1,
            Y = 4,
            Width = Dim.Fill(1),
            Height = 2
        };
        var hint = new Label("Tab Complete   Enter Save")
        {
            X = 1,
            Y = 7,
            Width = Dim.Fill(1),
            Height = 1
        };

        void RefreshCandidates() => candidatesLabel.Text = string.Join(", ",
            Completion.Matching(candidates, field.Text?.ToString() ?? ""));
        field.Changed = RefreshCandidates;
        RefreshCandidates();

        save.Clicked += () =>
        {
            result = field.Text?.ToString() ?? "";
            Application.RequestStop();
        };
        cancel.Clicked += () => Application.RequestStop();
        dialog.Add(label, field, candidatesLabel, hint);
        field.SetFocus();
        Application.Run(dialog);
        return result;
    }

    private enum UserDialogAction { Back, Connect, Add, Remove }
    private sealed record UserDialogResult(UserDialogAction Action, string Value);

    private sealed class CompletingTextField : TextField
    {
        private readonly string[] _candidates;
        public Action? Changed { get; set; }

        public CompletingTextField(string value, IEnumerable<string> candidates) : base(value)
        {
            _candidates = candidates.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public override bool ProcessKey(KeyEvent keyEvent)
        {
            if (keyEvent.Key == Key.Tab)
            {
                var current = Text?.ToString() ?? "";
                Text = Completion.LongestCommonPrefix(_candidates, current);
                Changed?.Invoke();
                return true;
            }

            var handled = base.ProcessKey(keyEvent);
            Changed?.Invoke();
            return handled;
        }
    }
}
