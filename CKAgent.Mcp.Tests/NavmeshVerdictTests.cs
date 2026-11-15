using Garden.CkAgent;

namespace CKAgent.Mcp.Tests;

/// Regression: the editor's navmesh check writes free text, and a real error (a degenerate triangle, a navmesh
/// altered but not checked out) has to land in the verdict instead of being filed as an unrecognised line.
public sealed class NavmeshVerdictTests
{
    // Constraint: these are the sentences this editor build writes, copied as it writes them.
    private const string Degenerate = "Navmesh 0012ABCD Cell Wilderness, Triangle 42 is degenerate and has an invalid area";
    private const string NotCheckedOut = "NavMesh ID 0012ABCD in cell Wilderness is altered but not checked out";
    private const string Unconnected = "Checking all navmeshes in WorldSpace (Warning 3 Unconnected NavMeshes)";

    [Fact]
    public void AnErrorInACellFailsTheVerdictAndNamesThatCell()
    {
        NavmeshVerdict verdict = NavmeshVerdictParser.Parse(new[]
        {
            "Checking all navmeshes in WorldSpace",
            Degenerate,
            NotCheckedOut,
            "something the parser has never seen",
        });

        Assert.Equal("errors", verdict.Code);
        Assert.Equal(2, verdict.Errors);
        Assert.Equal(0, verdict.Warnings);
        Assert.Single(verdict.Cells);
        Assert.Equal("Wilderness", verdict.Cells[0].Cell);
        Assert.Equal("errors", verdict.Cells[0].Verdict);
        Assert.Contains(verdict.Cells[0].Findings, finding => finding.Message.Contains("degenerate"));
        Assert.Contains("0012ABCD", verdict.Cells[0].Navmeshes);
        Assert.Contains("something the parser has never seen", verdict.Unparsed);
        Assert.Equal(4, verdict.Lines);
    }

    [Fact]
    public void AWarningThatNamesNoCellIsReportedRatherThanDropped()
    {
        NavmeshVerdict verdict = NavmeshVerdictParser.Parse(new[] { Unconnected });

        Assert.Equal("warnings", verdict.Code);
        Assert.Equal(0, verdict.Errors);
        Assert.Equal(1, verdict.Warnings);
        Assert.Empty(verdict.Cells);
        NavmeshFinding finding = Assert.Single(verdict.Unattributed);
        Assert.Equal("3 unconnected navmesh", finding.Message);
        Assert.Contains("Warning 3 Unconnected NavMeshes", finding.Line);
    }

    [Fact]
    public void TheSameFindingOnTwoLinesIsCountedOnceWithItsCount()
    {
        NavmeshVerdict verdict = NavmeshVerdictParser.Parse(new[] { Degenerate, Degenerate });

        Assert.Equal(2, verdict.Lines);
        NavmeshFinding finding = Assert.Single(verdict.Cells[0].Findings);
        Assert.Equal(2, finding.Count);
    }

    [Fact]
    public void SilenceIsUnknownRatherThanClean()
    {
        NavmeshVerdict verdict = NavmeshVerdictParser.Parse(Array.Empty<string>());

        Assert.Equal("unknown", verdict.Code);
        Assert.Contains("unknown", verdict.Summary);
    }

    [Fact]
    public void AnInformationalOnlyCheckIsClean()
    {
        NavmeshVerdict verdict = NavmeshVerdictParser.Parse(new[]
        {
            "Checking all navmeshes in WorldSpace",
            "Checking Navmesh Info Map",
        });

        Assert.Equal("clean", verdict.Code);
        Assert.Empty(verdict.Unparsed);
        Assert.Equal(0, verdict.Errors);
        Assert.Equal(0, verdict.Warnings);
    }
}
