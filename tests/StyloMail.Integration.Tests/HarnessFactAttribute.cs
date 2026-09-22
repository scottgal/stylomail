namespace StyloMail.Integration.Tests;

/// <summary>
/// A fact that needs Docker and a real socket, and therefore does not run by default.
/// </summary>
/// <remarks>
/// Skipped unless <c>STYLOMAIL_HARNESS=1</c>. The unit suites must stay runnable with nothing
/// installed: a normal <c>dotnet test</c> that fails because a container runtime is absent is a
/// suite people learn to ignore. This mirrors <c>LiveHostFactAttribute</c>, which is the same idea
/// for the console.
/// </remarks>
public sealed class HarnessFactAttribute : FactAttribute
{
    public const string EnableVariable = "STYLOMAIL_HARNESS";

    public HarnessFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnableVariable)))
        {
            Skip = $"Set {EnableVariable}=1, and have a container runtime running, to run the protocol harness.";
        }
    }
}

/// <summary>
/// A harness fact that cannot run yet, for a reason that has already been measured and reported.
/// </summary>
/// <remarks>
/// <para>
/// Always skipped, and the reason travels in the attribute so it appears in the test output rather
/// than only in a commit message. That distinction is the point: a test left failing because it is
/// blocked is indistinguishable, to the next person, from a test that is failing because they broke
/// something, and a suite that is expected to be red is a suite people stop running.
/// </para>
/// <para>
/// The state this encodes is temporary by construction. When the blocked condition is removed, this
/// attribute is replaced with <see cref="HarnessFactAttribute"/> and the test starts proving itself
/// again. A skip that outlives its reason is worse than no skip, so the reason names what has to
/// change for it to go away.
/// </para>
/// </remarks>
public sealed class BlockedHarnessFactAttribute : FactAttribute
{
    public BlockedHarnessFactAttribute(string reason) => Skip = reason;
}
