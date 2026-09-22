using StyloMail.Adaptive.Profiles;
using StyloMail.Host.Hosting;
using StyloMail.Jev;

namespace StyloMail.Host.Tests;

/// <summary>
/// The credential model: two secrets, both from the environment, with the two failure modes that
/// matter, half-configured, and too short to be worth anything.
/// </summary>
public sealed class CredentialTests
{
    private const string StrongKey = "0123456789abcdef0123456789abcdef"; // exactly 32 bytes

    [Fact]
    public void No_secrets_means_assessment_is_simply_not_configured()
    {
        // Not an error. A deployment that has not configured Assessment is a legitimate
        // configuration, and it gets the refusing sentinel rather than a refusal to boot.
        Assert.Equal(CredentialState.NotConfigured, HostCredentials.Resolve(null, null));
        Assert.Equal(CredentialState.NotConfigured, HostCredentials.Resolve("  ", "\t"));
    }

    [Fact]
    public void Both_secrets_present_is_configured()
    {
        Assert.Equal(CredentialState.Configured, HostCredentials.Resolve("jev-key", StrongKey));
    }

    [Fact]
    public void A_jev_key_without_a_profile_key_refuses_to_start()
    {
        // The case worth being loud about. A deployment that ran on with the Jev key live and no
        // pseudonymisation key would look perfectly healthy while quietly collapsing the tenant
        // isolation the keyed hashes exist to provide.
        var error = Assert.Throws<InvalidOperationException>(
            () => HostCredentials.Resolve("jev-key", null));

        Assert.Contains(HostCredentials.ProfileKeyEnvironmentVariable, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_profile_key_without_a_jev_key_refuses_to_start()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => HostCredentials.Resolve(null, StrongKey));

        Assert.Contains(JevOptions.ApiKeyEnvironmentVariable, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_short_master_key_refuses_to_start()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => HostCredentials.Resolve("jev-key", "too-short"));

        Assert.Contains("32", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_never_repeats_the_secret_it_rejected()
    {
        // Exception text travels: into logs, into a crash reporter, into a terminal someone is
        // screen-sharing. A message that explains which secret is wrong by quoting it has turned
        // every one of those into a disclosure.
        const string secret = "SUPER-SECRET-JEV-KEY-VALUE-1741";

        var error = Assert.Throws<InvalidOperationException>(
            () => HostCredentials.Resolve(secret, null));

        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);

        const string shortKey = "SHORT-SECRET-VALUE";
        var shortError = Assert.Throws<InvalidOperationException>(
            () => HostCredentials.Resolve("jev-key", shortKey));

        Assert.DoesNotContain(shortKey, shortError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_documented_minimum_matches_the_implementation_that_enforces_it()
    {
        // The host mirrors the constant so the failure can name the environment variable the
        // operator needs to fix, rather than surfacing as an argument error from three layers
        // down. A mirror that drifts would let a key pass here and be rejected there.
        Assert.Equal(ProfileKeyHasher.MinimumKeyBytes, HostCredentials.MinimalMasterKeyBytes);
    }

    [Fact]
    public void A_key_of_exactly_the_minimum_length_is_accepted()
    {
        // Boundary in the accepting direction: an off-by-one here would reject a key the hasher
        // itself would have taken.
        Assert.Equal(
            CredentialState.Configured,
            HostCredentials.Resolve("jev-key", new string('k', ProfileKeyHasher.MinimumKeyBytes)));
    }
}
