using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StyloMail.Core;

namespace StyloMail.Host.Tests;

/// <summary>
/// The two message routes are handed the same message and are expected to reach the same decision
/// about it.
/// </summary>
/// <remarks>
/// <para>
/// <c>POST /v1/submissions</c> and <c>POST /v1/assessments</c> share their ingress checks and take
/// the same request record, and the difference between them is meant to be what happens to the
/// message afterwards, not what is seen of it. They diverged on the second point: the submission
/// route spools the payload and the assessor reads it back through the envelope's reference, while
/// the assessment route writes nothing durable and passed a deliberately ephemeral reference that
/// resolves to nothing. The assessor therefore reached no bytes at all, extracted no deterministic
/// evidence, and the policy engine held every message whose risk index would otherwise have allowed
/// it, because a probabilistic negative may not authorise delivery when nothing checkable was
/// measured.
/// </para>
/// <para>
/// <b>The real assessor, because the fake produces no evidence to compare.</b> A comparison of
/// evidence sets is meaningless against a double that has none, so this runs the real pipeline with
/// the semantic provider unreachable, which is the same shape the rest of the suite uses: the
/// deterministic tier is produced for real and the semantic tier honestly reports itself
/// unavailable.
/// </para>
/// <para>
/// <b>What is compared, and why not the whole evidence list.</b> The deterministic rows are the
/// subject: they are what the bytes produce, and they are absent from the route without them. The
/// full lists can differ by one row that has nothing to do with either route,
/// <c>assessment.behavioural_context</c>, which is emitted when the sender has no behavioural
/// profile yet and therefore rides on whichever assessment happens to be the sender's first. That is
/// a property of the run rather than of the route, and asserting the whole list equal would make
/// this test red for a reason it is not about.
/// </para>
/// </remarks>
public sealed class AssessmentRouteAgreementTests
{
    private const string ExtractionUnavailable = "assessment.deterministic_extraction";

