using System.Text.Json.Serialization;

namespace StyloMail.Nimble;

/// <summary>
/// The vocabulary of the SystemOne request. Wire names, so they are facts about the server rather
/// than choices of this project, and they are named once here so a typo is a compile error.
/// </summary>
internal static class NimbleQuestionTypes
{
    /// <summary>
    /// A yes/no question whose answer is a graded probability.
    /// </summary>
    /// <remarks>
    /// The only type this adapter sends, and that is a measurement rather than a simplification.
    /// Every one of the twelve dimensions in <see cref="Core.SemanticDimensions"/> is a Noul question:
    /// each answers its own yes/no and several may hold at once, which is stated at
    /// <c>SemanticDimension.cs:7-9</c> as the reason they are deliberately not a choice across labels.
    /// The endpoint also offers <c>choice</c> and <c>score</c>, and neither is modelled here. A type
    /// with no caller would be dead code that reads as a supported feature, and the two remaining
    /// primitives are one line each to add if a dimension ever needs them.
    /// </remarks>
    internal const string Noul = "noul";
}

/// <summary>
/// The body sent to the local server's SystemOne decision endpoint, <c>POST /v1/systemone</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured, not read off a specification.</b> The three members below are exactly what the probe
/// at <c>.styloagent/scratch/overview/probe-systemone.py</c> sent and what the server answered with
/// 200 on 1 Oct 2026, recorded verbatim in the <c>.out</c> beside it. The same probe posts the same
/// body to the native path <c>/api/systemone</c> and gets a 404, so <c>/v1/systemone</c> is the only
/// path that exists and the endpoint string is a full URL ending in it.
/// </para>
/// <para>
/// <b>What the measured request does NOT carry, listed because each absence is a decision.</b> No
/// <c>options</c>, so neither temperature nor <c>num_ctx</c> is set for this shape; no <c>format</c>,
/// because the server's own answer envelope is typed; no <c>stream</c>; no <c>think</c>. Whether the
/// endpoint accepts any of them is UNMEASURED. The adapter therefore sends the shape that was measured
/// and nothing else, rather than adding a plausible field and calling it supported.
/// </para>
/// <para>
/// <b>The containment argument moves, and it is recorded here rather than dropped.</b> The previous
/// body split instructions from untrusted content across a <c>system</c> field and a <c>prompt</c>
/// field, and that split was the whole containment claim. This shape has no system channel: the
/// message travels in <c>state</c> and the instructions travel inside each question, so both are
/// members of one JSON document. Whether an additional <c>system</c> member is accepted is
/// UNMEASURED, and its absence here is the shape that was measured, not a finding that the guard was
/// unnecessary. The claim this file can still make is the narrow one: a hostile message cannot add a
/// question, because the question list is built from <see cref="Core.SemanticDimensions"/> in code.
/// Whether it can persuade the model to answer one wrongly is the question the live injection
/// measurement exists to ask.
/// </para>
/// <para>
/// Field names are the server's and are snake-free lower case here, which is not this project's
/// convention. The type is internal and exists to be serialised, so its property names follow the
/// wire.
/// </para>
/// </remarks>
internal sealed record NimbleSystemOneRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>
    /// The message under assessment, as the JSON text of the state document.
    /// </summary>
    /// <remarks>
    /// <b>Sent as a string, because that is what the probe sent and what the server accepted.</b> The
    /// state is a structured document and serialising it to JSON text here keeps it one opaque value
    /// from the server's point of view, which is the property the old <c>prompt</c> field had. Whether
    /// the endpoint accepts a JSON object in this member instead is UNMEASURED; passing the string
    /// cannot be wrong for the shape that was measured, and the object form would be a change with no
    /// measurement behind it.
    /// </remarks>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    /// <summary>
    /// One entry per asked dimension, keyed positionally as <c>q0</c> to <c>qN</c>.
    /// </summary>
    /// <remarks>
    /// Positional rather than by dimension id, for the reason the schema property names were
    /// positional before it: the ids carry dots and are long, and a key that reads like an id invites
    /// a payload that answers with one. The mapping back to ids is held in code, where it cannot
    /// drift. The server echoes the keys back in <c>answers</c>, so this key is also how an answer is
    /// matched to its dimension.
    /// </remarks>
    [JsonPropertyName("questions")]
    public required IReadOnlyDictionary<string, NimbleQuestion> Questions { get; init; }
}

/// <summary>One declared question: its type, its instruction, and its criteria.</summary>
internal sealed record NimbleQuestion
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = NimbleQuestionTypes.Noul;

    /// <summary>The question text. Fixed by code from the dimension, never by message content.</summary>
    [JsonPropertyName("instructions")]
    public required string Instructions { get; init; }

    [JsonPropertyName("criteria")]
    public required NimbleNoulCriteria Criteria { get; init; }
}

