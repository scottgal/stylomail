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

    /// <summary>True when the state carried the marker saying the QUOTED field was cut.</summary>
    /// <remarks>
    /// Read from the same payload as <see cref="BodyShortened"/> and separate from it, because the
    /// repair writes ONE FLAG AND ONE KEPT LENGTH PER FIELD. A state that names the cut field cannot
    /// name the other one, so on a quoted-only cut the BODY KEY'S ABSENCE is the assertion and this
    /// flag is what says the quoted one fired.
    /// </remarks>
    public required bool QuotedShortened { get; init; }

    /// <summary>The body's kept length from the payload, or null when the key is absent.</summary>
    public required int? BodyCharactersKept { get; init; }

    /// <summary>The quoted field's kept length from the payload, or null when the key is absent.</summary>
    /// <remarks>
    /// On a quoted cut this value IS the fit's landing budget, because the binding field is the one
    /// truncated to it - so an arm reads the budget off the wire instead of deriving it, and a
    /// predicted landing that disagrees with this is wrong whatever the arithmetic says.
    /// </remarks>
    public required int? QuotedCharactersKept { get; init; }

    /// <summary>The shortening reason the DECISION carries, or null when no row names one.</summary>
    /// <remarks>
    /// Read from the semantic evidence's <c>reason</c> attribute rather than from the request, because
    /// this is the only carrier a reader of a DECISION has: a console sees the reason and not the
    /// request, so it is the one place the cut is legible to a human. The request-side keys above are
    /// the arm's other half.
    /// </remarks>
    public required string? ShorteningReason { get; init; }

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

        // And the same read for the other field, because the repair writes one flag and one kept
        // length PER FIELD. The two keys are not substrings of one another, so a name match is a
        // key match, and the absence of one is a statement about the other.
        var quotedShortened = sent is not null
            && sent.Contains("quoted_text_shortened_for_prompt", StringComparison.Ordinal);
        var bodyKept = StateInt(sent, "body_text_characters_kept");
        var quotedKept = StateInt(sent, "quoted_text_characters_kept");

        // And the decision's own statement of the same fact. The `reason` attribute is NOT the
        // shortening reason alone: the adapter also records the UNAVAILABILITY reason under the same
        // name, so a read that takes the first row carrying one returns "HttpRequestException" on a
        // poisoned endpoint - which this lane measured by doing exactly that. The selection is
        // therefore by AVAILABILITY as well as by attribute name: the shortening reason is attached
        // to a row that produced a value, and a row that produced none is not it.
        string? shorteningReason = null;
        foreach (var row in assessment.Evidence)
        {
            if (row.Availability != EvidenceAvailability.Available)
            {
                continue;
            }

            var hit = row.Attributes?.FirstOrDefault(
                a => string.Equals(a.Name, "reason", StringComparison.Ordinal));

            if (hit is not null)
            {
                shorteningReason = hit.Value;
                break;
            }
        }

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
            QuotedShortened = quotedShortened,
            BodyCharactersKept = bodyKept,
            QuotedCharactersKept = quotedKept,
            ShorteningReason = shorteningReason,
            UnavailableReason = assessment.Cache.KeyDigest?.StartsWith("unavailable:", StringComparison.Ordinal) == true
                ? assessment.Cache.KeyDigest["unavailable:".Length..]
                : null,
            Model = assessment.ResolvedModelVersion,
            KeyDigest = assessment.Cache.KeyDigest,
        };
    }

    /// <summary>The integer a state key carries in the sent request, or null when the key is absent.</summary>
    /// <remarks>
    /// The state travels as an ESCAPED nested JSON string, so a key's own QUOTES arrive as characters
    /// rather than as quotes - measured on a captured request, an escaped quote is six characters. The
    /// NAME does not, which is why this finds the name and then reads the digits after the next colon
    /// rather than matching a quoted key. It also means the read does not depend on the escaping
    /// convention, which is an implementation detail this arm has no reason to encode.
    /// </remarks>
    private static int? StateInt(string? body, string key)
    {
        if (body is null)
        {
            return null;
        }

        var at = body.IndexOf(key, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var colon = body.IndexOf(':', at);
        if (colon < 0)
        {
            return null;
        }

        var i = colon + 1;
        while (i < body.Length && body[i] == ' ')
        {
            i++;
        }

        var start = i;
        while (i < body.Length && body[i] is >= '0' and <= '9')
        {
            i++;
        }

        return i > start && int.TryParse(body[start..i], out var value) ? value : null;
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
