using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StyloMail.Assessment;
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
/// <b>What is compared, and the one row that may differ.</b> The deterministic rows are the subject:
/// they are what the bytes produce, and they are absent from the route without them. Exactly one row
/// of deterministic origin is not produced by the bytes,
/// <c>assessment.behavioural_context</c>, which records that the classifier judged the words without
/// knowing the sender. The two routes differ on whether they can change what is known about that
/// sender, and by design rather than by accident: the submission route observes the message and
/// warms the sender's profile, while the assessment route is assessment only and
/// <c>UpdateObservedState</c> returns before it writes anything, leaving the profile store exactly as
/// it was found. So the row is recorded by any call that runs before the sender's first submission,
/// and it is absent from a call that runs after one.
/// </para>
/// <para>
/// That is a property of the profile store rather than of either route's view of the message. This
/// test names the row and asserts the shape of the difference per order, rather than filtering the
/// row out: a divergence in the byte derived rows still fails, and so does a second unexplained row.
/// </para>
/// </remarks>
public sealed class AssessmentRouteAgreementTests
{
    /// <summary>
    /// The marker the assessment route used to carry alone, named through the id the source
    /// publishes rather than through a literal.
    /// </summary>
    /// <remarks>
    /// A literal that no longer matches the source would make the <c>DoesNotContain</c> below pass by
    /// matching nothing, which is the same vacuous shape the non empty guard exists to prevent.
    /// </remarks>
    private const string ExtractionUnavailable = AssessmentEvidenceIds.DeterministicExtractionUnavailable;

    /// <summary>
    /// The one deterministic origin row that comes from the profile store rather than from the bytes.
    /// </summary>
    private const string BehaviouralContext = AssessmentEvidenceIds.BehaviouralContextUnavailable;

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

        // The byte derived rows, in both orders. This is the property the test is named for.
        Assert.Equal(
            submitted.Where(id => id != BehaviouralContext),
            assessed.Where(id => id != BehaviouralContext));

        // And the one row allowed to differ, with the assertion on the shape of the difference rather
        // than an exclusion: filtering the row out would also hide a second row that had no business
        // changing.
        Assert.Empty(assessed.Except(submitted, StringComparer.Ordinal));

        if (submitFirst)
        {
            // The submission went first with the sender still unknown: it recorded the row and,
            // because it is not assessment only, observed the message and warmed the profile. The
            // assessment that follows finds a profile and records nothing.
            Assert.Equal<string>(
                [BehaviouralContext],
                submitted.Except(assessed, StringComparer.Ordinal));
        }
        else
        {
            // The assessment went first and wrote nothing durable, so the submission that follows it
            // still finds no profile. Both calls record the row and the lists agree outright.
            Assert.Empty(submitted.Except(assessed, StringComparer.Ordinal));
            Assert.Contains(BehaviouralContext, submitted);
            Assert.Contains(BehaviouralContext, assessed);
        }

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
    /// <para>
    /// Deterministic origin is not quite the same claim as derived from the bytes, and the caller
    /// subtracts the one row where the two part company:
    /// <see cref="AssessmentEvidenceIds.BehaviouralContextUnavailable"/> is deterministic and comes
    /// from the profile store. Naming it at the call site rather than weakening the filter keeps the
    /// filter honest about what it selects.
    /// </para>
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
