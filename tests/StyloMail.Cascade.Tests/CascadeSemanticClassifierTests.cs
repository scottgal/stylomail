using StyloMail.Core;

namespace StyloMail.Cascade.Tests;

/// <summary>
/// What the cascade does with the answers, as opposed to what the rule decides about them.
/// </summary>
/// <remarks>
/// The rule's tests pin when the strong model is asked. These pin what the caller receives: which row
/// came from which model, what a failed escalation looks like, and the invariants that keep this
/// component from changing the question set the rest of the pipeline thinks it asked.
/// </remarks>
public sealed class CascadeSemanticClassifierTests
{
    private static readonly string[] FirstTwoIds =
        [SemanticDimensions.All[0].Id, SemanticDimensions.All[1].Id];

    [Fact]
    public async Task Returns_the_local_assessment_unchanged_when_nothing_escalates()
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var local = CascadeAssessments.Of(
            [CascadeRows.Available(FirstTwoIds[0], 0.97), CascadeRows.Available(FirstTwoIds[1], 0.96)],
            inputTokens: 900,
            outputTokens: 24);

        var localArm = new RecordingClassifier(_ => local);
        var hostedArm = new RecordingClassifier(_ => CascadeAssessments.Of([]));

        var result = await new CascadeSemanticClassifier(localArm, hostedArm)
            .ClassifyAsync(input, CancellationToken.None);

