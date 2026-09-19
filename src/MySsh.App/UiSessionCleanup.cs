namespace MySsh.App;

/// <summary>Persistence failure must never bypass restoration of terminal ownership.</summary>
internal static class UiSessionCleanup
{
    internal static void Run(Action saveState, Action shutdown)
    {
        try { saveState(); }
        finally { shutdown(); }
    }
}
