using Garden.CkAgent;

namespace CKAgent.Mcp.Tests;

/// Constraint: this stands in for the plugin pipe, answering the same command differently on its second call.
internal sealed class FakePlugin : IPluginCaller
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
