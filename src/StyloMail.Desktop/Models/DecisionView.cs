using System.Globalization;
using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Models;

/// <summary>
/// A decision, arranged the way it is read rather than the way it arrives.
/// </summary>
/// <remarks>
/// <b>This is the pane the console exists for, and spec 10.3 sets the rule:
/// evidence and ordered reason codes, not a single score.</b> Everything here
/// follows from that. The reasons keep policy's order, each one carries the
/// evidence that produced it, and nothing collapses to a verdict, because an
/// operator who is shown a number and no reasoning has been given nothing they
/// can act on or disagree with.
///
/// <para>
/// Built from the response alone. A view that needed a live client to render
/// could not be tested or photographed without a Host, and this is the one
/// surface where a rendering mistake means an operator acts on a false
/// explanation.
/// </para>
/// </remarks>
public sealed class DecisionView
{
    private DecisionView() { }

    public required string AssessmentId { get; init; }

    /// <summary>What policy did.</summary>
    public required string ActionLabel { get; init; }

    /// <summary>
    /// What policy would have done, in shadow mode. Null when this decision was
    /// taken outright.
    /// </summary>
    public string? ShadowLabel { get; init; }

    /// <summary>
    /// The aggregate, labelled as an index.
    /// </summary>
    /// <remarks>
    /// It is a documented index and <b>not</b> a calibrated probability. The
    /// label says so, because rendering it as a percentage would be the easiest
    /// way for this pane to be confidently wrong: an operator reads "82%" as a
    /// statement about how often this is right, and it is not one.
    /// </remarks>
    public required string RiskIndexLabel { get; init; }

    public required IReadOnlyList<ReasonView> Reasons { get; init; }

    public required IReadOnlyList<DimensionView> Dimensions { get; init; }

    public required IReadOnlyList<EvidenceView> Evidence { get; init; }

    public required IReadOnlyList<CoverageFlag> Coverage { get; init; }

    public required IReadOnlyList<VersionEntry> Versions { get; init; }

    public CacheEntry? Cache { get; init; }

    public static DecisionView From(DecisionResponse decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var bySignal = decision.Evidence.ToDictionary(
            evidence => evidence.SignalId,
            EvidenceView.From,
            StringComparer.Ordinal);

        return new DecisionView
        {
            AssessmentId = decision.AssessmentId,
            ActionLabel = decision.Action.ToString(),
            ShadowLabel = decision.ProposedActionInShadow is { } proposed
                ? $"Would have been {proposed}"
                : null,
            RiskIndexLabel = string.Create(
                CultureInfo.InvariantCulture,
                $"risk index {decision.RiskIndex:0.###} (an index, not a probability)"),
            Reasons = [.. decision.Reasons.Select(reason => ReasonView.From(reason, bySignal))],
            Dimensions = [.. decision.RiskDimensions.Select(DimensionView.From)],
            Evidence = [.. decision.Evidence.Select(EvidenceView.From)],
            Coverage = CoverageFlag.From(decision.Coverage),
            Versions = VersionEntry.From(decision.Versions),
            Cache = decision.Cache is null ? null : CacheEntry.From(decision.Cache),
        };
    }
}

/// <summary>
/// One reason, with the evidence behind it.
/// </summary>
/// <remarks>
/// The order of the outer list is policy's order and is never resorted. The
/// inner list is resolved here rather than in the view so that a signal the
/// response did not include can be reported rather than silently dropped.
/// </remarks>
public sealed class ReasonView
{
    private ReasonView() { }

    public required string Code { get; init; }

    public required string Message { get; init; }

    public required IReadOnlyList<EvidenceView> Evidence { get; init; }

    /// <summary>
    /// Signals this reason names that the response did not carry.
    /// </summary>
    /// <remarks>
    /// Named rather than dropped. Rendering only the signals that happened to
    /// arrive turns a truncated or mismatched response into a reason that looks
    /// fully evidenced, which is the failure mode of a pane whose whole job is
    /// to be checkable.
    /// </remarks>
    public required IReadOnlyList<string> MissingSignalIds { get; init; }

