namespace StyloMail.Host.Hosting;

/// <summary>
/// Which semantic provider this deployment assesses mail with.
/// </summary>
/// <remarks>
/// <para>
/// A closed set rather than a loose string, because the choice is not merely which adapter to
/// construct: <b>it decides what the deployment must be configured with before it can start.</b> The
/// hosted provider holds a credential and refuses to run without one; the local provider has no
/// credential to hold and treats the absence of one as the normal shape. An operator who selects the
/// local model and is then told a provider key is missing would be told to fix something that does
/// not exist, and an operator who selects the hosted model and finds no key required would be
/// running half-configured while every surface said healthy.
/// </para>
/// <para>
/// It is also a <em>locality</em> decision. The hosted provider sends message content to a third
/// party by definition; the local one sends it to an endpoint that defaults to this machine. Which
/// of those a deployment intends is not something to discover from a log after the mail has gone.
/// </para>
/// </remarks>
public enum AssessmentProvider
{
    /// <summary>
    /// The hosted Jev provider: HTTP, credentialed, and the default.
    /// </summary>
    /// <remarks>
    /// The default because it is what every deployment configured before this choice existed is
    /// already running, and a default that silently moved existing deployments to a provider they
    /// never selected would change where their mail goes without telling them.
    /// </remarks>
    Jev,

    /// <summary>
    /// The local decision model served by Ollama on this machine.
    /// </summary>
    /// <remarks>
    /// No credential, so its selection is what makes "profile master key present, provider key
    /// absent" a supported deployment rather than the half-configured refusal it is under
    /// <see cref="Jev"/>.
    /// </remarks>
    Nimble,
}

/// <summary>
/// Reads the provider decision out of configuration.
/// </summary>
/// <remarks>
/// <b>Selection is configuration; a secret is not.</b> The key that names the provider is a config
/// key, because naming a provider reveals nothing: it is a choice between two known adapters, and
/// putting it in configuration is what lets the composition be stated, inspected and tested rather
/// than inferred from whichever secret happens to be set in the process environment. The secrets
/// themselves stay where they are, in the environment, read by <see cref="HostCredentials"/> and
/// never by configuration. A config key is a place secrets must never go: it ends up in an
/// appsettings file, then in a repository, then in an image layer.
/// </remarks>
public static class AssessmentProviderSelection
{
    /// <summary>The configuration key that names the provider.</summary>
    public const string ConfigurationKey = "StyloMail:Assessment:Provider";

    /// <summary>
    /// The provider this deployment selected, defaulting to <see cref="AssessmentProvider.Jev"/>.
    /// </summary>
    /// <remarks>
    /// Public for the same reason <see cref="HostServices.BuildJevOptions"/> and
    /// <c>HostServices.DescribeTransport</c> are: this is a composition decision a test should be
    /// able to assert on directly rather than by inferring it from whose endpoint was called.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The key is set to a value that names no provider. Deliberately a refusal rather than a
    /// fallback to the default: an unrecognised value is a typo, and a host that quietly fell back
    /// to the hosted provider would send a deployment's mail to a third party while its operator
    /// believed the local model was reading it. Configuration that appears to work and does nothing
    /// is worse than configuration that is absent.
    /// </exception>
    public static AssessmentProvider Select(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var value = configuration[ConfigurationKey];

        if (string.IsNullOrWhiteSpace(value))
        {
            return AssessmentProvider.Jev;
        }

        // Matched against the names only, deliberately. Enum.TryParse would also accept a numeric
        // string, so "1" would select the second member and a deployment would be running the local
        // provider because someone typed a digit. A provider is named, not numbered.
        var name = Enum.GetNames<AssessmentProvider>()
            .FirstOrDefault(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));

        if (name is not null)
        {
            return Enum.Parse<AssessmentProvider>(name);
        }

        throw new InvalidOperationException(
            $"{ConfigurationKey} is set to '{value}', which names no provider. Use one of: "
            + $"{string.Join(", ", Enum.GetNames<AssessmentProvider>())}.");
    }
}
