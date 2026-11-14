namespace Garden.CkAgent;

/// Constraint: a status reply must say what this client can see, because a low-integrity host sees no editor process.
internal sealed record ClientView(
    string? Integrity,
    bool Elevated,
    IReadOnlyList<string> Privileges,
    bool SeesProcess,
    bool SeesWindow,
    string Sight)
{
    internal static ClientView Describe(bool processFound, bool windowFound) =>
        new(WindowsIntegrity.LevelName(), WindowsIntegrity.IsElevated(), WindowsIntegrity.EnabledPrivileges(),
            processFound, windowFound, SightOf(processFound, windowFound));

    // Invariant: a frame window is sight of the editor even when its process cannot be opened.
    internal static string SightOf(bool processFound, bool windowFound)
    {
        if (processFound) return "process";
        return windowFound ? "window" : "none";
    }
}
