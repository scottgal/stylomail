namespace StyloMail.Jev.Tests;

/// <summary>
/// A fact that contacts the real semantic provider and therefore needs a credential.
/// </summary>
/// <remarks>
/// <para>
/// Skipped when no credential is available, naming both places one may be supplied. The suite has to
/// stay runnable with nothing installed: a normal <c>dotnet test</c> that fails because a key is
/// absent is a suite people learn to ignore, and this one guards a live external call.
/// </para>
/// <para>
/// This mirrors <c>LiveHostFactAttribute</c> in the desktop suite and <c>HarnessFactAttribute</c> in
/// the protocol harness, which are the same idea for a running host and for a container runtime.
/// </para>
/// </remarks>
public sealed class JevLiveFactAttribute : FactAttribute
{
    public JevLiveFactAttribute()
    {
        if (!JevCorpus.TryReadCredential(out _))
        {
            // Deliberately the same message the recorder throws, so a reader gets the fix either way.
            // It names the two places and never a value.
            Skip = JevCorpus.MissingCredentialMessage;
        }
    }
}

/// <summary>
/// A fact that replays committed recordings, and is skipped while there are none.
/// </summary>
/// <remarks>
/// <para>
/// <b>Skipped rather than passing when the corpus is empty.</b> A replay test with nothing to replay
/// would be green and would assert nothing, which is the failure the corpus README names: a fixture
/// nothing asserts on is documentation that rots. Reporting the absence is the honest result.
/// </para>
/// <para>
/// Once a recording is committed this stops skipping by itself, so nobody has to remember to
/// remove a gate.
/// </para>
/// </remarks>
public sealed class JevCorpusReplayFactAttribute : FactAttribute
{
    public JevCorpusReplayFactAttribute()
    {
        if (!JevCorpus.HasAnyRecording)
        {
            Skip = $"No recordings are committed under {JevCorpus.FixturesDirectory} yet, so there is "
                + "nothing to replay and nothing would be asserted. Record with a credential to turn "
                + "this test on; it stops skipping as soon as one recording exists.";
        }
    }
}
