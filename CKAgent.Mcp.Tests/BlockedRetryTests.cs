using Garden.CkAgent;

namespace CKAgent.Mcp.Tests;

/// Constraint: the three still-blocked replies are driven from canned pipe replies instead of a running editor.
public sealed class BlockedRetryTests
{
    private const string BlockedStatus = "{\"blockedBy\":\"Data\",\"modalButtons\":[\"OK\",\"Cancel\"],\"dismissBy\":\"Cancel\"}";

    private static string Modals(string hwnd, string title = "Data") =>
        $"[{{\"hwnd\":\"{hwnd}\",\"title\":\"{title}\",\"blocking\":true,\"visible\":false}}]";

    [Fact]
    public void ADismissedDialogThatStillBlocksIsReportedWithItsHandle()
    {
        var plugin = new FakePlugin(command => command switch
        {
            "camera" => FakePlugin.Reply.Failed("no_cell", "no cell is loaded"),
            "status" => FakePlugin.Reply.Good(BlockedStatus),
            "ui.modals" => FakePlugin.Reply.Good(Modals("0x1234")),
            "ui.dismiss 0x1234" => FakePlugin.Reply.Good("{\"dismissed\":1,\"by\":\"Cancel\",\"hwnd\":\"0x1234\"}"),
            _ => FakePlugin.Reply.Failed("unexpected", command),
        });

        bool ok = BlockedEditor.TryCall(plugin, "camera", out _, out string code, out string message);

        Assert.False(ok);
        Assert.Equal("no_cell", code);
        Assert.Contains("no cell is loaded", message);
        Assert.Contains("dismissed with Cancel but the block persists", message);
        Assert.Contains("\"Data\" at 0x1234", message);
        Assert.Contains("ck.ui method=ui.click args=\"0x1234 <button>\"", message);
        Assert.Equal(
            new[] { "camera", "status", "ui.modals", "ui.dismiss 0x1234", "camera", "camera", "status", "ui.modals" },
            plugin.Calls);
    }

    [Fact]
    public void ADialogThatCouldNotBeDismissedIsNotReportedAsDismissed()
    {
        Environment.SetEnvironmentVariable("CKAGENT_TEST_DISMISS_FAILS", "1");
        try
        {
            var plugin = new FakePlugin(command => command switch
            {
                "camera" => FakePlugin.Reply.Failed("no_cell", "no cell is loaded"),
                "status" => FakePlugin.Reply.Good(BlockedStatus),
                "ui.modals" => FakePlugin.Reply.Good(Modals("0x1234")),
                _ => FakePlugin.Reply.Good("{}"),
            });

            bool ok = BlockedEditor.TryCall(plugin, "camera", out _, out string code, out string message);

            Assert.False(ok);
            Assert.Equal("no_cell", code);
            Assert.Contains("it could not be dismissed automatically", message);
            Assert.DoesNotContain("dismissed with", message);
            Assert.Contains("ck.ui method=ui.click args=\"0x1234 <button>\"", message);
            Assert.DoesNotContain("ui.dismiss", plugin.Calls);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CKAGENT_TEST_DISMISS_FAILS", null);
        }
    }

    [Fact]
    public void ASecondQueuedDialogIsTheHandleThatGetsReported()
    {
        var plugin = new FakePlugin((command, seen) => command switch
        {
            "camera" => FakePlugin.Reply.Failed("no_cell", "no cell is loaded"),
            "status" => FakePlugin.Reply.Good(seen == 0
                ? BlockedStatus
                : "{\"blockedBy\":\"Layers\",\"modalButtons\":[\"OK\"],\"dismissBy\":\"OK\"}"),
            "ui.modals" => FakePlugin.Reply.Good(seen == 0 ? Modals("0x1234") : Modals("0x5678", "Layers")),
            "ui.dismiss 0x1234" => FakePlugin.Reply.Good("{\"dismissed\":1,\"by\":\"Cancel\"}"),
            _ => FakePlugin.Reply.Failed("unexpected", command),
        });

        bool ok = BlockedEditor.TryCall(plugin, "camera", out _, out string code, out string message);

        Assert.False(ok);
        Assert.Equal("no_cell", code);
        Assert.Contains("\"Layers\" at 0x5678", message);
        Assert.Contains("args=\"0x5678 <button>\"", message);
        Assert.DoesNotContain("0x1234 <button>", message);
    }

    [Fact]
    public void AFailureWithNothingBlockingKeepsItsOwnMessage()
    {
        var plugin = new FakePlugin(command => command switch
        {
            "camera" => FakePlugin.Reply.Failed("no_cell", "no cell is loaded"),
            "status" => FakePlugin.Reply.Good("{\"blockedBy\":\"\",\"modalButtons\":[],\"dismissBy\":\"\"}"),
            _ => FakePlugin.Reply.Good("[]"),
        });

        bool ok = BlockedEditor.TryCall(plugin, "camera", out _, out string code, out string message);

        Assert.False(ok);
        Assert.Equal("no_cell", code);
        Assert.Equal("no cell is loaded", message);
        Assert.Equal(new[] { "camera", "status" }, plugin.Calls);
    }

    [Fact]
    public void ACallThatSucceedsAfterTheDismissalIsNotReportedAsBlocked()
    {
        var plugin = new FakePlugin((command, seen) => command switch
        {
            "camera" => seen == 0
                ? FakePlugin.Reply.Failed("no_cell", "no cell is loaded")
                : FakePlugin.Reply.Good("{\"cell\":\"DmndRadio01\"}"),
            "status" => FakePlugin.Reply.Good(BlockedStatus),
            "ui.modals" => FakePlugin.Reply.Good(Modals("0x1234")),
            "ui.dismiss 0x1234" => FakePlugin.Reply.Good("{\"dismissed\":1,\"by\":\"Cancel\"}"),
            _ => FakePlugin.Reply.Failed("unexpected", command),
        });

        bool ok = BlockedEditor.TryCall(plugin, "camera", out string payload, out string code, out _);

        Assert.True(ok);
        Assert.Equal("ok", code);
        Assert.Contains("DmndRadio01", payload);
    }

    /// Constraint: this stands in for the plugin pipe, answering the same command differently on its second call.
    private sealed class FakePlugin : IPluginCaller
    {
        private readonly Func<string, int, Reply> _answer;
        private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);

        public FakePlugin(Func<string, Reply> answer)
            : this((command, _) => answer(command))
        {
        }

        public FakePlugin(Func<string, int, Reply> answer) => _answer = answer;

        public List<string> Calls { get; } = new();

        public bool TryCall(string command, out string payload, out string code, out string message)
        {
            Calls.Add(command);
            _seen.TryGetValue(command, out int seen);
            _seen[command] = seen + 1;

            Reply reply = _answer(command, seen);
            payload = reply.Payload;
            code = reply.Code;
            message = reply.Message;
            return reply.Ok;
        }

        internal readonly record struct Reply(bool Ok, string Payload, string Code, string Message)
        {
            public static Reply Good(string payload = "{}") => new(true, payload, "ok", string.Empty);

            public static Reply Failed(string code, string message) => new(false, string.Empty, code, message);
        }
    }
}