    public bool HasMissingEvidence => MissingSignalIds.Count > 0;

    public string MissingEvidenceLabel =>
        $"Evidence referenced by this reason was not returned: {string.Join(", ", MissingSignalIds)}";

    internal static ReasonView From(ReasonResponse reason, IReadOnlyDictionary<string, EvidenceView> bySignal)
    {
        var resolved = new List<EvidenceView>();
        var missing = new List<string>();

        foreach (var signalId in reason.EvidenceSignalIds)
        {
            if (bySignal.TryGetValue(signalId, out var evidence)) resolved.Add(evidence);
            else missing.Add(signalId);
        }

        return new ReasonView
        {
            Code = reason.Code,
            Message = reason.Message,
            Evidence = resolved,
            MissingSignalIds = missing,
        };
    }
}

/// <summary>
/// One scored dimension of risk, with the availability that qualifies it.
/// </summary>
public sealed class DimensionView
{
    private DimensionView() { }

    public required string Name { get; init; }

    public required double Score { get; init; }

    public required EvidenceAvailability Availability { get; init; }

    /// <summary>
    /// Whether this dimension has a score to show at all.
    /// </summary>
    /// <remarks>
    /// <b>The whole point.</b> An unavailable dimension arrives with a score of
    /// zero, because that is what an enum-shaped payload does, and rendering
    /// that zero would say the semantic layer looked and found nothing. It
    /// never looked. The two conclusions are opposite and identical on screen
    /// unless this flag is respected.
    /// </remarks>
    public required bool HasScore { get; init; }

    public required string ScoreLabel { get; init; }

    /// <summary>Why the score is weaker than it looks, when it is.</summary>
    public required string Qualifier { get; init; }

    internal static DimensionView From(RiskDimensionResponse dimension)
    {
        var hasScore = dimension.Availability is EvidenceAvailability.Available
            or EvidenceAvailability.ReducedCoverage;

        return new DimensionView
        {
            Name = dimension.Name,
            Score = dimension.Score,
            Availability = dimension.Availability,
            HasScore = hasScore,
            ScoreLabel = hasScore
                ? dimension.Score.ToString("0.###", CultureInfo.InvariantCulture)
                : $"not measured ({Describe(dimension.Availability)})",
            Qualifier = dimension.Availability switch
            {
                EvidenceAvailability.ReducedCoverage =>
                    "Scored over reduced input coverage, so it is real but weaker.",
                EvidenceAvailability.Unavailable =>
                    "Could not be produced. This is not a negative result.",
                EvidenceAvailability.NotApplicable =>
                    "Does not apply to this message at all.",
                _ => string.Empty,
            },
        };
    }

    private static string Describe(EvidenceAvailability availability) => availability switch
    {
        EvidenceAvailability.Unavailable => "unavailable",
        EvidenceAvailability.NotApplicable => "not applicable",
        _ => availability.ToString().ToLowerInvariant(),
    };
}

/// <summary>One piece of evidence, with its provenance.</summary>
public sealed class EvidenceView
{
    private EvidenceView() { }

    public required string SignalId { get; init; }

    /// <summary>
    /// Where it came from, which decides how much authority it may carry. A
    /// model assessment and a computed fact are not the same kind of thing and
    /// must not look alike.
    /// </summary>
    public required string OriginLabel { get; init; }

    public required EvidenceAvailability Availability { get; init; }

    public double? Value { get; init; }

    public double? Confidence { get; init; }

    public int? SampleSupport { get; init; }

    public required string SourceVersion { get; init; }

    public required string ValueLabel { get; init; }

    public required string ConfidenceLabel { get; init; }

    /// <summary>
    /// The semantic answers carry no confidence field at all.
    /// </summary>
    /// <remarks>
    /// Measured against the live TypeSafe API on 2026-09-22: a Noul answer is
    /// <c>{ type, noul }</c> and nothing else. So a null confidence is the
    /// documented shape rather than data still to arrive, and the pane says
    /// "not reported" instead of showing an empty control an operator would
    /// wait on.
    /// </remarks>
    public required bool ConfidenceReported { get; init; }

