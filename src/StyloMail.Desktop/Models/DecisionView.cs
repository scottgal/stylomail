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

    /// <summary>Which channel the message came from.</summary>
    public required string ChannelLabel { get; init; }

    /// <summary>
    /// Whether anything could have been done, said plainly.
    /// </summary>
    /// <remarks>
    /// <b>The field that changes what the rest of the pane means.</b> A decision
    /// taken after the platform had already delivered the message had no action
    /// available that could have stopped it, and every action it does name is
    /// post-hoc. Rendering that like any other decision would tell an operator
    /// the system could have intervened when it only reacted.
    ///
    /// <para>
    /// Null when the decision was taken in the delivery path, because there is
    /// nothing to qualify: the ordinary case needs no banner.
    /// </para>
    /// </remarks>
    public string? PostDeliveryCaveat { get; init; }

    /// <summary>Whether the caveat applies, for a visibility binding.</summary>
    public bool IsPostDelivery => PostDeliveryCaveat is not null;

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

    /// <summary>
    /// What the index was divided by, or that it was not recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Served beside the index rather than left to the reader. An index an
    /// operator cannot check from what is on screen is one they have to take on
    /// trust, and the check is the whole reason the denominator is on the wire.
    /// </para>
    /// <para>
    /// <b>Always a sentence, including when the answer is "not recorded".</b>
    /// Null and zero are different facts: a served <c>0</c> is a measured empty
    /// arithmetic, a served <c>null</c> is a decision made before the
    /// arithmetic was recorded. Rendering the second as the first would report
    /// an unrecorded index as an empty one, so the absent case is said rather
    /// than left blank.
    /// </para>
    /// </remarks>
    public required string RiskIndexArithmetic { get; init; }

    /// <summary>
    /// The share of the asked weight that carried weight, said as a sentence,
    /// because it is the number the refusal text quotes and a pane showing only
    /// the divisor cannot recover it.
    /// </summary>
    /// <remarks>
    /// <b>Always a sentence, including when the answer is "not recorded".</b>
    /// Null is neither zero nor one on this field: a served <c>0</c> and a
    /// served <c>1</c> are both measurements, at the two ends of the range, and
    /// a served <c>null</c> is a decision made by a build that did not record
    /// the fraction. Rendering the absent case as <c>0</c> would report an
    /// unrecorded share as a measured empty one, which is the failure the
    /// neighbouring divisor's remarks exist to prevent.
    /// </remarks>
    public required string CoveredWeightArithmetic { get; init; }

    public required IReadOnlyList<ReasonView> Reasons { get; init; }

    public required IReadOnlyList<DimensionView> Dimensions { get; init; }

    public required IReadOnlyList<EvidenceView> Evidence { get; init; }

    public required IReadOnlyList<CoverageFlag> Coverage { get; init; }

    public required IReadOnlyList<VersionEntry> Versions { get; init; }

    public CacheEntry? Cache { get; init; }

    /// <summary>
    /// How many decisions the ledger holds for this message.
    /// </summary>
    /// <remarks>
    /// On the view rather than only on the window's model, because the pane's
    /// content is bound to a <see cref="DecisionView"/> and a property its
    /// DataContext does not have fails silently: the binding leaves the control
    /// at its default, which for a visibility binding is <em>visible</em>. That
    /// is how this rendered as an empty amber bar rather than not at all.
    /// </remarks>
    public int DecisionCount { get; init; } = 1;

    /// <summary>Says when a message has been assessed more than once.</summary>
    public string? HistoryNote => DecisionCount > 1
        ? $"This message has been assessed {DecisionCount} times. Showing the most recent."
        : null;

    public bool HasHistory => HistoryNote is not null;

    public static DecisionView From(DecisionResponse decision, int decisionCount = 1)
    {
        ArgumentNullException.ThrowIfNull(decision);

        // A lookup, not a dictionary. A real Host sent the same signal id twice
        // -- one row per observed scope -- and ToDictionary throws on a
        // duplicate key, which took the whole pane down: the operator saw
        // nothing at all. Every fixture had unique ids, so no unit test could
        // have caught it.
        var bySignal = decision.Evidence.ToLookup(
            evidence => evidence.SignalId,
            EvidenceView.From,
            StringComparer.Ordinal);

        return new DecisionView
        {
            DecisionCount = decisionCount,
            AssessmentId = decision.AssessmentId,
            ActionLabel = decision.Action.ToString(),
            ChannelLabel = decision.Channel.Kind.ToString(),

            // Only said when it is true. A banner that appears on every decision
            // is one an operator stops reading, and the whole value of this one
            // is that it is exceptional.
            PostDeliveryCaveat = decision.DeliveryTiming is DeliveryTiming.PostDelivery
                ? "Seen after delivery. The platform had already delivered this message, so every "
                    + "action available here is post-hoc: nothing in this decision could have "
                    + "stopped it."
                : null,
            ShadowLabel = decision.ProposedActionInShadow is { } proposed
                ? $"Would have been {proposed}"
                : null,
            RiskIndexLabel = string.Create(
                CultureInfo.InvariantCulture,
                $"risk index {decision.RiskIndex:0.###} (an index, not a probability)"),

            // The denominator, said even when it is absent, because "not
            // recorded" and "zero" are the two readings this field exists to
            // keep apart.
            RiskIndexArithmetic = decision.RiskIndexDenominator is { } denominator
                ? "Divided by a counted weight of "
                    + string.Create(CultureInfo.InvariantCulture, $"{denominator:0.###}")
                    + ": this index is the summed score of the rows that entered it."
                : "The weight this index was divided by was not recorded for this decision, so it "
                    + "cannot be checked from what is here.",

            // The fraction the refusal text quotes, and the same two readings to
            // keep apart: zero and one are measurements, null is an absence.
            // Three readings, and the ZERO is the one that reassures rather than
            // alarms: the engine's divisions are guarded (CompositeRiskScorer
            // :275-276), so nothing counted yields an index of 0.0, and a fully
            // masked message then reads exactly like a measured-benign one on the
            // number a reader looks at first. The sentence says so, because the
            // engine is already safe here (MailPolicyEngine gates coverage 0.0
            // against MinimumCoverageForAllow) and the pane is where a reader has
            // to be told.
            CoveredWeightArithmetic = decision.CoveredWeightFraction is not { } covered
                ? "The share of the asked weight that was counted was not recorded for this decision, "
                    + "so it cannot be checked from what is here."
                : covered > 0
                    ? "Of the weight this decision asked about, "
                        + string.Create(CultureInfo.InvariantCulture, $"{covered:0.###}")
                        + " was counted: this is the share the refusal text quotes."
                    : "None of the weight this decision asked about was counted, so the risk index "
                        + "beside this is NOT a measurement: a fully masked message and a "
                        + "measured-benign one read the same here.",
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

    internal static ReasonView From(ReasonResponse reason, ILookup<string, EvidenceView> bySignal)
    {
        var resolved = new List<EvidenceView>();
        var missing = new List<string>();

        foreach (var signalId in reason.EvidenceSignalIds)
        {
            var matches = bySignal[signalId].ToList();

            // Every row sharing the id, not the first. Dropping one would be
            // the console editing the Host's answer, and the rows are
            // distinguishable: they carry different scopes.
            if (matches.Count == 0) missing.Add(signalId);
            else resolved.AddRange(matches);
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

    /// <summary>This row's weight in the index, when the Host recorded one.</summary>
    public double? Weight { get; init; }

    /// <summary>
    /// Whether this row entered the index at all, or null when the decision
    /// predates the flag.
    /// </summary>
    /// <remarks>
    /// Kept raw as well as said in <see cref="ContributionLabel"/>, so the state
    /// stays assertable without parsing a sentence back apart.
    /// </remarks>
    public bool? Counted { get; init; }

    /// <summary>Why this row did not count, when the Host gave a reason.</summary>
    public string? ExclusionReason { get; init; }

    /// <summary>
    /// What this row did to the index, when that is not already on the row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The distinction this row could not make without it.</b> A dimension
    /// that was measured and came back 0.0 was counted, and it dilutes the
    /// index; a dimension decision 31 masked contributed nothing. Both arrive
    /// with the same score and the same availability, so both used to render as
    /// the same "0", and a reader recomputing the arithmetic from the pane
    /// would add the masked row's weight back in and get a number that agrees
    /// with a shape the system no longer has.
    /// </para>
    /// <para>
    /// Empty wherever saying it would only repeat what the row already says. A
    /// counted row is the ordinary case and a marker on every row is one nobody
    /// reads; and an unmasked-but-unmeasured row already reads "not measured",
    /// which is not a contribution and cannot be mistaken for one. Suppressing
    /// it there is what keeps the marker meaningful on the rows that need it:
    /// the ones showing a number that did not enter the sum.
    /// </para>
    /// <para>
    /// Null is a third state and is <b>never</b> suppressed, however the row
    /// reads: a row whose share was not recorded cannot be shown as one that did
    /// not count, and showing half of a pair invites a reader to guess the other
    /// half.
    /// </para>
    /// </remarks>
    public required string ContributionLabel { get; init; }

    public bool HasContributionNote => ContributionLabel.Length > 0;

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
            Weight = dimension.Weight,
            Counted = dimension.Counted,
            ExclusionReason = dimension.ExclusionReason,
            ContributionLabel = Contribution(
                dimension.Counted, dimension.Weight, dimension.ExclusionReason, hasScore),
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

    /// <summary>
    /// What this row contributed, or why it contributed nothing.
    /// </summary>
    /// <remarks>
    /// The weight is named on the rows that did NOT count, because that is the
    /// number a reader would otherwise add back in. On a counted row the weight
    /// is part of the denominator already stated above the list, and the row has
    /// nothing to warn about.
    /// </remarks>
    private static string Contribution(
        bool? counted, double? weight, string? exclusionReason, bool hasScore)
    {
        switch (counted)
        {
            // The ordinary case. A marker here would be on every row of every
            // decision, which is how a marker stops being read.
            case true:
                return string.Empty;

            // Masked, and the row shows a number anyway: this is the row a
            // reader would add into the divisor, so it has to say otherwise.
            // Where there is no score to show, the row already reads "not
            // measured", which no contributor's row says, and repeating it as a
            // sentence would put the same line on every row of a decision taken
            // while the semantic layer was down.
            case false when hasScore || !string.IsNullOrWhiteSpace(exclusionReason):
            {
                var reason = string.IsNullOrWhiteSpace(exclusionReason)
                    ? "Not counted towards the index."
                    : $"Not counted towards the index: {Sentence(exclusionReason)}";

                return weight is { } excluded
                    ? reason + string.Create(
                        CultureInfo.InvariantCulture,
                        $" Its weight of {excluded:0.###} is not in the divisor.")
                    : reason;
            }

            case false:
                return string.Empty;

            // Never suppressed, however the row reads. The flag and the weight
            // were served as a pair and only half of it is here, so the row says
            // so rather than leaving a reader to conclude it did not count.
            default:
                return "This row's share of the index was not recorded for this decision.";
        }
    }

    /// <summary>
    /// Ends a clause the Host wrote, without giving it a second full stop.
    /// </summary>
    /// <remarks>
    /// A Host reason is a sentence and ends in one. Appending another produced
    /// "Masked by the trusted-history rule.." on the fixture, and this sentence's
    /// whole value is that a reader checking the divisor reads it carefully: a
    /// doubled stop reads as a typo and costs the line the trust it needs.
    /// </remarks>
    private static string Sentence(string clause)
    {
        var trimmed = clause.TrimEnd();

        return trimmed.EndsWith('.') ? trimmed : trimmed + ".";
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

    /// <summary>
    /// What the observation was scoped to, when the Host said.
    /// </summary>
    /// <remarks>
    /// The field that makes two rows sharing a signal id tellable apart: a
    /// behavioural signal observed for a sender and for a recipient can carry
    /// the same id and different values, and a pane that showed them as one row
    /// would report one number where there were two.
    /// </remarks>
    public string? ObservedScope { get; init; }

    /// <summary>
    /// The producer's trend window for this row, or null when the signal is not
    /// windowed.
    /// </summary>
    /// <remarks>
    /// The third part of a signal's identity, beside <see cref="SignalId"/> and
    /// <see cref="ObservedScope"/>: two rows agreeing on both of those are told
    /// apart by this and by nothing else. Kept raw as well as composed into
    /// <see cref="ScopeLabel"/> so the identity stays expressible without
    /// parsing the label back apart.
    /// </remarks>
    public string? Window { get; init; }

    /// <summary>
    /// Which slice of what this signal describes, as one muted qualifier on the
    /// row: the scope where the Host gave one, and the producer's trend window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The label that makes two rows renderable as two rows. A signal id is not
    /// unique within an assessment: the behavioural evaluator emits one row per
    /// trend window, so two rows can agree on the id, on the origin and on the
    /// value label and differ only here. Without this the pane drew them
    /// identically and an operator could not tell a burst from a slow trend.
    /// </para>
    /// <para>
    /// <b>Null when neither applies, and null is a fact rather than a gap.</b>
    /// Every semantic row and every drift row is legitimately unwindowed, which
    /// is most of the list, so the row renders nothing here instead of the word
    /// "unknown". A qualifier claiming a gap where there is none is the same
    /// mistake as rendering an unavailable dimension as zero.
    /// </para>
    /// </remarks>
    public string? ScopeLabel { get; init; }

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
        ObservedScope = evidence.ObservedScope,
        Window = evidence.Window,
        ScopeLabel = Qualifier(evidence.ObservedScope, evidence.Window),
        ValueLabel = evidence.Availability is EvidenceAvailability.Available
            or EvidenceAvailability.ReducedCoverage
            ? evidence.Value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "no value"
            : "not produced",
        ConfidenceLabel = evidence.Confidence is { } confidence
            ? confidence.ToString("0.###", CultureInfo.InvariantCulture)
            : "not reported",
        ConfidenceReported = evidence.Confidence is not null,
    };

    /// <summary>
    /// Composes the row's scope qualifier, or null when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// The two parts are joined rather than split into columns because they
    /// answer one question, which slice of the signal this row is, and a reader
    /// takes them in together. Blank counts as absent: a whitespace window is
    /// the Host having nothing to say, not a window named " ".
    /// </remarks>
    private static string? Qualifier(string? scope, string? window)
    {
        var parts = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(scope))
        {
            parts.Add(scope);
        }

        if (!string.IsNullOrWhiteSpace(window))
        {
            parts.Add(window);
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
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
