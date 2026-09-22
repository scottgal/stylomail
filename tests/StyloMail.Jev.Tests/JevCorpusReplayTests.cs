using System.Net;
using System.Text;
using System.Text.Json;
using StyloMail.Core;

namespace StyloMail.Jev.Tests;

/// <summary>
/// Replays each committed recording through the adapter and asserts what it parses into.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the half that makes the corpus load-bearing.</b> A recording nobody asserts on is
/// documentation that rots, so every committed case is replayed and its dimensions, availability and
/// values are checked against what the recording itself carries.
/// </para>
/// <para>
/// <b>The expectations come from the fixture, not from a second hand-written file.</b> The recorded
/// body says which dimensions were answered and with what probability; the test reads that and
/// requires the adapter to agree. A hand-written expectation beside a recording can drift from it,
/// and then the test passes while asserting something the provider never said.
/// </para>
/// <para>
/// <b>All three availability states are covered.</b> <c>Available</c> comes from an answered
/// dimension, <c>Unavailable</c> from a dimension that was asked and not answered, and
/// <c>NotApplicable</c> from conversational continuity when the message carries no prior context.
/// A corpus of confident answers would exercise only the first, which is the easy path.
/// </para>
/// </remarks>
public sealed class JevCorpusReplayTests
{
    [JevCorpusReplayFact]
    public async Task EveryRecordingReplaysIntoTheEvidenceItCarries()
    {
        var failures = new List<string>();
        var statesSeen = new HashSet<EvidenceAvailability>();
        var replayed = 0;

        foreach (var caseName in JevCorpus.RecordedCases)
        {
            var recording = JevCorpus.ReadRecording(caseName);
            var input = JevCorpus.BuildInput(caseName);
            var assessment = await ClassifyWithAsync(recording, input);

            var asked = AskedDimensions(input);
            using var document = JsonDocument.Parse(recording);
            var answers = document.RootElement.TryGetProperty("answers", out var a) ? a : default;

            foreach (var dimension in input.Dimensions)
            {
                var evidence = assessment.Evidence.SingleOrDefault(e => e.SignalId == dimension.Id);
                if (evidence is null)
                {
                    failures.Add($"{caseName}: no evidence at all for {dimension.Id}");
                    continue;
                }

                statesSeen.Add(evidence.Availability);

                if (!asked.Contains(dimension.Id))
                {
                    if (evidence.Availability != EvidenceAvailability.NotApplicable)
                    {
                        failures.Add(
                            $"{caseName}: {dimension.Id} was not asked but reported {evidence.Availability} "
                            + "rather than NotApplicable");
                    }

                    continue;
                }

                var answered = answers.ValueKind == JsonValueKind.Object
                    && answers.TryGetProperty(dimension.Id, out var answer)
                    && answer.TryGetProperty("noul", out var noul)
                    && noul.ValueKind == JsonValueKind.Number;

                if (!answered)
                {
                    if (evidence.Availability != EvidenceAvailability.Unavailable)
                    {
                        failures.Add(
                            $"{caseName}: {dimension.Id} was asked and not answered but reported "
                            + $"{evidence.Availability} rather than Unavailable");
                    }

                    continue;
                }

                var expected = answers.GetProperty(dimension.Id).GetProperty("noul").GetDouble();
                if (evidence.Availability != EvidenceAvailability.Available)
                {
                    failures.Add(
                        $"{caseName}: {dimension.Id} was answered with {expected} but reported "
                        + $"{evidence.Availability} rather than Available");
                    continue;
                }

                if (evidence.Value is null || Math.Abs(evidence.Value.Value - expected) > 1e-9)
                {
                    failures.Add(
                        $"{caseName}: {dimension.Id} answered {expected} but parsed as "
                        + $"{evidence.Value?.ToString() ?? "null"}");
                }

                // A Noul carries no confidence field at all. The adapter must leave it null rather
                // than default it, which would fabricate a certainty the provider never expressed.
                if (evidence.Confidence is not null)
                {
                    failures.Add($"{caseName}: {dimension.Id} fabricated a confidence of {evidence.Confidence}");
                }
            }

            replayed++;
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.Equal(JevCorpus.RecordedCases.Count, replayed);

        // The corpus is only worth its weight if it reaches the interesting states. If a future
        // re-record makes every dimension Available, these two assertions are what notices.
        Assert.Contains(EvidenceAvailability.Available, statesSeen);
        Assert.Contains(EvidenceAvailability.NotApplicable, statesSeen);
    }

    [Fact]
    public async Task TheReplayMachineryWorksOnASyntheticBody()
    {
        // <b>This test exists because every test above skips while the corpus is empty.</b> A suite
        // where the whole path is skipped proves the skips work and proves nothing about the code
        // they guard, which is the failure this project keeps finding. So the machinery is exercised
        // against a body held in memory: it is never written to tests/fixtures/jev, because a
        // synthetic body committed as a recording would be exactly the thing the provenance field
        // exists to prevent.
        //
        // It covers all three availability states in one body: an answered dimension, a dimension
        // that was asked and left unanswered, and continuity, which is not asked at all for a
        // message with no prior context.
        const string Body = """
            {
              "model": "jev-1.13.0",
              "answers": {
                "semantic.credential_request": { "type": "noul", "noul": 0.93 },
                "semantic.payment_redirection": { "type": "noul", "noul": 0.02 }
              },
              "usage": { "input_tokens": 296, "output_tokens": 20 }
            }
            """;

        var input = JevCorpus.BuildInput("credential-request");
        var assessment = await ClassifyWithAsync(Body, input);

        var credentialRequest = assessment.Evidence.Single(e => e.SignalId == "semantic.credential_request");
        Assert.Equal(EvidenceAvailability.Available, credentialRequest.Availability);
        Assert.Equal(0.93, credentialRequest.Value!.Value, 9);

        // A Noul carries no confidence field, so the adapter must not invent one.
        Assert.Null(credentialRequest.Confidence);

        // Asked, and absent from the body, which is Unavailable and never a low score.
        var linkLure = assessment.Evidence.Single(e => e.SignalId == "semantic.link_lure");
        Assert.Equal(EvidenceAvailability.Unavailable, linkLure.Availability);
        Assert.Null(linkLure.Value);

        // No conversation context on this case, so continuity is NotApplicable rather than scored.
        var continuity = assessment.Evidence.Single(
            e => e.SignalId == SemanticDimensions.ConversationalContinuityId);
        Assert.Equal(EvidenceAvailability.NotApplicable, continuity.Availability);

        // Provenance the replay assertions depend on.
        Assert.Equal("jev-1.13.0", assessment.ResolvedModelVersion);
        Assert.Equal(296, assessment.InputTokens);
    }

    [JevCorpusReplayFact]
    public void EveryRecordingCarriesTheProvenanceThatMakesItTrustworthy()
    {
        foreach (var caseName in JevCorpus.RecordedCases)
        {
            var provenance = JevCorpus.ReadProvenance(caseName);

            Assert.Equal(caseName, provenance.Case);
            Assert.Equal("live", provenance.Source);

            // A question-set change has to be visible in the corpus rather than silently invalidating
            // it, so a schema move fails here and forces a re-record.
            Assert.Equal(SemanticDimensions.QuestionSchemaVersion, provenance.QuestionSchemaVersion);

            // The pin has to hold. An alias that resolved elsewhere is a discrepancy this project has
            // already been bitten by, and a recording is exactly where it would hide.
            Assert.Equal(provenance.RequestedModel, provenance.ReportedModel);

            Assert.True(provenance.AskedDimensions > 0);
            Assert.NotEqual(default, provenance.RecordedAt);

            // The recording itself has to be present and parseable, not merely described.
            var recording = JevCorpus.ReadRecording(caseName);
            using var document = JsonDocument.Parse(recording);
            Assert.True(document.RootElement.TryGetProperty("answers", out _));
        }
    }

    private static async Task<SemanticAssessment> ClassifyWithAsync(string responseBody, SemanticMailInput input)
    {
        using var http = new HttpClient(new StaticResponseHandler(responseBody));
        var options = new JevOptions
        {
            // Any non-empty key: the response is fixed, so nothing here reaches the provider and the
            // value is irrelevant. It is never a credential and never leaves this test.
            ApiKey = "replay-not-a-credential",
            Model = "jev-1.13.0",
        };

        return await new JevSemanticMailClassifier(http, options).ClassifyAsync(input, CancellationToken.None);
    }

    /// <summary>
    /// The dimensions the adapter will ask, which mirrors its own partition.
    /// </summary>
    /// <remarks>
    /// Continuity is only asked when prior context is present, so the threaded case is the only one
    /// where it appears. Computing it here rather than reading it from the recording is deliberate:
    /// the test states the rule, and a recording that disagrees fails instead of defining its own
    /// expectation.
    /// </remarks>
    private static HashSet<string> AskedDimensions(SemanticMailInput input)
    {
        var hasConversation = input.Message.ConversationContext is { Count: > 0 };

        return [.. input.Dimensions
            .Where(d => d.Id != SemanticDimensions.ConversationalContinuityId || hasConversation)
            .Select(d => d.Id)];
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly string _body;

        internal StaticResponseHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
    }
}
