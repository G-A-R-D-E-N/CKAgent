using Garden.CkAgent;

namespace CKAgent.Mcp.Tests;

/// Constraint: framing the cell that is already open must not reopen it and wait for a change that cannot come.
public sealed class FrameCellTests
{
    private const string OriginLoaded =
        "{\"editorId\":\"DiamondCityOrigin\",\"formId\":\"00000FEE\",\"cell\":\"0x111\",\"readable\":true,\"loaded\":true}";

    private static FakePlugin FrameAnswers(string cellFirst, string? cellAfterLoad = null, string? loadedRow = null)
    {
        return new FakePlugin((command, seen) => command switch
        {
            "cell" => FakePlugin.Reply.Good(seen == 0 || cellAfterLoad == null ? cellFirst : cellAfterLoad),
            _ when loadedRow != null && command == $"ui.cellload {loadedRow}" => FakePlugin.Reply.Good($"{{\"cell\":\"{loadedRow}\",\"row\":3}}"),
            _ when command.StartsWith("frame ", StringComparison.Ordinal) => FakePlugin.Reply.Good("{\"aim\":1.0000,\"references\":1961}"),
            _ => FakePlugin.Reply.Failed("unexpected", command),
        });
    }

    [Fact]
    public void AFrameOnTheAlreadyLoadedCellDoesNotLoadIt()
    {
        var plugin = FrameAnswers(OriginLoaded);

        CameraResult result = new CameraTools(plugin).Frame("DiamondStandsMayor01", cell: "DiamondCityOrigin");

        Assert.Equal("ok", result.Code);
        Assert.DoesNotContain("ui.cellload", string.Join(" ", plugin.Calls));
        Assert.Contains("already loaded", result.Message);
        Assert.Contains("DiamondCityOrigin", result.Message);
    }

    [Fact]
    public void TheAlreadyLoadedCheckIgnoresTheCaseOfTheName()
    {
        var plugin = FrameAnswers(OriginLoaded);

        CameraResult result = new CameraTools(plugin).Frame("DiamondStandsMayor01", cell: "diamondcityorigin");

        Assert.Equal("ok", result.Code);
        Assert.DoesNotContain("ui.cellload", string.Join(" ", plugin.Calls));
    }

    [Fact]
    public void AFrameOnAnotherCellLoadsItAndReportsWhatTheWindowHolds()
    {
        var plugin = FrameAnswers(OriginLoaded,
            "{\"editorId\":\"AndrewStation01\",\"formId\":\"00074D28\",\"cell\":\"0x222\",\"readable\":true}",
            loadedRow: "AndrewStation01");

        CameraResult result = new CameraTools(plugin).Frame("DiamondStandsMayor01", cell: "AndrewStation01");

        Assert.Equal("ok", result.Code);
        Assert.Contains("ui.cellload AndrewStation01", plugin.Calls);
        Assert.Contains("in AndrewStation01", result.Message);
        Assert.DoesNotContain("already loaded", result.Message);
    }

    [Fact]
    public void AFrameWithNoCellAskedNeverLoadsOne()
    {
        var plugin = FrameAnswers(OriginLoaded);

        CameraResult result = new CameraTools(plugin).Frame("DiamondStandsMayor01");

        Assert.Equal("ok", result.Code);
        Assert.DoesNotContain("ui.cellload", string.Join(" ", plugin.Calls));
        Assert.Contains("in DiamondCityOrigin", result.Message);
    }
}
