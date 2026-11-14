namespace Garden.CkAgent;

// Constraint: the host must pick a state directory it can actually write, or it starts and fails at its first save.
internal static class StateFolder
{
    // Constraint: a directory counts only if a probe file can be written there, since existence proves nothing.
    internal static bool Choose(IReadOnlyList<string> candidates, out string chosen, out string note)
    {
        chosen = string.Empty;
        note = string.Empty;

        var reasons = new List<string>();
        foreach (string candidate in candidates)
        {
            string? refusal = TryPrepare(candidate);
            if (refusal == null)
            {
                chosen = candidate;
                note = reasons.Count == 0 ? string.Empty : $"using {candidate} for state: {string.Join("; ", reasons)}";
                return true;
            }

            reasons.Add(refusal);
        }

        note = string.Join("; ", reasons);
        return false;
    }

    private static string? TryPrepare(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(Path.Combine(directory, "jobs"));
            Directory.CreateDirectory(Path.Combine(directory, "backups"));

            string probe = Path.Combine(directory, $".ckagent-write-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return null;
        }
        catch (Exception exception)
        {
            string reason = exception.Message.Trim();
            if (reason.Length > 90) reason = reason[..90] + "...";
            return $"{directory} is not writable ({exception.GetType().Name}: {reason})";
        }
    }
}