/// <summary>
/// A Noul question's two criteria, as an object with the members <c>true</c> and <c>false</c>.
/// </summary>
/// <remarks>
/// <b>Both members are always sent, and both come from the dimension.</b> Every one of the twelve
/// ships a <c>CriteriaTrue</c> and a <c>CriteriaFalse</c>, so there is no dimension for which one side
/// is empty and no case where this type would carry a null. The order the members are declared in is
/// not the order they appear on the wire, which is alphabetical because the server is the one that
/// decides; the pair is sent as a unit and the server reads it by name.
/// </remarks>
internal sealed record NimbleNoulCriteria
{
    /// <summary>The case in which the condition is present.</summary>
    [JsonPropertyName("true")]
    public required string True { get; init; }

    /// <summary>The case in which the condition is not present.</summary>
    [JsonPropertyName("false")]
    public required string False { get; init; }
}

/// <summary>
/// The body returned by <c>POST /v1/systemone</c>, as measured on Ollama 0.35.0.
/// </summary>
/// <remarks>
/// <para>
/// <b>Note what is absent, because two absences change the adapter rather than merely simplifying
/// it.</b> There is no <c>done_reason</c>, and there is no <c>prompt_eval_count</c>. A
/// <c>/api/generate</c> response carried both, and the classifier's truncation backstop read the
/// second one. Nothing here reports how many prompt tokens the server evaluated except
/// <see cref="NimbleUsage.InputTokens"/>, and whether that figure is taken before or after the server
/// applies its window is UNMEASURED. Until it is measured, the backstop that exists to stop a
/// partly-read message being reported as a complete answer is resting on an assumption, and the
/// adapter records that rather than asserting the property.
/// </para>
/// <para>
/// <b>There is no error member on a success and no success member on an error.</b> A failure is a
/// non-2xx status handled before this type is deserialised, so an <c>error</c> member here would be a
/// field that is never populated.
/// </para>
/// </remarks>
internal sealed record NimbleSystemOneResponse
{
    /// <summary>
    /// The model reference the server answered with. Echoed rather than resolved, exactly as
    /// <c>/api/generate</c> echoed it, so it is what was asked for and not a build identity.
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    /// <summary>One entry per asked question, keyed by the key the request sent.</summary>
    [JsonPropertyName("answers")]
    public IReadOnlyDictionary<string, NimbleSystemOneAnswer>? Answers { get; init; }

    [JsonPropertyName("usage")]
    public NimbleUsage? Usage { get; init; }
}

/// <summary>
/// One answer. For a Noul question the answer is <see cref="Noul"/>, a probability in [0, 1].
/// </summary>
/// <remarks>
/// <para>
/// <b>The answer is a graded probability, and that is the finding this migration was for.</b> The
/// probe's credential question answered <c>0.9995100663573931</c>, not a letter and not a boolean. The
/// port's evidence value is a Noul probability in which a figure near 0.5 means genuinely balanced
/// (<c>Evidence.cs:32-34</c>), so this shape maps onto the port directly, where the A/B letter shape
/// this replaced could only ever produce 1.0 or 0.0 and could not express balance at all.
/// </para>
/// <para>
/// <b><see cref="Type"/> is carried through rather than ignored.</b> The key is answered by the
/// question that was asked, so a mismatch between the type asked for and the type answered with means
/// the request and the answer are not about the same thing. The classifier refuses such an answer
/// rather than reading a number out of it.
/// </para>
/// <para>
/// <b>There is no <c>confidence</c> member, and that is why one is not declared here.</b> The measured
/// answer for a Noul question has <c>type</c> and <c>noul</c> and nothing else; the probe's
/// <c>choice</c> and <c>score</c> answers are the ones that carry <c>confidence</c> and
/// <c>probabilities</c>. This adapter asks Noul questions only, so a <c>Confidence</c> property here
/// would be a member that is never populated, which is the thing the old response type's remark
/// warned against. The port's rule is that a Noul answer carries a null confidence and the probability
/// is the value; that rule and this wire shape agree.
/// </para>
/// </remarks>
internal sealed record NimbleSystemOneAnswer
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    /// The probability that the condition holds, in [0, 1]. Null when the answer is not a Noul one.
    /// </summary>
    [JsonPropertyName("noul")]
    public double? Noul { get; init; }
}

/// <summary>Accounting for one call, as the server reports it.</summary>
internal sealed record NimbleUsage
{
    /// <summary>
    /// Prompt tokens the server counted. The only truncation signal this shape offers.
    /// </summary>
    /// <remarks>
    /// UNMEASURED whether this is the count of the prompt as sent or as the server applied it. On
    /// <c>/api/generate</c> the equivalent field was the post-shortening count, which is why the
    /// backstop that reads this one is marked as resting on an assumption rather than as a property.
    /// </remarks>
    [JsonPropertyName("input_tokens")]
    public int? InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public int? OutputTokens { get; init; }
}