    internal static EvidenceView From(EvidenceResponse evidence) => new()
    {
        SignalId = evidence.SignalId,
        OriginLabel = evidence.Origin.ToString(),
        Availability = evidence.Availability,
        Value = evidence.Value,
        Confidence = evidence.Confidence,
        SampleSupport = evidence.SampleSupport,
        SourceVersion = evidence.SourceVersion,
        ValueLabel = evidence.Availability is EvidenceAvailability.Available
            or EvidenceAvailability.ReducedCoverage
            ? evidence.Value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "no value"
            : "not produced",
        ConfidenceLabel = evidence.Confidence is { } confidence
            ? confidence.ToString("0.###", CultureInfo.InvariantCulture)
            : "not reported",
        ConfidenceReported = evidence.Confidence is not null,
    };
}

/// <summary>One coverage flag that was true, which qualifies every number above it.</summary>
public sealed record CoverageFlag(string Name, string Detail)
{
    internal static IReadOnlyList<CoverageFlag> From(CoverageResponse coverage)
    {
        var flags = new List<CoverageFlag>();

        // Only the true ones. BodyParsed is true on the ordinary path and is
        // the absence of a problem rather than a finding, so listing it would
        // train an operator to ignore this section.
        if (coverage.HtmlTextDisagreement)
        {
            flags.Add(new("html_text_disagreement", "The HTML and text parts disagree."));
        }

        if (coverage.ParserLimitExceeded)
        {
            flags.Add(new("parser_limit_exceeded", "A parser limit was reached, so part of the message was not examined."));
        }

        if (coverage.ContentEncrypted)
        {
            flags.Add(new("content_encrypted", "The content is encrypted or password-protected and could not be read."));
        }

        if (coverage.Truncated)
        {
            flags.Add(new("truncated", "The analysis covered only part of the message."));
        }

        if (coverage.ConversationContextMissing)
        {
            flags.Add(new("conversation_context_missing", "No conversation context was available for this message."));
        }

        if (coverage.HasAttachments)
        {
            flags.Add(new("has_attachments", "The message carries attachments. Their contents were not opened."));
        }

        if (!coverage.BodyParsed)
        {
            flags.Add(new("body_not_parsed", "No message body could be parsed at all."));
        }

        return flags;
    }
}

/// <summary>One version, which is what makes a ledger entry reproducible later.</summary>
public sealed record VersionEntry(string Label, string? Value)
{
    /// <summary>
    /// Whether the Host reported one. Null is meaningful rather than missing:
    /// a null classifier model means the semantic path did not run.
    /// </summary>
    public bool IsPresent => !string.IsNullOrWhiteSpace(Value);

    public string Display => IsPresent ? Value! : "not used for this message";

    internal static IReadOnlyList<VersionEntry> From(VersionsResponse versions) =>
    [
        new("Policy", versions.PolicyVersion),
        new("Classifier model", versions.ClassifierModelVersion),
        new("Question schema", versions.QuestionSchemaVersion),
        new("Preprocessing", versions.PreprocessingVersion),
        new("Regime", versions.RegimeId),
    ];
}

/// <summary>Whether this decision reused an earlier semantic assessment.</summary>
public sealed record CacheEntry(bool Hit, bool Stale, string KeyDigest, string? ModelVersion)
{
    public string Display => (Hit, Stale) switch
    {
        (false, _) => "No. The semantic layer was asked for this message.",
        (true, false) => $"Yes, reused an earlier assessment ({KeyDigest}).",
        (true, true) => $"Yes, but the cached assessment is stale ({KeyDigest}): the pinned model has moved since.",
    };

    internal static CacheEntry From(CacheResponse cache)
        => new(cache.Hit, cache.Stale, cache.KeyDigest, cache.ModelVersion);
}
