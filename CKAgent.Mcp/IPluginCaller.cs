namespace Garden.CkAgent;

/// Constraint: every caller of the plugin pipe must go through this, so the retry logic can be driven without one.
public interface IPluginCaller
{
    bool TryCall(string command, out string payload, out string code, out string message);
}
