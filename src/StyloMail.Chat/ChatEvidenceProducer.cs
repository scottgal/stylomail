using System.Globalization;
using StyloMail.Core;

namespace StyloMail.Chat;

/// <summary>
/// Produces the deterministic evidence a chat message supports, with no network call and no model.
/// </summary>
/// <remarks>
/// <para>
/// <b>The chat sibling of the MIME adapter's deterministic extraction.</b> It reads the links the
/// message contained and reports what the shared analysis made of them. The link and homograph
/// judgement is not written here: it is <see cref="LinkAnalysis"/>, which is where it moved to once
/// it was clear that a link lure is a link lure on every channel.
/// </para>
/// <para>
/// <b>No semantic evidence and no behaviour.</b> The classifier is opt-in and local-only by default,
/// and the author's behavioural profile is the assessment path's to add. What this produces is what
/// can be established from the message alone.
/// </para>
/// </remarks>
public static class ChatEvidenceProducer
{
    /// <summary>How many links one message's evidence may consider.</summary>
    private const int MaxLinksConsidered = 256;

    /// <summary>How many example attributes one signal may carry, so one message cannot inflate the ledger.</summary>
    private const int MaxExamples = 5;

    private const int MaxAttributeValueLength = 128;

    public static IReadOnlyList<Evidence> Produce(ChatAnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // The input carries observations; the judgement is made here, by handing the same
        // label-and-destination pairs to the shared analysis that the mail path uses. No plain text
        // is passed because the input's links already include everything the body contained.
        var links = LinkAnalysis.Extract(
            input.Links.Select(link => (link.ActualTarget, link.DisplayedText)),
            plainText: string.Empty,
            MaxLinksConsidered);

        var evidence = new List<Evidence>
        {
            DisplayMismatch(links, input.OccurredAt),
            Idn(links, input.OccurredAt),
            Homographs(links, input.OccurredAt),
        };

        return evidence;
    }

    private static Evidence DisplayMismatch(IReadOnlyList<LinkFinding> links, DateTimeOffset observedAt)
    {
        var mismatched = links.Where(l => l.DisplayMismatch).ToList();
        var labelled = links.Where(l => l.LabelMakesHostClaim).ToList();

        return Build(
            ChatSignals.LinkDisplayMismatch,
            links.Count == 0 ? EvidenceAvailability.NotApplicable : EvidenceAvailability.Available,
            observedAt,

            // A ratio rather than a count, because one mismatch among one labelled link and one among
            // fifty are not the same claim.
            value: labelled.Count == 0 ? null : (double)mismatched.Count / labelled.Count,
            attributes:
            [
                Of("linkCount", links.Count.ToString(CultureInfo.InvariantCulture)),
                Of("labelledLinkCount", labelled.Count.ToString(CultureInfo.InvariantCulture)),
                Of("mismatchCount", mismatched.Count.ToString(CultureInfo.InvariantCulture)),
                .. mismatched.Take(MaxExamples).Select(l => Of(
                    l.MismatchKind ?? "mismatch",
                    $"{Shorten(l.DisplayedText, 64)} -> {Shorten(l.ActualTarget, 96)}")),
            ]);
    }

    private static Evidence Idn(IReadOnlyList<LinkFinding> links, DateTimeOffset observedAt)
    {
        var idnLinks = links.Where(l => l.Idn is not null).ToList();

        return Build(
            ChatSignals.LinkIdn,
            idnLinks.Count == 0 ? EvidenceAvailability.NotApplicable : EvidenceAvailability.Available,
            observedAt,
            value: idnLinks.Count,
            attributes: [.. idnLinks.Take(MaxExamples).Select(l => Of(
                "idn",
                $"{l.Idn!.UnicodeHost} ({l.Idn.AsciiHost})"))]);
    }

    private static Evidence Homographs(IReadOnlyList<LinkFinding> links, DateTimeOffset observedAt)
    {
        var idnLinks = links.Where(l => l.Idn is not null).ToList();
        var homographs = idnLinks.Where(l => l.Idn!.Confusables.Count > 0 || l.Idn.IsMixedScript).ToList();

        return Build(
            ChatSignals.LinkIdnHomograph,

            // NotApplicable on no internationalised links rather than on no homographs, so that "we
            // saw plain ASCII hosts" and "there were no links at all" stay distinguishable. A zero
            // here is a real finding: we looked at internationalised hosts and they were clean.
            idnLinks.Count == 0 ? EvidenceAvailability.NotApplicable : EvidenceAvailability.Available,
            observedAt,
            value: homographs.Count,
            attributes: [.. homographs.Take(MaxExamples).Select(l => Of(
                "homograph",
                $"{l.Idn!.UnicodeHost} looks like {l.Idn.AsciiSkeleton} " +
                $"[scripts={string.Join(",", l.Idn.Scripts)} mixed={l.Idn.IsMixedScript} " +
                $"confusables={string.Join("/", l.Idn.Confusables.Take(4))}]"))]);
    }

    /// <summary>
    /// Stamps a signal with this producer's origin, version and observation time.
    /// </summary>
    /// <remarks>
    /// Centralised so <see cref="EvidenceOrigin.Deterministic"/> cannot be forgotten on one signal
    /// and quietly turn a reproducible fact into something a policy might treat as a model's opinion.
    /// Confidence is always null here for the same reason: nothing in this class is a model.
    /// </remarks>
    private static Evidence Build(
        string signalId,
        EvidenceAvailability availability,
        DateTimeOffset observedAt,
        double? value = null,
        IReadOnlyList<EvidenceAttribute>? attributes = null) =>
        new()
        {
            SignalId = signalId,
            Origin = EvidenceOrigin.Deterministic,
            Availability = availability,
            Value = value,
            Confidence = null,
            SourceVersion = ChatSignals.SourceVersion,
            ObservedAt = observedAt,
            ObservedScope = "message",

            // Truncated rather than dropped, and repeated names are kept: several of these signals
            // are genuinely multi-valued, and collapsing them would silently lose evidence.
            Attributes = attributes is null or { Count: 0 }
                ? null
                : [.. attributes.Select(a => a with { Value = Shorten(a.Value, MaxAttributeValueLength) })],
        };

    private static EvidenceAttribute Of(string name, string value) => new() { Name = name, Value = value };

    private static string Shorten(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "...");
}
