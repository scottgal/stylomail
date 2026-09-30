using StyloMail.Jev;

namespace StyloMail.Host.Hosting;

/// <summary>
/// The two secrets the assessment path may need, exactly as this process holds them.
/// </summary>
/// <remarks>
/// <b>Raw values, before any decision has been made about them.</b> What a pair of these means
/// depends on the provider, and that is <see cref="HostCredentials.ResolveForProvider"/>'s job, not
/// this record's: it carries facts so the policy stays a pure function that a test can call with any
/// pair it likes.
/// </remarks>
public sealed record AssessmentSecrets(string? JevApiKey, string? ProfileMasterKey)
{
    /// <summary>
    /// Reports presence and never content.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Overridden because the generated one would print both values, and a record's
    /// <c>ToString</c> is exactly what ends up in a log line when someone writes
    /// <c>LogInformation("assessment secrets: {Secrets}", secrets)</c>. Presence only, and not the
    /// length either: a log is a place a secret must not reach, not even in the shape of how long it
    /// is.
    /// </para>
    /// <para>
    /// <b>This does not make the object safe to log.</b> Structured destructuring reads the
    /// properties directly and never calls this, so <c>"{@Secrets}"</c> would print both values
    /// whatever is written here. The override closes the accidental case and not the deliberate one,
    /// and the rule stands: this object is never a log argument.
    /// </para>
    /// </remarks>
    public override string ToString()
        => $"AssessmentSecrets {{ JevApiKey = {Presence(JevApiKey)}, "
            + $"ProfileMasterKey = {Presence(ProfileMasterKey)} }}";

    private static string Presence(string? value)
        => string.IsNullOrWhiteSpace(value) ? "absent" : "present";
}

/// <summary>
/// Where the host reads its assessment secrets from, and the only place it reads them.
/// </summary>
/// <remarks>
/// <para>
/// <b>A seam, because the composition would otherwise be unprovable.</b> Reading process environment
/// variables directly inside the composition root works fine in production and cannot be tested:
/// the suite runs test classes in parallel, so a variable one test sets is visible to every other
/// test running at the same moment, and a test that depended on one would be asserting against
/// another test's environment. The alternative, arranging for no other test to care, makes the suite
/// correct only until someone adds one.
/// </para>
/// <para>
/// This is not a step towards reading secrets from configuration. Configuration is where a provider
/// <em>name</em> belongs (see <see cref="AssessmentProviderSelection"/>); a secret in a config key
/// ends up in a file, then in a repository, then in an image layer.
/// </para>
/// </remarks>
public interface IAssessmentSecretSource
{
    /// <summary>The secrets this process holds, with no decision applied to them.</summary>
    AssessmentSecrets Read();
}

/// <summary>
/// The production source: the two environment variables the deployment was given.
/// </summary>
/// <remarks>
/// Reads them and decides nothing. In particular it does not call
/// <see cref="HostCredentials.Resolve"/>, whose half-configured refusal is correct for the hosted
/// provider and wrong for the local one: applying it here would refuse to read the values at all,
/// and the provider-aware decision downstream would never get to see them.
/// </remarks>
public sealed class EnvironmentAssessmentSecretSource : IAssessmentSecretSource
{
    public AssessmentSecrets Read()
        => new(
            Environment.GetEnvironmentVariable(JevOptions.ApiKeyEnvironmentVariable),
            Environment.GetEnvironmentVariable(HostCredentials.ProfileKeyEnvironmentVariable));
}
