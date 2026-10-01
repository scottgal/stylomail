using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StyloMail.Assessment;
using StyloMail.Host.Assessors;
using StyloMail.Host.Hosting;
using StyloMail.Host.Observability;
using StyloMail.Nimble;

namespace StyloMail.Host.Tests;

/// <summary>
/// Which semantic provider a deployment selected, and what that selection requires of it.
/// </summary>
/// <remarks>
/// A pure decision over configuration, so it is asserted directly. The one thing this must not do is
/// guess: an unrecognised value names no provider, and a host that fell back to a default would
/// assess a deployment's mail with a provider nobody chose.
/// </remarks>
public sealed class AssessmentProviderSelectionTests
{
    [Fact]
    public void The_hosted_provider_is_the_default_when_nothing_is_configured()
    {
        // Every deployment that predates this choice is running the hosted provider, so a default
        // that moved them elsewhere would change where their mail goes without telling them.
        Assert.Equal(AssessmentProvider.Jev, AssessmentProviderSelection.Select(Configuration()));
    }

    [Fact]
    public void The_local_provider_is_selected_by_name()
    {
        var provider = AssessmentProviderSelection.Select(
            Configuration((AssessmentProviderSelection.ConfigurationKey, "Nimble")));

        Assert.Equal(AssessmentProvider.Nimble, provider);
    }

    [Fact]
    public void The_name_is_matched_without_regard_to_case()
    {
        // An operator writing "nimble" has named the provider unambiguously, and refusing over
        // letter case would be pedantry with a startup failure attached.
        var provider = AssessmentProviderSelection.Select(
            Configuration((AssessmentProviderSelection.ConfigurationKey, "nimble")));

        Assert.Equal(AssessmentProvider.Nimble, provider);
    }

