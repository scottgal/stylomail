using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StyloMail.Host.Hosting;
using StyloMail.Jev;
using StyloMail.Nimble;

namespace StyloMail.Host.Tests;

/// <summary>
/// The cascade as a deployment choice: which provider a host that selected it gets, what that
/// selection requires of the deployment, and what a caller can see on a decision.
/// </summary>
/// <remarks>
/// <para>
/// <b>A composition is only real if a host can reach it.</b> The component's own suite proves the
/// rule; nothing in it proves that a deployment can select the composition, that the selection
/// refuses the right things, or that an escalated row is legible from outside the process. Those are
/// the three things this file asserts.
/// </para>
/// <para>
/// The two ends of the selection are asserted together, on purpose: a test that only proved the
/// cascade is selected when configured would pass just as well if the cascade had become the DEFAULT,
/// which would move every existing deployment onto a composition it never chose.
/// </para>
/// </remarks>
public sealed class CascadeWiringTests
{
    private const string MasterKey = "0123456789abcdef0123456789abcdef";
    private const string ApiKeyFixture = "a-placeholder-key-that-never-leaves-this-process";

    [Fact]
    public void The_cascade_is_selected_by_configuration()
    {
        var provider = AssessmentProviderSelection.Select(
            Configuration((AssessmentProviderSelection.ConfigurationKey, "Cascade")));

        Assert.Equal(AssessmentProvider.Cascade, provider);
    }

    [Fact]
    public void A_deployment_that_selects_nothing_still_gets_the_hosted_provider()
    {
        // The other half of the arm above, and the one that protects every deployment that predates
        // the cascade: adding a composition must not move anybody onto it.
        Assert.Equal(AssessmentProvider.Jev, AssessmentProviderSelection.Select(Configuration()));
    }

