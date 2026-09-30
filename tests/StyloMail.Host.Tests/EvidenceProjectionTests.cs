using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace StyloMail.Host.Tests;

/// <summary>
/// The evidence projection on a decision, over real HTTP through the real pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>These run the real assessor, not <c>RecordingAssessor</c>.</b> The fake returns a verdict and
/// models the acceptance seam, and its result carries no evidence at all, so no assertion about the
/// shape of evidence can be made against it. <see cref="TestHost.WithRealAssessor"/> composes the
/// real pipeline with the semantic provider unreachable by design, which leaves the semantic rows
/// <c>Unavailable</c> while behavioural evidence is produced for real. That is the half this file is
/// about.
/// </para>
/// <para>
/// <b>Asserted against the raw JSON rather than a deserialised record.</b> The question that matters
/// for <c>window</c> is not only what value it holds but whether the member is present at all, and a
/// record with a nullable property cannot tell the two apart: absent and null deserialise to the same
/// instance.
/// </para>
/// </remarks>
public sealed class EvidenceProjectionTests
{
    [Fact]
    public async Task Two_trend_rows_in_one_scope_come_back_distinguishable_by_their_window()
    {
        // A signal id is not unique within an assessment. The behavioural evaluator emits a velocity
        // and an acceleration for each of its two trend windows, so two rows can agree on signalId
        // AND observedScope and differ only in the window they describe. Without the window in the
        // payload a client has no way to tell them apart, which is exactly what forced the console to
        // render duplicate-looking rows it could not explain.
        using var host = new TestHost().WithRealAssessor();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var response = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(body);

        var velocity = Evidence(document)
            .Where(row => row.GetProperty("signalId").GetString() == "behavioural.trend.velocity")
            .ToList();

        // The pipeline must actually have produced the rows, or the assertions below would be about an
        // empty set and would pass for the wrong reason.
        Assert.True(
            velocity.Count >= 2,
            $"Expected at least two trend-velocity rows from the real pipeline. Body was: {body}");

        // Rows for one scope, which is where the collision happens: same signal, same scope.
        var byScope = velocity
            .GroupBy(row => row.GetProperty("observedScope").GetString(), StringComparer.Ordinal)
            .ToList();

        var pair = byScope.FirstOrDefault(group => group.Count() >= 2);

        Assert.True(
            pair is not null,
            $"Expected two trend-velocity rows sharing one scope. Body was: {body}");

        var windows = pair!.Select(row => WindowOf(row)).ToList();

        // The two windows the producer defines, so this asserts the values that actually reach a
        // caller rather than merely that some window field is non-empty. A renamed attribute on the
        // producer's side fails here, which is the drift the projection helper cannot catch alone.
        Assert.Contains("burst", windows);
        Assert.Contains("slow", windows);

        // And the pair really is indistinguishable without it: within this group the signal id and
        // the scope are both constant, so the window is the only member that tells the rows apart.
        // Asserted rather than assumed, because if the signal ids had differed the collision this
        // field exists to resolve would not have been demonstrated here at all.
        Assert.Single(pair.Select(row => row.GetProperty("signalId").GetString()).Distinct(StringComparer.Ordinal));
        Assert.Single(pair.Select(row => row.GetProperty("observedScope").GetString()).Distinct(StringComparer.Ordinal));
        Assert.Equal(2, windows.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task A_signal_that_is_not_windowed_reports_a_null_window_rather_than_omitting_it()
    {
        // Null is a fact about the signal, not a missing field and not a lost value: a semantic row
        // describes the message, not a slice of a series, so it has no window to report. It must be
        // present as null so a client can tell "this signal is not windowed" from "this payload came
        // from a build that did not know about windows", which are different conclusions to draw.
        using var host = new TestHost().WithRealAssessor();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var response = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(body);

        var semantic = Evidence(document)
            .Where(row => row.GetProperty("origin").GetString() == "Semantic")
            .ToList();

        Assert.True(
            semantic.Count > 0,
            $"Expected semantic rows to be present even with the provider unreachable, since an outage is reported as Unavailable rather than by omitting the dimensions. Body was: {body}");

        foreach (var row in semantic)
        {
            Assert.True(
                row.TryGetProperty("window", out var window),
                $"The window member must be present, as null, on every row. Missing on: {row.GetRawText()}");

            Assert.Equal(JsonValueKind.Null, window.ValueKind);

            // Null here and the availability below are separate facts and must stay separable: this
            // row's window is null because the signal is not windowed, and its availability says the
            // value could not be produced. A client that read one as the other would call a perfectly
            // described signal "unknown".
            Assert.True(
                row.TryGetProperty("availability", out var availability)
                && availability.ValueKind == JsonValueKind.String,
                $"Availability is a required member and must be present on the same row: {row.GetRawText()}");
        }
    }

    private static IEnumerable<JsonElement> Evidence(JsonDocument document)
        => document.RootElement.GetProperty("evidence").EnumerateArray();

    /// <summary>The window a row reports, or null when it is absent or JSON null.</summary>
    private static string? WindowOf(JsonElement row)
        => row.TryGetProperty("window", out var window) && window.ValueKind == JsonValueKind.String
            ? window.GetString()
            : null;
}
