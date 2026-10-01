using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Core;
using StyloMail.Host.Decisions;

namespace StyloMail.Host.Tests;

/// <summary>
/// The arithmetic a served decision carries about itself.
/// </summary>
/// <remarks>
/// <para>
/// Decision 37. A counted zero and an excluded row are the same two members in a response
/// (<c>score: 0</c>, <c>availability: Available</c>), so a caller recomputing the index from the rows
/// alone adds back the weight of a row the index excluded and publishes a number nothing in the body
/// contradicts. These tests take the recomputation a consumer would make and require the body to
/// reproduce its own index from what it serves.
/// </para>
/// <para>
/// <b>Asserted against the raw JSON, not a deserialised record.</b> Whether <c>exclusionReason</c> is
/// present at all is part of what is pinned here, and a record with a nullable property cannot tell
/// absent from null: both deserialise to the same instance.
/// </para>
/// <para>
/// <b>Where the counted rows come from.</b> Real HTTP through <see cref="TestHost"/> for the shape of
/// the served body, but not from the live scorer: <see cref="TestHost.WithRealAssessor"/> composes the
/// real pipeline with the semantic provider unreachable by design, and in that configuration no
/// dimension is weighted-and-available at once, so nothing is counted and the identity is vacuous
/// there. The identity is therefore exercised through a recorded decision whose rows are stated, and
/// the real path is asserted for the invariant it can actually exhibit. The scorer's own production of
/// these rows, including decision 31's mask, is the Policy suite's to prove.
/// </para>
/// </remarks>
public sealed class DecisionArithmeticProjectionTests
{
    [Fact]
    public async Task A_served_decision_reproduces_its_own_index_from_the_rows_it_serves()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var response = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var rows = root.GetProperty("riskDimensions").EnumerateArray().ToList();
        Assert.NotEmpty(rows);

        var denominator = root.GetProperty("riskIndexDenominator").GetDouble();
        var index = root.GetProperty("riskIndex").GetDouble();

        var counted = rows.Where(row => row.GetProperty("counted").GetBoolean()).ToList();
        Assert.True(counted.Count > 0, $"No dimension was counted, so the served body cannot exercise "
            + $"the arithmetic. Body was: {body}");
        Assert.True(denominator > 0, $"The denominator is not positive where {counted.Count} rows were "
            + $"counted. Body was: {body}");

        // The denominator is not a number of the response's own choosing: it is the summed weight of
        // the rows it publishes as counted, which is the whole of what makes the index checkable.
        Assert.Equal(
            counted.Sum(row => row.GetProperty("weight").GetDouble()),
            denominator,
            precision: 12);

        // The recomputation a consumer makes, from nothing but the body.
        var numerator = counted.Sum(row =>
            row.GetProperty("weight").GetDouble() * row.GetProperty("score").GetDouble());

