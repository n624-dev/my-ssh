using MySsh.App;
using MySsh.Core;
using Terminal.Gui;

internal sealed class Issue27Tests : IRegressionCase
{
    public string Name => "#27 typing after Tab appends to the completed username, including Unicode";

    public Task RunAsync()
    {
        Application.Init(new FakeDriver());
        try
        {
            Check("u", ["usera", "userb"], "user", "usera");
            Check("\U0001F680", ["\U0001F680usera", "\U0001F680userb"], "\U0001F680user", "\U0001F680usera");
            Check("x", ["usera", "userb"], "x", "xa");
        }
        finally { Application.Shutdown(); }
        return Task.CompletedTask;
    }

    private static void Check(string initial, string[] candidates, string completed, string expected)
    {
        using var field = new SelectionUi.CompletingTextField(initial, candidates);
        var observed = "";
        field.Changed = () => observed = field.Text.ToString();
        field.ProcessKey(new KeyEvent(Key.Tab, new KeyModifiers()));
        RegressionCases.Check(field.Text.ToString() == completed && field.CursorPosition == field.Text.RuneCount,
            "Tab did not place the cursor after the completion.");
        field.ProcessKey(new KeyEvent((Key)'a', new KeyModifiers()));
        RegressionCases.Check(observed == expected, "Subsequent input corrupted the username.");
        var connection = new Connection("example.test", observed);
        connection.Validate();
        RegressionCases.Check(connection.User == expected, "The connection received a different username.");
    }
}
