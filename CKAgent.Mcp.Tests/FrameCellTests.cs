using Garden.CkAgent;

namespace CKAgent.Mcp.Tests;

/// Regression: framing a reference in a named cell has to open that cell and wait for the window to actually
/// hold a different one, because a cell that was already open satisfies a wait that only checks for "a cell".
public sealed class FrameCellTests
{
    private const string FirstCell =
        "{\"editorId\":\"DiamondCityOrigin\",\"formId\":\"00000FEE\",\"cell\":\"0x111\",\"readable\":true,\"loaded\":true}";

    private const string SecondCell =
        "{\"editorId\":\"AndrewStation01\",\"formId\":\"00074D28\",\"cell\":\"0x222\",\"readable\":true}";

    // Constraint: the handle read before the load and every read after it, so the second answer is what changes.
    private static FakePlugin Plugin(string cellAfterLoad = SecondCell) => new((command, seen) => command switch
    {
        "cell" => FakePlugin.Reply.Good(seen == 0 ? FirstCell : cellAfterLoad),
        "frame DiamondStandsMayor01" => FakePlugin.Reply.Good("{\"aim\":1.0000,\"references\":1961}"),
        _ when command.StartsWith("ui.cellload ", StringComparison.Ordinal) => FakePlugin.Reply.Good("{\"posted\":true}"),
        _ => FakePlugin.Reply.Failed("unexpected", command),
    });

    [Fact]
    public void AnAskedCellIsLoadedBeforeTheCameraFrames()
    {
        FakePlugin plugin = Plugin();

        CameraResult result = new CameraTools(plugin).Frame("DiamondStandsMayor01", cell: "AndrewStation01");

        Assert.Equal("ok", result.Code);
        int load = plugin.Calls.IndexOf("ui.cellload AndrewStation01");
        int frame = plugin.Calls.IndexOf("frame DiamondStandsMayor01");
        Assert.True(load >= 0, string.Join(" | ", plugin.Calls));
        Assert.True(frame > load, "the frame must come after the load: " + string.Join(" | ", plugin.Calls));
        Assert.Contains("frame DiamondStandsMayor01", result.Message);
    }

    [Fact]
    public void ACellThatNeverChangesIsNotReportedAsLoaded()
    {
        FakePlugin plugin = Plugin(cellAfterLoad: FirstCell);

        CameraResult result = new CameraTools(plugin).Frame("DiamondStandsMayor01", cell: "AndrewStation01");

        Assert.Equal("cell_not_loaded", result.Code);
        Assert.Contains("did not report a new cell", result.Message);
        Assert.DoesNotContain("frame DiamondStandsMayor01", plugin.Calls);
    }

    [Fact]
    public void AFrameWithNoCellAskedNeverLoadsOne()
    {
        FakePlugin plugin = Plugin();

        CameraResult result = new CameraTools(plugin).Frame("DiamondStandsMayor01");

        Assert.Equal("ok", result.Code);
        Assert.DoesNotContain("ui.cellload", string.Join(" ", plugin.Calls));
        Assert.Contains("frame DiamondStandsMayor01", plugin.Calls);
    }
}
