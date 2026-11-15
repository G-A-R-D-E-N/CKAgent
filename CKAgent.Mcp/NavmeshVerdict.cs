using System.Text.RegularExpressions;

namespace Garden.CkAgent;

public sealed record NavmeshFinding(
    string Severity,
    string Message,
    string? Navmesh,
    int Count,
    string Line);

public sealed record NavmeshCellVerdict(
    string Cell,
    string Verdict,
    int Errors,
    int Warnings,
    IReadOnlyList<string> Navmeshes,
    IReadOnlyList<NavmeshFinding> Findings);

public sealed record NavmeshVerdict(
    string Verdict,
    string Code,
    string Summary,
    int Lines,
    int Errors,
    int Warnings,
    IReadOnlyList<NavmeshCellVerdict> Cells,
    IReadOnlyList<NavmeshFinding> Unattributed,
    IReadOnlyList<string> Unparsed);

// Constraint: every pattern is a message template this editor build carries, never a guess from a keyword.
public static class NavmeshVerdictParser
{
    private sealed record Rule(Regex Pattern, string Severity, Func<Match, string> Message);

    private static Rule Make(string pattern, string severity, Func<Match, string> message)
        => new(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), severity, message);

    private static readonly Rule[] Rules =
    {
        Make(@"Navmesh \((?<nav>[0-9A-F]{8})\) in Cell (?<cell>.+?) has warnings that need to be fixed",
            "warning", _ => "has warnings that need to be fixed"),
        Make(@"Navmesh (?<nav>[0-9A-F]{8}) Cell (?<cell>.+?), has a large number of triangles, (?<n>\d+)",
            "warning", m => $"has a large number of triangles ({m.Groups["n"].Value})"),
        Make(@"Navmesh (?<nav>[0-9A-F]{8}) Cell (?<cell>.+?), Triangle (?<tri>\d+) is degenerate",
            "error", m => $"triangle {m.Groups["tri"].Value} is degenerate and has an invalid area"),
        Make(@"NavMesh ID (?<nav>[0-9A-F]{8}) in cell (?<cell>.+?) has invalid cover data",
            "warning", _ => "has invalid cover data, which will be removed"),
        Make(@"NavMesh ID (?<nav>[0-9A-F]{8}) in cell (?<cell>.+?) is altered but not checked out",
            "error", _ => "is altered but is not checked out to the current user"),
        Make(@"Potentially overlapping navmesh detected while saving Navmesh \((?<nav>[0-9A-F]{8})\) in cell (?<cell>.+?) \(",
            "warning", _ => "is potentially overlapping another navmesh"),
        Make(@"NavMeshInfo (?<nav>[0-9A-F]{8}) in cell (?<cell>.+?), has an invalid door portal to form (?<other>[0-9A-F]{8})",
            "warning", m => $"has an invalid door portal to form {m.Groups["other"].Value}"),
        Make(@"NavMesh (?<nav>[0-9A-F]{8}) in cell (?<cell>.+?), has an invalid door portal to form (?<other>[0-9A-F]{8})",
            "warning", m => $"has an invalid door portal to form {m.Groups["other"].Value}"),
        Make(@"NavMesh (?<nav>[0-9A-F]{8}) contains portal pointing to invalid NavMesh ID (?<other>[0-9A-F]{8})",
            "warning", m => $"has a portal pointing to invalid navmesh {m.Groups["other"].Value}"),
        Make(@"Found Two Road markers on the same navmesh (?<nav>[0-9A-F]{8}) in cell (?<cell>.+?) \(found checking",
            "warning", _ => "has two road markers"),
        Make(@"Bad index in CheckNavMesh", "error", _ => "reported a bad index"),
        Make(@"Degenerate triangles found in CheckNavMesh", "error", _ => "found degenerate triangles"),
        Make(@"Edges (?<a>\d+) and (?<b>\d+)\s*point to the same triangle", "error",
            m => $"edges {m.Groups["a"].Value} and {m.Groups["b"].Value} point to the same triangle"),
        Make(@"Opposite normals in linked triangles", "error", _ => "linked triangles have opposite normals"),
        Make(@"Triangle missing a link", "error", _ => "a triangle is missing a link"),
        Make(@"Triangle linking is mismatched", "error", _ => "triangle linking is mismatched"),
        Make(@"Linked triangle vertices do not match", "error", _ => "linked triangle vertices do not match"),
        Make(@"\bNAVMESH ERROR\b", "error", _ => "reported a navmesh error")
    };

    // Constraint: these suffix warnings ride on a summary line, so they must be read from that same line.
    private static readonly Rule[] Suffixes =
    {
        Make(@"\(Warning (?<n>\d+) Unconnected NavMesh(?:es)?\)", "warning",
            m => $"{m.Groups["n"].Value} unconnected navmesh"),
        Make(@"\(Warning small number of triangles in a single navmesh\)", "warning",
            _ => "small number of triangles in a single navmesh")
    };

    private static readonly Regex[] Informational =
    {
        new(@"Checking all navmeshes in WorldSpace", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"Checking out Navmeshes and removing current portals", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"Checking Navmesh Info Map", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^\s*Check NavMeshes\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
    };

    private sealed class CellBuilder
    {
        public string Cell { get; init; } = "";
        public List<NavmeshFinding> Findings { get; } = new();
        public SortedSet<string> Navmeshes { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static NavmeshVerdict Parse(IReadOnlyList<string> lines)
    {
        var cells = new Dictionary<string, CellBuilder>(StringComparer.OrdinalIgnoreCase);
        var unattributed = new List<NavmeshFinding>();
        var unparsed = new List<string>();
        int recognized = 0;

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            bool matched = false;
            foreach (Rule rule in Rules)
            {
                Match match = rule.Pattern.Match(line);
                if (!match.Success) continue;

                string? navmesh = Group(match, "nav");
                string? cell = Group(match, "cell");
                Add(cells, unattributed, cell, new NavmeshFinding(rule.Severity, rule.Message(match), navmesh, 1, line));
                matched = true;
                recognized++;
                break;
            }

            foreach (Rule rule in Suffixes)
            {
                Match match = rule.Pattern.Match(line);
                if (!match.Success) continue;

                string? navmesh = MatchId(line);
                Add(cells, unattributed, null, new NavmeshFinding(rule.Severity, rule.Message(match), navmesh, 1, line));
                matched = true;
                recognized++;
            }

            if (matched) continue;

            if (Informational.Any(pattern => pattern.IsMatch(line)))
            {
                recognized++;
                continue;
            }

            unparsed.Add(line);
        }

        var reports = new List<NavmeshCellVerdict>();
        int errors = 0, warnings = 0;

        foreach (CellBuilder builder in cells.Values.OrderBy(cell => cell.Cell, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<NavmeshFinding> findings = Condense(builder.Findings);
            int cellErrors = findings.Count(finding => finding.Severity == "error");
            int cellWarnings = findings.Count(finding => finding.Severity == "warning");
            errors += cellErrors;
            warnings += cellWarnings;

            reports.Add(new NavmeshCellVerdict(
                builder.Cell,
                VerdictOf(cellErrors, cellWarnings),
                cellErrors,
                cellWarnings,
                builder.Navmeshes.ToList(),
                findings));
        }

        IReadOnlyList<NavmeshFinding> loose = Condense(unattributed);
        errors += loose.Count(finding => finding.Severity == "error");
        warnings += loose.Count(finding => finding.Severity == "warning");

        // Constraint: silence is not a pass, so a check that wrote nothing readable is unknown, never clean.
        string code = errors > 0 ? "errors"
            : warnings > 0 ? "warnings"
            : recognized > 0 ? "clean"
            : "unknown";

        return new NavmeshVerdict(
            code,
            code,
            SummaryOf(code, lines.Count, errors, warnings, reports.Count, unparsed.Count),
            lines.Count,
            errors,
            warnings,
            reports,
            loose,
            unparsed);
    }

    private static void Add(Dictionary<string, CellBuilder> cells, List<NavmeshFinding> unattributed,
        string? cell, NavmeshFinding finding)
    {
        if (string.IsNullOrWhiteSpace(cell))
        {
            unattributed.Add(finding);
            return;
        }

        string name = cell.Trim();
        if (!cells.TryGetValue(name, out CellBuilder? builder))
        {
            builder = new CellBuilder { Cell = name };
            cells[name] = builder;
        }

        builder.Findings.Add(finding);
        if (!string.IsNullOrEmpty(finding.Navmesh)) builder.Navmeshes.Add(finding.Navmesh!);
    }

    private static IReadOnlyList<NavmeshFinding> Condense(List<NavmeshFinding> findings)
    {
        var merged = new List<NavmeshFinding>();

        foreach (var group in findings
            .GroupBy(finding => (finding.Severity, finding.Message, finding.Navmesh))
            .OrderBy(group => group.Key.Severity == "error" ? 0 : 1)
            .ThenBy(group => group.Key.Navmesh, StringComparer.OrdinalIgnoreCase))
        {
            NavmeshFinding first = group.First();
            merged.Add(first with { Count = group.Count() });
        }

        return merged;
    }

    private static string VerdictOf(int errors, int warnings)
        => errors > 0 ? "errors" : warnings > 0 ? "warnings" : "clean";

    private static string SummaryOf(string code, int lines, int errors, int warnings, int cells, int unparsed)
    {
        string scope = cells == 1 ? "1 cell" : $"{cells} cells";

        return code switch
        {
            "errors" => $"check reported {errors} error(s) and {warnings} warning(s) across {scope}",
            "warnings" => $"check reported {warnings} warning(s) across {scope}",
            "clean" => $"check reported no findings across {scope}",
            _ => $"check wrote nothing this tool recognises in {lines} line(s), so the verdict is unknown" +
                 (unparsed > 0 ? $" ({unparsed} unrecognised line(s))" : "")
        };
    }

    private static string? Group(Match match, string name)
            => match.Groups[name].Success ? match.Groups[name].Value : null;

    private static string? MatchId(string line)
    {
        Match match = Regex.Match(line, @"(?<![0-9A-Fa-f])([0-9A-Fa-f]{8})(?![0-9A-Fa-f])");
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }
}
