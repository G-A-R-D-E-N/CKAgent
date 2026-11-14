using Garden.CkAgent;

namespace CKAgent.Mcp.Tests;

/// Constraint: the startup decisions that make the host usable at low integrity are asserted without an editor.
public sealed class StartupTests
{
    // Constraint: the test host runs at low integrity here and %TEMP% is refused, so scratch lives beside the build.
    private static string Scratch()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    // Constraint: at low integrity the profile state directory is refused and the host must not abort at startup.
    [Fact]
    public void AnUnwritableStateDirectoryFallsBackToTheNextCandidate()
    {
        string root = Scratch();
        try
        {
            string blocker = Path.Combine(root, "not-a-folder");
            File.WriteAllText(blocker, "a file where the state directory would go");
            string denied = Path.Combine(blocker, "ckagent");
            string usable = Path.Combine(root, "low");

            bool chosen = StateFolder.Choose(new[] { denied, usable }, out string directory, out string note);

            Assert.True(chosen, note);
            Assert.Equal(usable, directory);
            Assert.Contains(denied, note);
            Assert.Contains("not writable", note);
            Assert.True(Directory.Exists(Path.Combine(usable, "jobs")));
            Assert.True(Directory.Exists(Path.Combine(usable, "backups")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AUsableFirstCandidateIsUsedWithoutANote()
    {
        string root = Scratch();
        try
        {
            bool chosen = StateFolder.Choose(new[] { root }, out string directory, out string note);

            Assert.True(chosen, note);
            Assert.Equal(root, directory);
            Assert.Equal(string.Empty, note);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void EveryRefusedCandidateIsNamedWhenNoneIsWritable()
    {
        string root = Scratch();
        try
        {
            string blocker = Path.Combine(root, "not-a-folder");
            File.WriteAllText(blocker, "a file where the state directory would go");
            string first = Path.Combine(blocker, "one");
            string second = Path.Combine(blocker, "two");

            bool chosen = StateFolder.Choose(new[] { first, second }, out string directory, out string note);

            Assert.False(chosen);
            Assert.Equal(string.Empty, directory);
            Assert.Contains(first, note);
            Assert.Contains(second, note);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // Constraint: a hidden frame is a running editor, so its reply must read as neither missing nor ready.
    [Fact]
    public void AHiddenEditorIsInvisibleRatherThanMissing()
    {
        Assert.Equal("editor_invisible",
            EditorDiscovery.DescribeStatus(processFound: true, hasWindow: true, windowVisible: false).Code);
        Assert.Equal("window_not_ready",
            EditorDiscovery.DescribeStatus(processFound: true, hasWindow: false, windowVisible: false).Code);
        Assert.Equal("ok",
            EditorDiscovery.DescribeStatus(processFound: true, hasWindow: true, windowVisible: true).Code);
        Assert.Contains("not visible",
            EditorDiscovery.DescribeStatus(processFound: true, hasWindow: true, windowVisible: false).Message);
    }

    // Constraint: a host handed no editor process must not report the editor it can see as not running.
    [Fact]
    public void AnEditorThisHostCannotSeeIsInvisibleRatherThanMissing()
    {
        (string code, string message) =
            EditorDiscovery.DescribeStatus(processFound: false, hasWindow: true, windowVisible: true);

        Assert.Equal("editor_invisible", code);
        Assert.Contains("cannot open its process", message);
        Assert.Contains("pipe", message);

        Assert.Equal("editor_not_running",
            EditorDiscovery.DescribeStatus(processFound: false, hasWindow: false, windowVisible: false).Code);
    }
}
