using Garden.CkAgent;

namespace CKAgent.Mcp.Tests;

/// Constraint: the pipe policy decides who can drive the editor, so it is asserted without an editor or a pipe.
public sealed class PipePolicyTests
{
    // Constraint: a low-integrity client must not be handed a labelled-low pipe unless the host opted in to it.
    [Fact]
    public void TheDefaultCandidateIsTheStrictPipeAlone()
    {
        Assert.Equal(new[] { "ckagent" }, PluginPipes.Candidates(allowLow: false));
        Assert.Equal(@"\\.\pipe\ckagent", PluginPipes.Describe(allowLow: false));
    }

    // Constraint: opting in must not make the loose pipe the easy path, so the strict one is still tried first.
    [Fact]
    public void TheOptInTriesTheStrictPipeFirst()
    {
        Assert.Equal(new[] { "ckagent", "ckagent-low" }, PluginPipes.Candidates(allowLow: true));
        Assert.Equal(@"\\.\pipe\ckagent or \\.\pipe\ckagent-low", PluginPipes.Describe(allowLow: true));
    }

    // Constraint: a refusal has to name the side to fix - the absent opt-in, not a plugin that predates the pipe.
    [Fact]
    public void ARefusalAtLowIntegrityNamesTheOptIn()
    {
        var denied = new UnauthorizedAccessException("Access is denied.");

        string without = PluginClient.DeniedMessage(denied, "low", allowLow: false);
        Assert.Contains("--allow-low-pipe", without);
        Assert.Contains("LowIntegrityPipe=1", without);
        Assert.DoesNotContain("opted in", without);

        string with = PluginClient.DeniedMessage(denied, "low", allowLow: true);
        Assert.Contains("ckagent-low", with);
        Assert.DoesNotContain("--allow-low-pipe", with);
    }
}