        Assert.Equal(index, numerator / denominator, precision: 12);
    }

    [Fact]
    public async Task A_counted_zero_and_an_excluded_row_stay_distinguishable_in_the_served_body()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var posted = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());
        posted.EnsureSuccessStatusCode();

        string assessmentId;
        using (var postedBody = JsonDocument.Parse(await posted.Content.ReadAsStringAsync()))
        {
            assessmentId = postedBody.RootElement.GetProperty("assessmentId").GetString()!;
        }

        var ledger = host.Services.GetRequiredService<IDecisionLedger>();
        var recorded = await ledger.FindAsync(
            TestPrincipals.AcmeTenant, assessmentId, CancellationToken.None);
        Assert.NotNull(recorded);

        // Two rows that read identically to a consumer that ignores the flag: both score zero, both
        // available. One is a measured calm that enters the denominator and dilutes the index; the
        // other was available and excluded, and enters nothing at all. Writing them through the
        // ledger rather than in the POST body is deliberate: the round trip through storage is where a
        // required member could be dropped without any compile error.
        var risk = Dimension("behavioural.risk", score: 1.0, counted: true);
        var measuredCalm = Dimension("behavioural.calm", score: 0.0, counted: true);
        var excluded = Dimension(
            "semantic.one_sided",
            score: 0.0,
            counted: false,
            reason: "available but not confirming, so it contributes nothing rather than a counted zero "
                + "(decision 31)");

        await ledger.RecordAsync(
            recorded with
            {
                RiskDimensions = [risk, measuredCalm, excluded],
                // One counted weight of 1.0 for each of the two counted rows, and the index their
                // scores produce: (1.0 * 1.0 + 1.0 * 0.0) / 2.0.
                RiskIndexDenominator = 2.0,
                RiskIndex = 0.5,
            },
            CancellationToken.None);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var response = await reviewer.GetAsync($"/v1/decisions/{assessmentId}");
        var served = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(served);
        var root = document.RootElement;
        var rows = root.GetProperty("riskDimensions").EnumerateArray().ToList();

        var calm = rows.Single(row => row.GetProperty("name").GetString() == "behavioural.calm");
        var masked = rows.Single(row => row.GetProperty("name").GetString() == "semantic.one_sided");

        // The premise of the decision, asserted rather than assumed: on the members a consumer would
        // reach for first, these two rows are the same row.
        Assert.Equal(0.0, calm.GetProperty("score").GetDouble());
        Assert.Equal(0.0, masked.GetProperty("score").GetDouble());
        Assert.Equal(
            nameof(EvidenceAvailability.Available),
            calm.GetProperty("availability").GetString());
        Assert.Equal(
            nameof(EvidenceAvailability.Available),
            masked.GetProperty("availability").GetString());

        // And the member that decides whether they entered the arithmetic is what tells them apart.
        Assert.True(calm.GetProperty("counted").GetBoolean());
        Assert.False(masked.GetProperty("counted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, calm.GetProperty("exclusionReason").ValueKind);
        Assert.Equal(JsonValueKind.String, masked.GetProperty("exclusionReason").ValueKind);

        var denominator = root.GetProperty("riskIndexDenominator").GetDouble();
        var index = root.GetProperty("riskIndex").GetDouble();

        var counted = rows.Where(row => row.GetProperty("counted").GetBoolean()).ToList();

        Assert.Equal(
            counted.Sum(row => row.GetProperty("weight").GetDouble()),
            denominator,
            precision: 12);

        var checkable = counted.Sum(row =>
            row.GetProperty("weight").GetDouble() * row.GetProperty("score").GetDouble()) / denominator;

        Assert.Equal(index, checkable, precision: 12);

        // What a consumer gets when it ignores the flag: the excluded row's weight lands in the
        // denominator and nothing in the numerator, which dilutes the index toward Allow while the
        // coverage it reports rises. That number is the defect this record exists to prevent.
        var naive =
            rows.Sum(row => row.GetProperty("weight").GetDouble() * row.GetProperty("score").GetDouble())
            / rows.Sum(row => row.GetProperty("weight").GetDouble());

        Assert.NotEqual(index, naive, precision: 12);
    }

    [Fact]
    public async Task An_uncounted_row_says_why_it_was_not_counted_rather_than_reading_as_a_zero()
    {
        // The live pipeline as this suite can compose it: the semantic provider refuses, so its rows
        // are unavailable or not applicable, and nothing reaches the numerator. What can be asserted
        // here is the invariant that holds either way, and that a body with nothing counted is still
        // coherent with itself rather than being a division nobody can perform.
        using var host = new TestHost().WithRealAssessor();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var response = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var rows = root.GetProperty("riskDimensions").EnumerateArray().ToList();

        Assert.NotEmpty(rows);

        var counted = new List<JsonElement>();

        foreach (var row in rows)
        {
            var name = row.GetProperty("name").GetString();

            // Read rather than probed, which is itself the assertion: GetProperty throws when the
            // member is absent, so this pins that a null is served as null on every row instead of
            // the member being dropped from the payload.
            var reason = row.GetProperty("exclusionReason");
            var availability = row.GetProperty("availability").GetString();

            if (row.GetProperty("counted").GetBoolean())
            {
                counted.Add(row);

                // A counted row has nothing to excuse, so it carries no reason.
                Assert.Equal(JsonValueKind.Null, reason.ValueKind);
            }
            else
            {
                // Two ways to be excluded, and the row has to say which one: the evidence was not
                // available, or it was available and excluded anyway, in which case the reason has to
                // travel because the availability does not explain it.
                Assert.True(
                    availability != nameof(EvidenceAvailability.Available)
                        || reason.ValueKind == JsonValueKind.String,
                    $"Row '{name}' was not counted, was available, and gave no reason for it: that is "
                    + $"the row a consumer cannot tell from a measured zero. Body was: {body}");
            }
        }

        var denominator = root.GetProperty("riskIndexDenominator").GetDouble();
        Assert.Equal(
            counted.Sum(row => row.GetProperty("weight").GetDouble()),
            denominator,
            precision: 12);

        if (counted.Count == 0)
        {
            // Nothing counted is a real state, not a broken one: there is no denominator to divide by
            // and the index is zero, which is what the scorer publishes when no dimension was
            // available. A body that said otherwise would invite a division by zero.
            Assert.Equal(0.0, root.GetProperty("riskIndex").GetDouble());
            Assert.Equal(0.0, denominator);
        }
    }

    [Fact]
    public async Task The_covered_fraction_a_floor_used_survives_storage_and_is_not_the_row_sum()
    {
        // The same round trip as the test above, for the member the floors are compared against. It is
        // written through the ledger rather than posted for the reason stated there: storage is where a
        // required member can be dropped without any compile error.
        //
        // The fraction is a STATED fixture and not a scorer reproduction, which is what this file's
        // class remark allows: the rows are chosen so the two candidate values are far apart, and the
        // test is about which one survives, not about the scorer's arithmetic (that is the Policy and
        // Assessment suites' to prove).
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var posted = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());
        posted.EnsureSuccessStatusCode();

        string assessmentId;
        using (var postedBody = JsonDocument.Parse(await posted.Content.ReadAsStringAsync()))
        {
            assessmentId = postedBody.RootElement.GetProperty("assessmentId").GetString()!;
        }

        var ledger = host.Services.GetRequiredService<IDecisionLedger>();
        var recorded = await ledger.FindAsync(
            TestPrincipals.AcmeTenant, assessmentId, CancellationToken.None);
        Assert.NotNull(recorded);

        // One counted row of weight 1.0, and three masked rows of weight 1.0 that no member the
        // response serves distinguishes from rows that DO count. Their availability is Unavailable,
        // which is what the scorer publishes both for a row that was asked and not answered (it stays
        // in the denominator and counts against coverage) and for a deterministic row that was never
        // asked (it leaves the denominator entirely). Nothing on the row decides between those two
        // classes, which is the gap: the covered weight the floor used is 1.0, while a caller summing
        // every served row's weight builds a denominator of 4.0 and reads 0.25.
        //
        // Unavailable rather than NotApplicable deliberately: a NotApplicable row is the one never-asked
        // shape a reader CAN exclude by eye, so it would be the weaker demonstration of the same point.
        var counted = Dimension("behavioural.risk", score: 1.0, counted: true);
        var masked = Dimension(
            "semantic.one_sided",
            score: 0.0,
            counted: false,
            availability: EvidenceAvailability.Unavailable);

        await ledger.RecordAsync(
            recorded with
            {
                RiskDimensions = [counted, masked, masked, masked],
                RiskIndexDenominator = 1.0,
                RiskIndex = 1.0,
                CoveredWeightFraction = 1.0,
            },
            CancellationToken.None);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var response = await reviewer.GetAsync($"/v1/decisions/{assessmentId}");
        var served = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(served);
        var root = document.RootElement;

        Assert.True(
            root.TryGetProperty("coveredWeightFraction", out var fraction),
            $"The served decision carries no coveredWeightFraction at all. Body was: {served}");

        // The value that survives storage is the one the floor was given, not null and not a zero
        // standing in for it.
        Assert.Equal(1.0, fraction.GetDouble(), precision: 12);

        // And it is NOT what the served rows give back. Asserted as a difference rather than compared
        // by eye, because the whole case for the member is that these two values disagree here.
        var rows = root.GetProperty("riskDimensions").EnumerateArray().ToList();
        var servedWeight = rows.Sum(row => row.GetProperty("weight").GetDouble());
        var countedWeight = rows
            .Where(row => row.GetProperty("counted").GetBoolean())
            .Sum(row => row.GetProperty("weight").GetDouble());

        Assert.Equal(4.0, servedWeight, precision: 12);
        Assert.Equal(1.0, countedWeight, precision: 12);

        // Written as an explicit tolerance rather than through a NotEqual precision overload, which no
        // test in this repo currently uses and which this patch cannot compile against before it is
        // applied. A wrong overload would be a build break at exactly the moment the tree is moving.
        Assert.True(
            Math.Abs(fraction.GetDouble() - (countedWeight / servedWeight)) > 1e-12,
            $"The served fraction and the row-derived fraction both read {fraction.GetDouble()}, so "
                + "this body does not show the member carrying anything the rows do not. "
                + $"Body was: {served}");
    }

    private static RiskDimension Dimension(
        string name,
        double score,
        bool counted,
        string? reason = null,
        // Stated per row rather than fixed, because the availability is what decides whether
        // the scorer's never-asked branch takes the row out of the coverage denominator, and
        // a test that needs that class has to be able to say so. Defaulted, so every call
        // site that predates this keeps the value it was written against.
        EvidenceAvailability availability = EvidenceAvailability.Available) => new()
    {
        Name = name,
        Score = score,
        Availability = availability,
        Weight = 1.0,
        Counted = counted,
        ExclusionReason = reason,
        EvidenceSignalIds = [],
    };
}
