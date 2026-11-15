using System.Text.RegularExpressions;

namespace Garden.CkAgent;

// Constraint: the check's own report is the only evidence a save may rest on, so what the report means is decided here
// rather than in each caller.
public sealed record NavmeshCheckEvidence(
    string Code,
    bool Clean,
    bool Silent,
    bool Corroborated,
    string Reason);

// Regression: the Creation Kit's Check NavMeshes writes nothing for a cell with no findings, and the report window then
// holds only this tool's own CALL/RPLY lines. That used to parse as `unknown` and every clean cell was refused as
// `verdict_mismatch` against the plugin's `clean`. Silence is a clean signal only when the plugin's own classifier
// agrees and the geometry the save would compare is readable; everything else keeps refusing.
public static class NavmeshCheckGate
{
    private static readonly Regex ProtocolLine =
        new(@"^(CALL|RPLY) \d+ ", RegexOptions.CultureInvariant);

    public static bool IsProtocolLine(string line) => ProtocolLine.IsMatch(line.Trim());

    /// The window carries nothing but this tool's own pipe echo, so the editor itself wrote no check output.
    public static bool OnlyProtocolLines(IReadOnlyList<string> lines)
    {
        bool any = false;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (!IsProtocolLine(trimmed)) return false;
            any = true;
        }

        return any;
    }

    /// Invariant: a clean answer always has a reason that can be pointed at - either a recognisable report both sides
    /// agree on, or a silent report that the plugin calls clean and the cell's own digests corroborate.
    public static NavmeshCheckEvidence Resolve(string? reported, NavmeshVerdict verdict, IReadOnlyList<string> lines,
                                               bool parsedClean, NavmeshExpected? geometry, string? blockedBy)
    {
        bool silent = OnlyProtocolLines(lines);
        bool corroborated = geometry is { Records.Count: > 0 };

        if (!string.IsNullOrEmpty(blockedBy))
        {
            return new NavmeshCheckEvidence("blocked", false, silent, corroborated,
                $"the editor was left blocked by \"{blockedBy}\"");
        }

        if (silent && !parsedClean)
        {
            if (!string.Equals(reported, "clean", StringComparison.OrdinalIgnoreCase))
            {
                return new NavmeshCheckEvidence("unknown", false, true, corroborated,
                    $"the check wrote no findings, and the plugin's own verdict was '{reported ?? "missing"}'");
            }

            if (!corroborated)
            {
                return new NavmeshCheckEvidence("unknown", false, true, false,
                    "the check wrote no findings, but the cell reported no geometry digests to corroborate a clean " +
                    "verdict, so the save is refused");
            }

            return new NavmeshCheckEvidence("clean", true, true, true,
                $"the check wrote no findings ({lines.Count} protocol line(s) only), the plugin's own verdict is clean, " +
                $"and the cell's geometry digests corroborate it ({geometry!.Records.Count} record(s))");
        }

        if (reported is not null && !string.Equals(reported, verdict.Code, StringComparison.OrdinalIgnoreCase))
        {
            return new NavmeshCheckEvidence("mismatch", false, silent, corroborated,
                $"the plugin's own check reported '{reported}' while the parsed report says '{verdict.Code}'");
        }

        if (verdict.Errors > 0)
            return new NavmeshCheckEvidence("dirty", false, silent, corroborated, verdict.Summary);

        if (verdict.Warnings > 0)
            return new NavmeshCheckEvidence("warnings", false, silent, corroborated, verdict.Summary);

        if (parsedClean)
            return new NavmeshCheckEvidence("clean", true, silent, corroborated, verdict.Summary);

        return new NavmeshCheckEvidence("unknown", false, silent, corroborated, verdict.Summary);
    }
}