    [Fact]
    public void An_unrecognised_name_lists_the_cascade_among_what_it_would_accept()
    {
        // The refusal already names every member, so this asserts that the cascade is reachable BY
        // NAME from an operator's side rather than only by a code path: a provider that is in the
        // enum but missing from this message is a provider whose typo sends the operator to the wrong
        // repair.
        var thrown = Assert.Throws<InvalidOperationException>(() => AssessmentProviderSelection.Select(
            Configuration((AssessmentProviderSelection.ConfigurationKey, "Kascade"))));

        Assert.Contains(nameof(AssessmentProvider.Cascade), thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cascade_requires_the_hosted_providers_credential_pair()
    {
        // All-or-nothing, the same shape the hosted provider has. The two rows that carry the claim
        // are the middle ones, and they are the reason this is not a copy of the local provider's
        // rule: a cascade holding the master key ALONE is half-configured and refuses to start,
        // because a cascade whose second arm holds no credential is the local provider with a rule
        // that can never be satisfied. That is the opposite of `Nimble`, where the master key alone
        // is the normal shape, and asserting both here is what keeps the two from being conflated.
        Assert.Equal(
            CredentialState.Configured,
            HostCredentials.ResolveForProvider(AssessmentProvider.Cascade, ApiKeyFixture, MasterKey));

        var masterOnly = Assert.Throws<InvalidOperationException>(() => HostCredentials.ResolveForProvider(
            AssessmentProvider.Cascade,
            null,
            MasterKey));

        Assert.Contains(JevOptions.ApiKeyEnvironmentVariable, masterOnly.Message, StringComparison.Ordinal);

        var keyOnly = Assert.Throws<InvalidOperationException>(() => HostCredentials.ResolveForProvider(
            AssessmentProvider.Cascade,
            ApiKeyFixture,
            null));

        Assert.Contains(HostCredentials.ProfileKeyEnvironmentVariable, keyOnly.Message, StringComparison.Ordinal);

        // And neither, which is the missing-configuration case rather than a refusal: the host starts
        // on the refusing assessor and /health/ready names it, so nothing about the state passes for
        // healthy. The two are different answers on purpose and the host reports them differently.
        Assert.Equal(
            CredentialState.NotConfigured,
            HostCredentials.ResolveForProvider(AssessmentProvider.Cascade, null, null));
    }

    [Fact]
    public void The_reported_classifier_version_names_each_arms_model_and_each_arms_host()
    {
        var version = HostServices.CascadeClassifierVersion(
            new NimbleOptions { Model = "nimble:latest", Endpoint = "http://127.0.0.1:11435/v1/systemone" },
            new JevOptions { Model = "jev-1.13.0", Endpoint = "https://api.typesafe.ai/v1/systemone" });

        // Both models, so a row's producer cannot be confused for the other arm's.
        Assert.Contains("nimble:latest", version, StringComparison.Ordinal);
        Assert.Contains("jev-1.13.0", version, StringComparison.Ordinal);

        // And both HOSTS, which is the term the cache depends on: the same model at the same digest
        // on two hosts answers differently, so a version naming models alone would serve an entry
        // taken under a replaced host. The authority rather than the whole URL, so a path or scheme
        // change does not fragment an entry while a different host does not share one.
        Assert.Contains("127.0.0.1:11435", version, StringComparison.Ordinal);
        Assert.Contains("api.typesafe.ai", version, StringComparison.Ordinal);
    }

    [Fact]
    public void The_classifier_version_moves_when_a_host_moves_and_not_when_a_path_does()
    {
        var arms = (
            Local: new NimbleOptions { Model = "nimble:latest", Endpoint = "http://127.0.0.1:11435/v1/systemone" },
            Second: new JevOptions { Model = "jev-1.13.0", Endpoint = "https://api.typesafe.ai/v1/systemone" });

        var baseline = HostServices.CascadeClassifierVersion(arms.Local, arms.Second);

        var movedHost = HostServices.CascadeClassifierVersion(
            new NimbleOptions { Model = "nimble:latest", Endpoint = "http://192.0.2.15:11434/v1/systemone" },
            arms.Second);

        var movedPath = HostServices.CascadeClassifierVersion(
            new NimbleOptions { Model = "nimble:latest", Endpoint = "http://127.0.0.1:11435/v2/systemone" },
            arms.Second);

        // The direction that matters is the first one: a version that did NOT move here would key the
        // cache on an entry produced by a different machine. The second asserts the deliberate
        // narrowness: the authority is what identifies a server, not the route it was asked on.
        Assert.NotEqual(baseline, movedHost);
        Assert.Equal(baseline, movedPath);
    }

    [Fact]
    public async Task A_cascade_deployment_publishes_which_condition_escalated_each_row()
    {
        // Through the real pipeline, the real decision projection and real HTTP. Both arms are
        // unreachable by design, which is the state that must escalate rather than pass through: the
        // local answer is UNKNOWN, so every askable dimension goes to the second arm, and the second
        // arm answers nothing either.
        using var host = new TestHost().WithRealCascadeAssessor();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var response = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(body);

        var semanticRows = Evidence(document)
            .Where(row => row.GetProperty("signalId").GetString()?.StartsWith("semantic.", StringComparison.Ordinal) == true)
            .ToList();

        // The control for everything below: an empty set would make each assertion pass for the wrong
        // reason, which is the failure mode a negative assertion has by default.
        Assert.NotEmpty(semanticRows);

        // A dimension that could not be asked at all is NOT escalated, and this is the row that
        // proves it: conversational continuity is NotApplicable without a conversation window, so it
        // carries no escalation site even here.
        var notApplicable = semanticRows
            .Where(row => row.GetProperty("availability").GetString() == "NotApplicable")
            .ToList();

        Assert.All(notApplicable, row => Assert.Null(CascadeEscalationOf(row)));

        var escalated = semanticRows
            .Where(row => row.GetProperty("availability").GetString() == "Unavailable")
            .ToList();

        Assert.NotEmpty(escalated);

        // Which condition asked, and whether the ask was answered. Both arms refused, so the local
        // reason is the first and the failed second opinion is recorded beside it: a reader can tell
        // "we asked and heard nothing" from "we did not ask", which is the whole point of the site.
        Assert.All(escalated, row => Assert.Equal(
            "local_unavailable;hosted:unavailable",
            CascadeEscalationOf(row)));
    }

    [Fact]
    public async Task A_deployment_that_did_not_select_the_cascade_publishes_no_escalation()
    {
        // The other side of the same arm. The hosted provider path is unchanged by this wiring, and
        // its rows must not grow a cascade site they did not earn.
        using var host = new TestHost().WithRealAssessor();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var response = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(body);

        var semanticRows = Evidence(document)
            .Where(row => row.GetProperty("signalId").GetString()?.StartsWith("semantic.", StringComparison.Ordinal) == true)
            .ToList();

        Assert.NotEmpty(semanticRows);
        Assert.All(semanticRows, row => Assert.Null(CascadeEscalationOf(row)));
    }

    private static IEnumerable<JsonElement> Evidence(JsonDocument decision)
        => decision.RootElement.GetProperty("evidence").EnumerateArray();

    /// <summary>The member this file is about, read as a string or null when it is absent or null.</summary>
    private static string? CascadeEscalationOf(JsonElement row)
        => row.TryGetProperty("cascadeEscalation", out var escalation) && escalation.ValueKind == JsonValueKind.String
            ? escalation.GetString()
            : null;

    private static IConfiguration Configuration(params (string Key, string? Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(setting => setting.Key, setting => setting.Value))
            .Build();
}