    /// <summary>
    /// The call order is a control, not a second opinion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test is red for one reason only if the routes disagree, and it has to be able to say so.
    /// The reproduction that surfaced the original divergence ran both orders, and the orders were not
    /// the variable: the agreement appeared in a submissions-first run and in an assessments-first run
    /// once the assessment route supplied the bytes, and the disagreement appeared in both orders
    /// before that. Left unpinned, a reader comparing two artifacts would see two shapes that differ
    /// and would have nothing in the suite telling them which differences are noise.
    /// </para>
    /// <para>
    /// So the order is a parameter and both cases assert the same property. A red here names the
    /// route, because the only thing that changed between the two cases is which call went first, and
    /// that is the property this test exists to be able to deny.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_same_message_assessed_and_submitted_produces_the_same_deterministic_evidence(
        bool submitFirst)
    {
        using var host = new TestHost().WithRealAssessor();
        using var sender = host.ClientAs(TestPrincipals.AcmeSenderKey);
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        // One request body, posted twice. The bytes are identical because the record is, which is
        // the only way this comparison says anything about the routes rather than about fixtures.
        var body = TestMessages.Request();

        string assessmentId;
        if (submitFirst)
        {
            await SubmitAsync(sender, body);
            assessmentId = await AssessAsync(sender, body);
        }
        else
        {
            assessmentId = await AssessAsync(sender, body);
            await SubmitAsync(sender, body);
        }

        // The submission route can refuse and still record its decision, and under the unreachable
        // provider this suite uses it does exactly that: a total semantic outage declines
        // responsibility rather than assuming it. So the decision is found in the ledger, and a Host
        // built for one test holds two of them, which is what makes the other one unambiguous.
        var decisions = await DecisionsAsync(reviewer);
        var submissionId = Assert.Single(
            decisions,
            id => id != assessmentId);

        using var submission = await DecisionAsync(reviewer, submissionId);
        using var assessment = await DecisionAsync(reviewer, assessmentId);

        var submitted = DeterministicSignalIds(submission);
        var assessed = DeterministicSignalIds(assessment);

        // Guards the comparison against going vacuous. If the bytes stop reaching the parser on both
        // routes, the two empty lists below would be equal and this test would pass while proving
        // nothing, which is the shape of the defect it exists to catch.
        Assert.NotEmpty(submitted);
        Assert.Equal(submitted, assessed);

        // And neither route reports that extraction did not run, which is the marker the assessment
        // route used to carry on its own and nobody else did.
        Assert.DoesNotContain(SignalIds(submission), id => id == ExtractionUnavailable);
        Assert.DoesNotContain(SignalIds(assessment), id => id == ExtractionUnavailable);

        // The arithmetic, which is what a reader would otherwise use to check this: the two routes
        // divide by the same denominator because they counted the same rows.
        Assert.Equal(
            assessment.RootElement.GetProperty("riskIndexDenominator").GetDouble(),
            submission.RootElement.GetProperty("riskIndexDenominator").GetDouble());
        Assert.Equal(
            assessment.RootElement.GetProperty("riskIndex").GetDouble(),
            submission.RootElement.GetProperty("riskIndex").GetDouble());

        // The action is deliberately not compared, and the reason is the provider rather than the
        // routes. A total semantic outage makes the submission route decline responsibility, which
        // is a decision about whether to accept mail, while the assessment route only records what
        // it would have done. Those two postures differ by design when the provider is out, and
        // asserting them equal here would be asserting something false about the system.
    }

    private static async Task SubmitAsync(HttpClient sender, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/submissions")
        {
            Content = JsonContent.Create(body),
        };

        // Required by this route and refused without it: a retry has to be recognisable as a retry.
        request.Headers.Add("Idempotency-Key", "route-agreement-1");

        using var response = await sender.SendAsync(request);

        // Accepted, or refused for a reason this test is not about. Anything else means the request
        // never reached the pipeline, and then the ledger holds one decision rather than two and the
        // assertion above fails without saying why.
        Assert.True(
            response.StatusCode is HttpStatusCode.Accepted
                or HttpStatusCode.OK
                or HttpStatusCode.UnprocessableEntity
                or HttpStatusCode.ServiceUnavailable,
            $"The submission route answered {(int)response.StatusCode}: "
                + await response.Content.ReadAsStringAsync());
    }

    private static async Task<List<string>> DecisionsAsync(HttpClient reviewer)
    {
        using var document = JsonDocument.Parse(await reviewer.GetStringAsync("/v1/decisions"));

        return [.. document.RootElement.GetProperty("decisions").EnumerateArray()
            .Select(row => row.GetProperty("assessmentId").GetString()!)];
    }

    private static async Task<string> AssessAsync(HttpClient sender, object body)
    {
        using var response = await sender.PostAsJsonAsync("/v1/assessments", body);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("assessmentId").GetString()
            ?? throw new InvalidOperationException("The assessment route served no assessment id.");
    }

    private static async Task<JsonDocument> DecisionAsync(HttpClient reviewer, string assessmentId)
    {
        var json = await reviewer.GetStringAsync($"/v1/decisions/{assessmentId}");

        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// The evidence ids of deterministic origin, ordered so two sets compare by content.
    /// </summary>
    /// <remarks>
    /// Filtered on the origin rather than on a name prefix, because the origin is the fact: a row
    /// derived from the bytes is what the route without them cannot produce, whatever it is called.
    /// </remarks>
    private static List<string> DeterministicSignalIds(JsonDocument decision) =>
        [.. decision.RootElement.GetProperty("evidence").EnumerateArray()
            .Where(row => row.GetProperty("origin").GetString() == nameof(EvidenceOrigin.Deterministic))
            .Select(row => row.GetProperty("signalId").GetString()!)
            .Order(StringComparer.Ordinal)];

    private static List<string> SignalIds(JsonDocument decision) =>
        [.. decision.RootElement.GetProperty("evidence").EnumerateArray()
            .Select(row => row.GetProperty("signalId").GetString()!)];

    private static List<string> ReasonCodes(JsonDocument decision) =>
        [.. decision.RootElement.GetProperty("reasons").EnumerateArray()
            .Select(row => row.GetProperty("code").GetString()!)];
}
