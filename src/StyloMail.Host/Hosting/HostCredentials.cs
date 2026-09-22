using System.Text;
using StyloMail.Jev;

namespace StyloMail.Host.Hosting;

/// <summary>What the host resolved for its two deployment secrets.</summary>
public enum CredentialState
{
    /// <summary>Neither secret is present. Assessment is simply not configured on this deployment.</summary>
    NotConfigured,

    /// <summary>Both secrets are present and usable.</summary>
    Configured,
}

/// <summary>
/// The host's credential model: two secrets, both from environment variables.
/// </summary>
/// <remarks>
/// <b>Environment variables, not files and not config keys.</b> A secret written into a config key
/// ends up in an appsettings file, then in a repository, then in an image layer, and a key
/// committed to a repository must be treated as compromised and rotated rather than merely
/// deleted.
///
/// <para>
/// The values are read once, held in memory, and never logged, never placed in an exception
/// message, and never written into an assessment or a decision record. Exception text from this
/// class names <em>which</em> secret is missing and never what it contained.
/// </para>
/// </remarks>
public static class HostCredentials
{
    /// <summary>
    /// Environment variable holding the profile keyed-hash master key.
    /// </summary>
    /// <remarks>
    /// This is the master key for the tenant-scoped keyed hashes that pseudonymise profile
    /// identifiers (spec §11). It is a <b>keyed-hash secret</b>, not a passphrase: 32 bytes of
    /// high-entropy material, enforced by <c>ProfileKeyHasher</c>.
    ///
    /// <para>
    /// <b>One deployment-level master key, with the tenant id mixed into the hashed input</b>,     /// not one key per tenant. That yields the property that matters (the same address hashes
    /// differently in two tenants, so a profile key from one tenant means nothing in another)
    /// without per-tenant key management. Per-tenant keys are a future option if isolation
    /// requirements harden, and changing this construction is a <b>migration</b>: every stored
    /// profile key becomes unreadable.
    /// </para>
    /// </remarks>
    public const string ProfileKeyEnvironmentVariable = "STYLOMAIL_PROFILE_KEY";

    /// <summary>
    /// Environment variable holding the secret the Cloudflare Email Routing Worker presents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A secret this deployment generates, not a provider credential.</b> Nothing it unlocks can
    /// read a mailbox or act on a Cloudflare account, compromising it lets an attacker submit mail,
    /// which is what the recipient-domain check already constrains. That is the whole reason this
    /// connector was chosen: the deployment still holds zero <em>provider</em> secrets.
    /// </para>
    /// <para>
    /// It is still a secret, and the name is part of the privilege model, which is why it is defined
    /// here beside the others rather than invented at the call site. Environment-only, like the other
    /// two: a value in a config key ends up in an appsettings file, then a repository, then an image
    /// layer.
    /// </para>
    /// </remarks>
    public const string CloudflareIngressSecretEnvironmentVariable = "STYLOMAIL_CF_INGRESS_SECRET";

    /// <summary>Reads the Cloudflare ingress secret from the environment, or null when unset.</summary>
    public static string? CloudflareIngressSecretFromEnvironment() =>
        Environment.GetEnvironmentVariable(CloudflareIngressSecretEnvironmentVariable);

    /// <summary>
    /// Decides whether an enabled Cloudflare intake may run. Throws when it may not.
    /// </summary>
    /// <remarks>
    /// <b>Enabled without a secret is refused at startup rather than serving 401s.</b> A route that
    /// exists and always refuses looks like a misconfigured Worker, so the operator goes and checks
    /// the Worker, while the actual fault is here. Failing to start names the variable instead.
    ///
    /// <para>
    /// A <em>disabled</em> connector needs no secret: a deployment that has not opted into this
    /// intake holds nothing for it. Kept as a pure function of its two arguments so the decision is
    /// testable without mutating process environment, which would race against every other test in
    /// the suite, the same reason <see cref="Resolve"/> is shaped this way.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Enabled, and no secret is configured.</exception>
    public static void RequireCloudflareIngressSecretIfEnabled(string? secret, bool enabled)
    {
        if (!enabled || !string.IsNullOrWhiteSpace(secret))
        {
            return;
        }

        throw new InvalidOperationException(
            $"The Cloudflare Email Routing intake is enabled but " +
            $"{CloudflareIngressSecretEnvironmentVariable} is not set. The Worker's shared secret is " +
            "the only thing standing between this route and an open mail injection endpoint, so " +
            "refusing to start is the only answer that cannot be mistaken for a working deployment.");
    }

    /// <summary>
    /// Decides what a pair of secret values means for this deployment.
    /// </summary>
    /// <remarks>
    /// Kept as a pure function of the two values so the decision is testable without mutating
    /// process environment, which would race against every other test in the suite.
    ///
    /// <para>
    /// The middle case is the one worth stating: <b>exactly one secret present is a
    /// misconfiguration and refuses to start.</b> Falling back to the unconfigured sentinel there
    /// would leave a deployment that believes it is assessing mail while every assessment returns
    /// 503, an outage that looks like an outage, which is at least honest, but the worse reading
    /// is a half-configured deployment where the Jev key is live and the pseudonymisation key is
    /// not. Refusing at startup is the only answer that cannot be mistaken for healthy.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// One secret is present and the other is not, or the master key is too short to be worth
    /// anything.
    /// </exception>
    public static CredentialState Resolve(string? jevApiKey, string? profileMasterKey)
    {
        var hasJev = !string.IsNullOrWhiteSpace(jevApiKey);
        var hasProfile = !string.IsNullOrWhiteSpace(profileMasterKey);

        if (!hasJev && !hasProfile)
        {
            return CredentialState.NotConfigured;
        }

        if (!hasJev)
        {
            throw new InvalidOperationException(
                $"{ProfileKeyEnvironmentVariable} is set but {JevOptions.ApiKeyEnvironmentVariable} is not. " +
                "Assessment is half-configured; refusing to start rather than running with one secret.");
        }

        if (!hasProfile)
        {
            throw new InvalidOperationException(
                $"{JevOptions.ApiKeyEnvironmentVariable} is set but {ProfileKeyEnvironmentVariable} is not. " +
                "Assessment is half-configured; refusing to start rather than running with one secret.");
        }

        // Length is checked here as well as inside ProfileKeyHasher so the failure names the
        // environment variable the operator has to fix, rather than surfacing as a constructor
        // argument error from three layers down.
        var keyBytes = Encoding.UTF8.GetByteCount(profileMasterKey!);
        if (keyBytes < MinimalMasterKeyBytes)
        {
            // Deliberately reports the length and not the value.
            throw new InvalidOperationException(
                $"{ProfileKeyEnvironmentVariable} is {keyBytes} bytes; at least {MinimalMasterKeyBytes} " +
                "are required. A short key is brute-forceable, and a brute-forced key undoes the " +
                "pseudonym it was protecting.");
        }

        return CredentialState.Configured;
    }

    /// <summary>Mirrors <c>ProfileKeyHasher.MinimumKeyBytes</c>; asserted equal by a test.</summary>
    public const int MinimalMasterKeyBytes = 32;

    /// <summary>Reads both secrets from the environment and applies <see cref="Resolve"/>.</summary>
    public static CredentialState ResolveFromEnvironment(
        out string? jevApiKey,
        out string? profileMasterKey)
    {
        jevApiKey = Environment.GetEnvironmentVariable(JevOptions.ApiKeyEnvironmentVariable);
        profileMasterKey = Environment.GetEnvironmentVariable(ProfileKeyEnvironmentVariable);

        return Resolve(jevApiKey, profileMasterKey);
    }
}