    [Fact]
    public void An_unknown_provider_refuses_to_start_and_names_what_it_would_accept()
    {
        // The refusal, not a fallback. A typo that quietly selected the hosted provider would send
        // the mail to a third party while the operator believed the local model was reading it.
        var thrown = Assert.Throws<InvalidOperationException>(() => AssessmentProviderSelection.Select(
            Configuration((AssessmentProviderSelection.ConfigurationKey, "Zeta"))));

        Assert.Contains(AssessmentProviderSelection.ConfigurationKey, thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Zeta", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(AssessmentProvider.Nimble), thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_number_is_not_a_provider_name()
    {
        // "1" is how the second member is numbered, and accepting it would mean a deployment ran the
        // local provider because someone typed a digit. Only the names are accepted.
        Assert.Throws<InvalidOperationException>(() => AssessmentProviderSelection.Select(
            Configuration((AssessmentProviderSelection.ConfigurationKey, "1"))));
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}

/// <summary>
/// What a pair of secrets means, given the provider the deployment selected.
/// </summary>
/// <remarks>
/// <b>The point of these is the asymmetry.</b> A lone profile master key is the half-configured
/// refusal under the hosted provider and a complete deployment under the local one, because the
/// local provider holds no credential and pairs with nothing. Requiring a key it never reads would
/// make the local provider impossible to configure, and the operator's only repair would be to
/// obtain a credential for a provider they are not using.
/// </remarks>
public sealed class ProviderCredentialResolutionTests
{
    /// <summary>A 32-byte fixture, not a secret: it pseudonymises identifiers inside this process only.</summary>
    private const string MasterKey = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void The_local_provider_is_configured_by_the_master_key_alone()
    {
        var state = HostCredentials.ResolveForProvider(AssessmentProvider.Nimble, null, MasterKey);

        Assert.Equal(CredentialState.Configured, state);
    }

    [Fact]
    public void The_local_provider_with_no_secrets_is_unconfigured_rather_than_misconfigured()
    {
        // Absent, not half-configured: with no local credential to pair with, a missing master key
        // is the whole deployment unconfigured. The host starts on the refusing assessor and
        // /health/ready names it, so this is visible as not-ready rather than passing for healthy.
        var state = HostCredentials.ResolveForProvider(AssessmentProvider.Nimble, null, null);

        Assert.Equal(CredentialState.NotConfigured, state);
    }

    [Fact]
    public void A_provider_key_the_local_provider_never_reads_is_not_a_reason_to_refuse()
    {
        // A deployment may carry a key it is not using, and refusing to start over a secret nothing
        // reads would hand its availability to someone else's configuration. The composition root
        // says the key is inert instead.
        var state = HostCredentials.ResolveForProvider(AssessmentProvider.Nimble, "an-unused-provider-key", MasterKey);

        Assert.Equal(CredentialState.Configured, state);
    }

    [Fact]
    public void The_local_provider_refuses_a_master_key_too_short_to_be_worth_anything()
    {
        // The one requirement that is not about the provider at all: without a real master key the
        // profiles are keyed on identifiers rather than pseudonyms, and no provider choice makes
        // that acceptable.
        var thrown = Assert.Throws<InvalidOperationException>(() => HostCredentials.ResolveForProvider(
            AssessmentProvider.Nimble,
            null,
            "too-short-a-key"));

        Assert.Contains(HostCredentials.ProfileKeyEnvironmentVariable, thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("too-short-a-key", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hosted_provider_delegates_to_the_rules_it_always_had()
    {
        // Asserted here because the hosted path must not be re-implemented for the sake of the
        // provider check: the same pair of values has to mean the same thing it meant before, so
        // that adding a second provider cannot quietly loosen the first one.
        Assert.Equal(
            CredentialState.Configured,
            HostCredentials.ResolveForProvider(AssessmentProvider.Jev, "a-provider-key", MasterKey));

        Assert.Equal(
            CredentialState.NotConfigured,
            HostCredentials.ResolveForProvider(AssessmentProvider.Jev, null, null));

        Assert.Throws<InvalidOperationException>(
            () => HostCredentials.ResolveForProvider(AssessmentProvider.Jev, null, MasterKey));
    }
}

/// <summary>
/// Binding the local provider's endpoint and model from configuration.
/// </summary>
/// <remarks>
/// The endpoint is the setting that can undo the reason this provider was selected, so a non-loopback
/// one is announced at startup. The rest of the reasoning is on the method itself.
/// </remarks>
public sealed class NimbleOptionsBindingTests
{
    [Fact]
    public void The_endpoint_and_model_default_to_the_measured_values()
    {
        var options = HostServices.BuildNimbleOptions(Configuration(), new CapturingLogger());

        var defaults = new NimbleOptions();

        Assert.Equal(defaults.Endpoint, options.Endpoint);
        Assert.Equal(defaults.Model, options.Model);
    }

    [Fact]
    public void A_configured_endpoint_and_model_are_honoured()
    {
        var options = HostServices.BuildNimbleOptions(
            Configuration(
                ("StyloMail:Nimble:Endpoint", "http://127.0.0.1:9/api/generate"),
                ("StyloMail:Nimble:Model", "nimble:0.0.0-local")),
            new CapturingLogger());

        Assert.Equal("http://127.0.0.1:9/api/generate", options.Endpoint);
        Assert.Equal("nimble:0.0.0-local", options.Model);
    }

    [Theory]
    [InlineData("http://127.0.0.1:11435/api/generate")]
    [InlineData("http://localhost:11435/api/generate")]
    [InlineData("http://[::1]:11435/api/generate")]
    public void A_local_endpoint_is_not_announced(string endpoint)
    {
        // Every spelling of this machine is local, and a warning on every boot is a warning nobody
        // reads: it would bury the case that matters.
        var logger = new CapturingLogger();

        HostServices.BuildNimbleOptions(Configuration(("StyloMail:Nimble:Endpoint", endpoint)), logger);

        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void An_endpoint_that_leaves_this_machine_is_announced_at_startup()
    {
        // Staying local is the property this provider was chosen for. An endpoint elsewhere gives it
        // up while the assessments keep looking right, which is why it has to be said out loud.
        var logger = new CapturingLogger();

        HostServices.BuildNimbleOptions(
            Configuration(("StyloMail:Nimble:Endpoint", "http://192.0.2.10:11435/api/generate")),
            logger);

        var warning = Assert.Single(logger.Warnings);

        Assert.Contains("NOT LOOPBACK", warning, StringComparison.Ordinal);
        Assert.Contains("http://192.0.2.10:11435/api/generate", warning, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}

/// <summary>
/// Selecting the local provider, through the real composition and over real HTTP.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are the tests that make the selection mean something.</b> The unit tests above prove the
/// decision and the binding; none of them proves that anything acts on the decision, and a selection
/// nothing reads is a config key that appears to work and does nothing.
/// </para>
/// <para>
/// The endpoint is a loopback address that refuses, so a connection attempt fails at once with no
/// DNS, no timeout and nothing sent. That is not a shortcut: it is the only way this suite can
/// exercise the local provider without depending on whether an Ollama server happens to be running
/// on the machine, which would make a green run mean something different on each developer's box. A
/// refused connection degrades to <c>Unavailable</c> evidence, exactly as a duplicate's would.
/// </para>
/// </remarks>
public sealed class NimbleProviderCompositionTests
{
    /// <summary>A 32-byte fixture, not a secret: it pseudonymises identifiers inside this process only.</summary>
    private const string MasterKey = "0123456789abcdef0123456789abcdef";

    /// <summary>Shaped like the real endpoint, refused before the path is ever reached.</summary>
    private const string RefusingLocalModel = "http://127.0.0.1:1/api/generate";

    /// <summary>The two states a semantic row can be in when the provider could not answer.</summary>
    private static readonly string[] UnansweredStates = ["Unavailable", "NotApplicable"];

    [Fact]
    public async Task The_local_provider_composes_an_assessor_with_no_provider_key_at_all()
    {
        // The half of the contract that is new: this deployment has no provider credential, which
        // under the hosted provider is the half-configured refusal. The assessment answering at all
        // is the proof that a real assessor was composed, since the refusing one answers 503 with an
        // error rather than a decision with evidence.
        using var host = new TestHost()
            .WithAssessmentSecrets(jevApiKey: null, profileMasterKey: MasterKey)
            .Configure("StyloMail:Assessment:Provider", "Nimble")
            .Configure("StyloMail:Nimble:Endpoint", RefusingLocalModel);

        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var response = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        var evidence = document.RootElement.GetProperty("evidence").EnumerateArray().ToList();

        Assert.True(evidence.Count > 0, $"Expected a decision carrying evidence. Body was: {body}");

        // And the provider really was asked: dimensions it could ask about come back Unavailable
        // because the endpoint refused, rather than being absent or silently scored. A decision with
        // no semantic rows at all would mean the classifier was never composed into the pipeline.
        var semantic = evidence
            .Where(row => row.GetProperty("origin").GetString() == "Semantic")
            .ToList();

        Assert.True(semantic.Count > 0, $"Expected semantic rows to be present. Body was: {body}");

        var availability = semantic
            .Select(row => row.GetProperty("availability").GetString() ?? string.Empty)
            .ToList();

        // Not every semantic row is Unavailable, and the exception is the point rather than noise:
        // the local provider reports a dimension as NotApplicable when the message cannot answer it
        // at all, which for a message with no conversation context is conversational continuity.
        // That is a fact about the message, not about the provider, and it stays NotApplicable
        // whether the endpoint is up or down.
        Assert.True(
            availability.Contains("Unavailable"),
            $"Expected the refused endpoint to leave at least one semantic row Unavailable. Body was: {body}");

        // Nothing was scored: an "Available" row would mean a value was produced for a dimension the
        // provider was never able to answer, which is the one outcome this test exists to rule out.
        Assert.DoesNotContain("Available", availability);
        Assert.All(availability, value => Assert.Contains(value, UnansweredStates));

        // The assessor being real is also what readiness is asking about, so the two routes cannot
        // disagree about it.
        using var anonymous = host.Anonymous();

        var ready = await anonymous.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    /// <summary>
    /// The action, not only the rows: a local model that is down defers every message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gap this closes is inference, not safety.</b> The test above proves the rows are
    /// <c>Unavailable</c> and readiness is <c>ready</c>, and `MailAssessor`'s outage guard is what
    /// turns an entirely-unavailable semantic layer into <c>Defer</c>. Until this test, nothing
    /// asserted that the guard actually fires under the local provider, so "a Nimble deployment whose
    /// model is down defers rather than allows" was read off the predicate rather than measured.
    /// </para>
    /// <para>
    /// <b>The row count here is not the point, and neither is the count of reasons.</b> What is
    /// asserted is that the action is <c>Defer</c>, that the reason is
    /// <c>assessment.semantic_unavailable</c>, and that the reason names exactly the signal ids this
    /// same response reports as <c>Unavailable</c>. A reason that named everything, or nothing, would
    /// pass a mere presence check and fail this one.
    /// </para>
    /// <para>
    /// The guard's condition and its default are deliberately untouched. A test that needed either
    /// changed to pass would be a test of the change rather than of the behaviour.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_refused_local_model_defers_every_message_and_names_what_it_could_not_ask()
    {
        using var host = new TestHost()
            .WithAssessmentSecrets(jevApiKey: null, profileMasterKey: MasterKey)
            .Configure("StyloMail:Assessment:Provider", "Nimble")
            .Configure("StyloMail:Nimble:Endpoint", RefusingLocalModel);

        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var response = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        // A decision was produced, rather than the refusal a missing assessor gives. Without this the
        // assertions below could pass on an error body that happened to parse.
        var action = root.GetProperty("action").GetString();
        Assert.Equal("Defer", action);

        var outage = root.GetProperty("reasons")
            .EnumerateArray()
            .SingleOrDefault(reason => reason.GetProperty("code").GetString()
                == AssessmentReasonCodes.SemanticUnavailable);

        Assert.True(
            outage.ValueKind == JsonValueKind.Object,
            $"Expected reason '{AssessmentReasonCodes.SemanticUnavailable}' on a deferred decision. "
            + $"Body was: {body}");

        // Exactly the semantic rows this response itself reports as Unavailable, no more and no
        // fewer.
        //
        // The origin filter is load bearing and was learned by running it: other producers report
        // Unavailable too. On this fixture the behavioural and base-pipeline rows are Unavailable
        // for their own reasons (no profile history, nothing deterministic to extract), and they
        // are deliberately NOT named here, because this reason is about the provider that could
        // not answer. Widening it to every Unavailable row would blame the local model for a
        // missing profile and send a reader to the wrong subsystem.
        //
        // The NotApplicable rows are absent by design as well: nothing was asked of the provider
        // about them, so they are not part of what was lost, and their absence is what keeps a
        // not-applicable dimension from being reported as an outage.
        var unavailable = root.GetProperty("evidence")
            .EnumerateArray()
            .Where(row => row.GetProperty("origin").GetString() == "Semantic")
            .Where(row => row.GetProperty("availability").GetString() == "Unavailable")
            .Select(row => row.GetProperty("signalId").GetString() ?? string.Empty)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var named = outage.GetProperty("evidenceSignalIds")
            .EnumerateArray()
            .Select(id => id.GetString() ?? string.Empty)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(unavailable);
        Assert.Equal(unavailable, named);

        // And they are the semantic dimensions, not a mix of producers: the guard is about the
        // provider that could not answer, so naming anything else would send a reader to the wrong
        // subsystem.
        Assert.All(named, id => Assert.StartsWith("semantic.", id, StringComparison.Ordinal));

        // The reason explains itself rather than only naming a code, since an operator reading the
        // ledger sees the message first and a bare code would leave them to look it up.
        Assert.False(string.IsNullOrWhiteSpace(outage.GetProperty("message").GetString()));
    }

    [Fact]
    public void The_same_secrets_under_the_hosted_provider_still_refuse_to_build()
    {
        // The other half, and the reason the first one is worth anything: the required-secret set
        // follows the provider and nothing else. These are the identical secrets, under the default
        // provider, and this deployment is the half-configured one that must not start.
        using var host = new TestHost().WithAssessmentSecrets(jevApiKey: null, profileMasterKey: MasterKey);

        var thrown = Assert.Throws<InvalidOperationException>(
            () => host.Services.GetRequiredService<StyloMail.Core.IMailAssessor>());

        Assert.Contains("half-configured", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A local deployment holding a provider key it cannot pair with starts and says so, rather than
    /// refusing to start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The documented asymmetry, asserted on the pair the hosted provider refuses.</b> A provider
    /// key with no master key is the half-configured deployment that must not start under the hosted
    /// provider, because its adapter would run against a key that pairs with nothing. Locally there
    /// is no credential to pair with, so the same pair means the deployment is unconfigured rather
    /// than misconfigured, and the honest answer is a host that starts, warns, and stops advertising
    /// itself as ready.
    /// </para>
    /// <para>
    /// <b>The fixture is deliberately not "neither secret".</b> That pair is unconfigured under both
    /// providers, so a test using it would pass even if the local path inherited the hosted
    /// provider's refusal, which is exactly the fault worth catching. This pair distinguishes them,
    /// and swapping <c>ResolveForProvider</c> for <c>Resolve</c> in the composition turns this test
    /// red.
    /// </para>
    /// <para>
    /// <b>Resolving the assessor is the assertion that it did not refuse.</b> A throw from
    /// <c>GetRequiredService</c> here would mean an operator's only repair was to obtain a credential
    /// for a provider they are not using.
    /// </para>
    /// <para>
    /// Readiness is asserted as exactly one failed check for the same reason the neighbouring health
    /// test gives: any other name appearing here would mean the 503 had a cause other than the
    /// missing assessor, and the assertion would have passed over it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_local_provider_with_an_unpairing_key_starts_not_ready_rather_than_refusing()
    {
        using var host = new TestHost()
            .WithAssessmentSecrets(jevApiKey: "an-unused-provider-key", profileMasterKey: null)
            .Configure("StyloMail:Assessment:Provider", "Nimble");

        Assert.IsType<UnavailableMailAssessor>(
            host.Services.GetRequiredService<StyloMail.Core.IMailAssessor>());

        using var client = host.Anonymous();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(
            new[] { ReadinessProbe.AssessorUnavailable },
            body.RootElement.GetProperty("failedChecks").EnumerateArray().Select(e => e.GetString()));
    }
}