        // The SAME instance, not an equal one. A rebuild would be a place where a field could drift,
        // and "the local answer is taken whole" is the property that makes this cascade cheap.
        Assert.Same(local, result);
        Assert.Equal(0, hostedArm.CallCount);
    }

    [Fact]
    public async Task Asks_the_strong_model_only_for_the_dimensions_the_rule_named()
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var localArm = new RecordingClassifier(_ => CascadeAssessments.Of(
            [CascadeRows.Available(FirstTwoIds[0], 0.97), CascadeRows.Available(FirstTwoIds[1], 0.50)]));
        var hostedArm = new RecordingClassifier(_ => CascadeAssessments.Of(
            [CascadeRows.Available(FirstTwoIds[1], 0.88, CascadeRows.HostedSource)]));

        await new CascadeSemanticClassifier(localArm, hostedArm)
            .ClassifyAsync(input, CancellationToken.None);

        // One call, and the question set on it is the escalated subset rather than all twelve. This
        // is what "the strong model's cost is the request" turns into as an assertion.
        Assert.Equal(1, hostedArm.CallCount);
        Assert.Equal(FirstTwoIds[1], Assert.Single(hostedArm.LastAskedDimensionIds));
    }

    [Fact]
    public async Task Names_the_answering_model_and_the_reason_on_the_escalated_row()
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var localArm = new RecordingClassifier(_ => CascadeAssessments.Of(
            [CascadeRows.Available(FirstTwoIds[0], 0.97), CascadeRows.Available(FirstTwoIds[1], 0.50)]));
        var hostedArm = new RecordingClassifier(_ => CascadeAssessments.Of(
            [CascadeRows.Available(FirstTwoIds[1], 0.88, CascadeRows.HostedSource)]));

        var result = await new CascadeSemanticClassifier(localArm, hostedArm)
            .ClassifyAsync(input, CancellationToken.None);

        var escalated = Row(result, FirstTwoIds[1]);
        var kept = Row(result, FirstTwoIds[0]);

        // Which model answered, per row, which is the granularity the fleet's two-host measurement
        // forces: a mixed assessment has no single answering model.
        Assert.Equal(CascadeRows.HostedSource, escalated.SourceVersion);
        Assert.Equal(0.88, escalated.Value);

        // And why it was asked, in a namespaced attribute rather than under `reason`, which the
        // console reads as a row's availability reason.
        Assert.Equal(
            EscalationReasonTokens.IndecisiveValue,
            Assert.Single(escalated.Attributes!, a => a.Name == CascadeSemanticClassifier.EscalationAttribute).Value);

        // The kept row is untouched: still the local producer, still the local value, no attribute.
        Assert.Equal(CascadeRows.LocalSource, kept.SourceVersion);
        Assert.Equal(0.97, kept.Value);
        Assert.Null(kept.Attributes);
    }

    [Fact]
    public async Task Keeps_the_local_row_when_the_strong_model_answers_nothing_for_it()
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var localArm = new RecordingClassifier(_ => CascadeAssessments.Of(
            [CascadeRows.Available(FirstTwoIds[0], 0.50), CascadeRows.Available(FirstTwoIds[1], 0.97)]));
        var hostedArm = new RecordingClassifier(_ => CascadeAssessments.Of(
            [CascadeRows.Unavailable(FirstTwoIds[0])]));

        var result = await new CascadeSemanticClassifier(localArm, hostedArm)
            .ClassifyAsync(input, CancellationToken.None);

        var row = Row(result, FirstTwoIds[0]);

        // An escalation that produced nothing usable does NOT throw away the local answer, and does
        // not pretend the strong model answered. The row keeps its value and its producer, and its
        // site says the attempt happened and failed, so a reader can tell "we asked and it did not
        // answer" from "we did not ask".
        Assert.Equal(CascadeRows.LocalSource, row.SourceVersion);
        Assert.Equal(0.50, row.Value);
        Assert.Equal(
            EscalationReasonTokens.IndecisiveValue + ";" + EscalationReasonTokens.HostedUnavailable,
            Assert.Single(row.Attributes!, a => a.Name == CascadeSemanticClassifier.EscalationAttribute).Value);
    }

    [Fact]
    public async Task Adds_and_removes_no_rows()
    {
        var input = CascadeInputs.Input();
        var local = CascadeAssessments.Of(
            [.. SemanticDimensions.All.Select(d => CascadeRows.Available(d.Id, 0.50))]);

        var localArm = new RecordingClassifier(_ => local);
        var hostedArm = new RecordingClassifier(asked => CascadeAssessments.Of(
            [.. asked.Dimensions.Select(d => CascadeRows.Available(d.Id, 0.90, CascadeRows.HostedSource))]));

        var result = await new CascadeSemanticClassifier(localArm, hostedArm)
            .ClassifyAsync(input, CancellationToken.None);

        // Same ids, same count, same order. A cascade that added or dropped rows would move policy's
        // coverage denominator, which is a change to the decision by the back door, and it is exactly
        // the failure a per-dimension rule makes possible.
        Assert.Equal(local.Evidence.Select(e => e.SignalId), result.Evidence.Select(e => e.SignalId));
    }

    [Fact]
    public async Task Reports_the_cascade_version_rather_than_a_model_id()
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var localArm = new RecordingClassifier(_ => CascadeAssessments.Of(
            [CascadeRows.Available(FirstTwoIds[0], 0.50)]));
        var hostedArm = new RecordingClassifier(_ => CascadeAssessments.Of(
            [CascadeRows.Available(FirstTwoIds[0], 0.88, CascadeRows.HostedSource)]));

        var options = new CascadeOptions { ClassifierVersion = "cascade/1+nimble:latest+jev-1.13.0" };

        var result = await new CascadeSemanticClassifier(localArm, hostedArm, options)
            .ClassifyAsync(input, CancellationToken.None);

        // The cache serves an entry only when the assessment's resolved version equals the configured
        // one, so a per-message model id here would stop the cache serving hits on any mixed message.
        Assert.Equal("cascade/1+nimble:latest+jev-1.13.0", result.ResolvedModelVersion);
        Assert.NotEqual(CascadeRows.HostedSource, result.ResolvedModelVersion);
    }

    [Fact]
    public async Task Sums_the_token_counts_of_both_arms()
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var localArm = new RecordingClassifier(_ => CascadeAssessments.Of(
            [CascadeRows.Available(FirstTwoIds[0], 0.50)], inputTokens: 900, outputTokens: 24));
        var hostedArm = new RecordingClassifier(_ => CascadeAssessments.Of(
            [CascadeRows.Available(FirstTwoIds[0], 0.88, CascadeRows.HostedSource)],
            inputTokens: 1200,
            outputTokens: 30));

        var result = await new CascadeSemanticClassifier(localArm, hostedArm)
            .ClassifyAsync(input, CancellationToken.None);

        // Both calls happened, so the assessment's cost is both calls. Reporting only the strong
        // model's count would understate the price of an escalation by the local call that caused it.
        Assert.Equal(2100, result.InputTokens);
        Assert.Equal(54, result.OutputTokens);
    }

    [Fact]
    public async Task Reports_an_absent_token_count_as_absent()
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var localArm = new RecordingClassifier(_ => CascadeAssessments.Of(
            [CascadeRows.Available(FirstTwoIds[0], 0.50)]));
        var hostedArm = new RecordingClassifier(_ => CascadeAssessments.Of(
            [CascadeRows.Available(FirstTwoIds[0], 0.88, CascadeRows.HostedSource)]));

        var result = await new CascadeSemanticClassifier(localArm, hostedArm)
            .ClassifyAsync(input, CancellationToken.None);

        // Neither arm reported a count, so the sum is absent rather than zero: a zero would read as a
        // measurement of an empty prompt.
        Assert.Null(result.InputTokens);
        Assert.Null(result.OutputTokens);
    }

    [Fact]
    public async Task Lets_a_provider_fault_surface_rather_than_degrading_to_local_only()
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var localArm = new RecordingClassifier(_ => CascadeAssessments.Of(
            [CascadeRows.Available(FirstTwoIds[0], 0.50)]));

        var hostedArm = new RecordingClassifier(_ =>
            throw new InvalidOperationException("the hosted provider rejected the request shape"));

        // A rejected credential or a shape the provider refused is a configuration fault, not a
        // reading. Absorbing it would leave a deployment quietly assessing everything on the weak
        // model while its operator believed the strong one was in the loop.
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await new CascadeSemanticClassifier(localArm, hostedArm)
                .ClassifyAsync(input, CancellationToken.None));
    }

    [Fact]
    public async Task Reads_the_prior_answer_through_the_supplied_source()
    {
        var input = CascadeInputs.Input(CascadeInputs.Two);
        var localArm = new RecordingClassifier(_ => CascadeAssessments.Of(
            [CascadeRows.Available(FirstTwoIds[0], 0.80)]));
        var hostedArm = new RecordingClassifier(_ => CascadeAssessments.Of(
            [CascadeRows.Available(FirstTwoIds[0], 0.80, CascadeRows.HostedSource)]));

        var prior = new Dictionary<string, double>(StringComparer.Ordinal) { [FirstTwoIds[0]] = 0.90 };

        var result = await new CascadeSemanticClassifier(localArm, hostedArm, null, _ => prior)
            .ClassifyAsync(input, CancellationToken.None);

        // The prior is supplied by the deployment, because one host is deterministic on a repeated
        // request and a second call to the same host could never disagree with the first.
        Assert.Equal(1, hostedArm.CallCount);
        Assert.Equal(CascadeRows.HostedSource, Row(result, FirstTwoIds[0]).SourceVersion);
    }

    private static Evidence Row(SemanticAssessment assessment, string signalId)
        => Assert.Single(assessment.Evidence, e => e.SignalId == signalId);
}
