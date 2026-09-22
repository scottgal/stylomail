using StyloMail.Core;

namespace StyloMail.Assessment.Semantic;

/// <summary>
/// How much of a semantic answer was actually produced, and how much of it is missing.
/// </summary>
/// <remarks>
/// Retained alongside a cached assessment because an entry read back tomorrow must be readable as
/// what it was. A cached result whose dimensions were half unavailable is a weaker piece of
/// evidence than one that answered everything, and a cache that discarded that distinction would
/// launder an outage into a clean-looking hit.
/// </remarks>
public sealed record SemanticCoverage
{
    public required int Available { get; init; }

    public required int ReducedCoverage { get; init; }

    public required int Unavailable { get; init; }

    public required int NotApplicable { get; init; }

    public required int Total { get; init; }

    /// <summary>Fraction of dimensions that produced a usable value, in [0, 1].</summary>
    public double CoveredFraction =>
        Total == 0 ? 0.0 : (double)(Available + ReducedCoverage) / Total;

    /// <summary>True when at least one dimension was asked and could not be answered.</summary>
    public bool HasUnavailable => Unavailable > 0;

    public static SemanticCoverage From(IReadOnlyList<Evidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var available = 0;
        var reduced = 0;
        var unavailable = 0;
        var notApplicable = 0;

        foreach (var item in evidence)
        {
            switch (item.Availability)
            {
                case EvidenceAvailability.Available: available++; break;
                case EvidenceAvailability.ReducedCoverage: reduced++; break;
                case EvidenceAvailability.Unavailable: unavailable++; break;
                case EvidenceAvailability.NotApplicable: notApplicable++; break;
                default: unavailable++; break;
            }
        }

        return new SemanticCoverage
        {
            Available = available,
            ReducedCoverage = reduced,
            Unavailable = unavailable,
            NotApplicable = notApplicable,
            Total = evidence.Count,
        };
    }
}

/// <summary>
/// One memoised semantic assessment, with everything needed to decide whether it may still be
/// served and to explain that it was.
/// </summary>
/// <remarks>
/// <b>What is stored is evidence, and only evidence.</b> There is no action, no disposition and no
/// sender verdict in this record, which is what makes "never memoise allow this sender" a property
/// of the structure rather than a rule to remember. Every message that hits this entry still goes
/// through current authentication, URL, counter, profile and policy checks, the cache removes one
/// provider call and nothing else.
/// </remarks>
public sealed record CachedSemanticAssessment
{
    /// <summary>The key this entry was stored under, retained so a hit can be audited against it.</summary>
    public required string KeyDigest { get; init; }

    /// <summary>The evidence distribution as the provider produced it, with its original timestamps.</summary>
    public required IReadOnlyList<Evidence> Evidence { get; init; }

    /// <summary>Coverage at the time of classification. Never recomputed from the evidence alone.</summary>
    public required SemanticCoverage Coverage { get; init; }

    /// <summary>When the provider answered. Survives the cache, a hit does not re-date the evidence.</summary>
    public required DateTimeOffset CachedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The model the provider reported it actually resolved, not the one we asked for.</summary>
    public required string? ResolvedModelVersion { get; init; }

    public required string QuestionSchemaVersion { get; init; }

    public required string PreprocessingVersion { get; init; }

    /// <summary>
    /// What this entry's message actually did, so a later message that merely reads like it can be
    /// told apart from one that is the same. See <see cref="SecurityBearingFingerprint"/>.
    /// </summary>
    public required SecurityBearingFingerprint Fingerprint { get; init; }

    /// <summary>Untrusted, message-supplied correlation only. Recorded for the ledger, never a key.</summary>
    public string? UntrustedMessageIdHeader { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public bool IsExpiredAt(DateTimeOffset now) => now >= ExpiresAt;
}
