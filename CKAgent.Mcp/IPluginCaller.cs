namespace Garden.CkAgent;

/// Constraint: only the plugin reaches editor internals, and every caller must fail with a code rather than an
/// exception, so the seam between this process and the editor is a single method.
public interface IPluginCaller
{
    bool TryCall(string command, out string payload, out string code, out string message);
}
