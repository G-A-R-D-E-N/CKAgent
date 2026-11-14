namespace Garden.CkAgent;

// Constraint: the strict pipe is the only default, so a client must ask before the low-labelled one is even tried.
internal static class PluginPipes
{
    internal const string Strict = "ckagent";
    internal const string Low = "ckagent-low";

    // Invariant: the strict pipe is tried first even when the opt-in is on, so the loose one is never the easy path.
    internal static IReadOnlyList<string> Candidates(bool allowLow) =>
        allowLow ? new[] { Strict, Low } : new[] { Strict };

    internal static string Describe(bool allowLow) =>
        string.Join(" or ", Candidates(allowLow).Select(name => $"\\\\.\\pipe\\{name}"));
}
