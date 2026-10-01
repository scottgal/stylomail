using System.Diagnostics;
using System.Text.Json;
using StyloMail.Core;
using StyloMail.Nimble;

namespace StyloMail.Conversation.Measure;

/// <summary>
/// One call through the shipping adapter, reduced to the facts a report quotes.
/// </summary>
internal sealed record CallResult
{
    /// <summary>Dimension id to answer code, for the dimensions the provider actually answered.</summary>
    public required Dictionary<string, string> Codes { get; init; }

    /// <summary>The continuity row on its own, because it is the dimension this lane is about.</summary>
    public required string Continuity { get; init; }

    /// <summary>How many dimensions the provider was asked, and how many it answered.</summary>
    public required int Asked { get; init; }

    public required int Answered { get; init; }

    public required int? PromptTokens { get; init; }

    public required int? OutputTokens { get; init; }

    public required long ElapsedMs { get; init; }

    /// <summary>True when the prompt carried the marker saying the body was cut to make room.</summary>
    public required bool BodyShortened { get; init; }

    /// <summary>The adapter's own reason when it produced no answer at all.</summary>
    public string? UnavailableReason { get; init; }

    public required string? Model { get; init; }

    /// <summary>
    /// The adapter's digest over everything that could change the answer, for the call that was made.
    /// </summary>
    /// <remarks>
    /// <b>This is the identity of the question, not a note about the call.</b> The adapter hashes the
    /// model, the schema and shape versions, the window, the number of dimensions asked, and the
    /// rendered state the provider was given. Two calls agree on this value exactly when the provider
    /// was shown the same thing, which is what makes it usable as an equality test across processes:
    /// a digest read here can be compared with one the Host served for its own request.
    /// </remarks>
    public required string? KeyDigest { get; init; }
}

/// <summary>Drives the adapter and turns each answer into a <see cref="CallResult"/>.</summary>
internal sealed class Runner(
    NimbleSemanticMailClassifier classifier,
    RecordingHandler recorder,
    NimbleOptions options)
{
    internal NimbleOptions Options { get; } = options;

    internal async Task<CallResult> CallAsync(SemanticMailInput input, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var assessment = await classifier.ClassifyAsync(input, cancellationToken).ConfigureAwait(false);
        clock.Stop();

        var continuity = assessment.Evidence
            .FirstOrDefault(e => e.SignalId == SemanticDimensions.ConversationalContinuityId);

        var answered = assessment.Evidence
            .Where(e => e.Availability == EvidenceAvailability.Available)
            .ToList();

        var codes = answered.ToDictionary(
            e => e.SignalId,
            e => e.Value == 1.0 ? "A" : "B",
            StringComparer.Ordinal);

        var sent = recorder.LastRequestBody;

        // The marker is written into the state only when the body was cut to fit, and the state is
        // what was sent, so reading it off the payload is the same fact the model was told.
        var shortened = sent is not null
            && sent.Contains("body_text_shortened_for_prompt", StringComparison.Ordinal);

        return new CallResult
        {
            Codes = codes,
            Continuity = Describe(continuity),
            Asked = assessment.Evidence.Count(e => e.Availability != EvidenceAvailability.NotApplicable),
            Answered = answered.Count,
            PromptTokens = assessment.InputTokens,
            OutputTokens = assessment.OutputTokens,
            ElapsedMs = clock.ElapsedMilliseconds,
            BodyShortened = shortened,
            UnavailableReason = assessment.Cache.KeyDigest?.StartsWith("unavailable:", StringComparison.Ordinal) == true
                ? assessment.Cache.KeyDigest["unavailable:".Length..]
                : null,
            Model = assessment.ResolvedModelVersion,
            KeyDigest = assessment.Cache.KeyDigest,
        };
    }

    private static string Describe(Evidence? evidence)
    {
        if (evidence is null)
        {
            return "absent";
        }

        return evidence.Availability switch
        {
            EvidenceAvailability.Available => evidence.Value == 1.0 ? "A" : "B",
            _ => evidence.Availability.ToString(),
        };
    }
}
