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
/// One member names the absence of a provider altogether (<see cref="NeverAsks"/>). That is a
/// deployment's decision about itself rather than a report about a provider, and it is listed here
/// because it decides the same two things the other members do: which semantic tier is constructed,
/// and what the deployment must be configured with before it may start.
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

    /// <summary>
    /// No semantic provider at all: this deployment declares that it never asks one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A declaration about the deployment, never a reading of a provider's health.</b> Selecting
    /// it says that no question will be put to a semantic provider for any message, ever, and the
    /// semantic tier answers every dimension <c>NotApplicable</c>: the question exists, it was not
    /// asked, so nothing was lost. That is the fact a deployment needs in order to allow on local
    /// evidence with a clean semantic column, and it is why this exists as a named selection rather
    /// than as a consequence of the provider being unreachable: <b>an outage produces
    /// <c>Unavailable</c> rows and this produces <c>NeverAsks</c> rows, and policy must be able to
    /// tell a deployment that never asks from one that asked and heard nothing.</b> A blackout must
    /// never be convertible into an allow by configuration, in either direction.
    /// </para>
    /// <para>
    /// <b>It must be chosen explicitly, and it is not the default.</b> The default stays
    /// <see cref="Jev"/>, so a deployment that names nothing and holds no key is the half-configured
    /// refusal it already was rather than a deployment quietly running without semantic evidence. A
    /// forgotten secret must never be able to become this selection, which is the whole reason this
    /// is a provider name an operator writes down and not something inferred from an absent key.
    /// </para>
    /// <para>
    /// It holds no credential, so like <see cref="Nimble"/> it requires the profile master key and
    /// nothing else. A provider key left set is announced at startup rather than refused: a
    /// deployment may carry a secret it is not using, and refusing to start over it would hand the
    /// deployment's availability to someone else's configuration.
    /// </para>
    /// </remarks>
    NeverAsks,

    /// <summary>
    /// Both models, in order: a local decision model first, and a second opinion only where a
    /// deterministic rule says the local answer cannot be taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a third adapter: a composition of the two that already exist.</b> The local arm is the
    /// one <see cref="Nimble"/> constructs, the second arm is the one <see cref="Jev"/> constructs,
    /// and the cascade is a decorator over the provider port that asks the second only for the
    /// dimensions the rule names. So everything the two members above say about their adapters is
    /// true here of one arm each, and the cascade adds exactly one thing: which arm answered a given
    /// row, recorded on that row.
    /// </para>
    /// <para>
    /// <b>It needs the hosted provider's secrets, and that is the point rather than an
    /// inconvenience.</b> A cascade whose second arm holds no credential is not a cascade; it is the
    /// local provider with a rule that can never be satisfied, so it is refused at startup exactly as
    /// <see cref="Jev"/> is, naming the missing variable. The failure this prevents is a deployment
    /// that selected a cascade, lost its key, and went on assessing every message on the local arm
    /// while its evidence looked complete.
    /// </para>
    /// <para>
    /// <b>The default stays <see cref="Jev"/>.</b> A deployment that has asked for nothing keeps the
    /// behaviour it had, and no existing deployment is moved onto a composition it did not choose.
    /// </para>
    /// </remarks>
    /// <seealso cref="StyloMail.Cascade.CascadeEscalationRule"/>
    Cascade,
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
