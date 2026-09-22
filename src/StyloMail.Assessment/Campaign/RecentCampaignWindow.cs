using StyloMail.Adaptive.Profiles;
using StyloMail.Assessment.Semantic;

namespace StyloMail.Assessment.Campaign;

/// <summary>
/// One recently seen message, reduced to what campaign comparison needs.
/// </summary>
/// <remarks>
/// <b>Note what is not here.</b> There is no <see cref="StyloMail.Core.SemanticAssessment"/>, no
/// evidence list and no verdict. The window keeps a bounded behavioural vector, a security-bearing
/// digest and an identifier — so the rule that near-duplicate matching supplies campaign evidence
/// and is never reused as an assessment is enforced by the type rather than by a comment. There is
/// nothing in this record that could be handed back as somebody else's answer.
/// </remarks>
public sealed record CampaignObservation
{
    public required string TenantId { get; init; }

    public required string AssessmentId { get; init; }

    public required string InternalMessageId { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    /// <summary>The semantic dimension vector, with masked dimensions explicitly absent.</summary>
    public required DimensionVector Vector { get; init; }

    /// <summary>
    /// What this message did, as opposed to how it read.
    /// </summary>
    /// <remarks>
    /// Retained so a later message that merely resembles this one can be told apart from one that
    /// <em>is</em> it. Wording is the cheapest thing for a campaign to change and the last thing
    /// worth comparing; destinations, payload hashes and sender context are the parts an attacker
    /// cannot vary without changing the attack.
    /// </remarks>
    public required SecurityBearingFingerprint Fingerprint { get; init; }

    /// <summary>Pseudonymised sender key, when one was available. Never a raw address.</summary>
    public string? SenderScope { get; init; }
}

/// <summary>One near match found in the recent window.</summary>
public sealed record CampaignMatch
{
    public required CampaignObservation Observation { get; init; }

    /// <summary>Similarity over the dimensions the two messages have in common, in [0, 1].</summary>
    public required double Similarity { get; init; }

    /// <summary>How many dimensions the comparison actually covered.</summary>
    public required int ComparedDimensions { get; init; }

    /// <summary>
    /// Whether the two messages do the same thing, not merely say the same thing.
    /// </summary>
    /// <remarks>
    /// False means this is a <em>variant</em>: the wording matches and the effect does not. That is
    /// the single most important distinction this window draws, because it is exactly the shape of
    /// a campaign that recycles a known-good template against a new destination.
    /// </remarks>
    public required bool SecurityBearingAgrees { get; init; }
}

/// <summary>
/// A bounded, tenant-scoped window of recently observed messages, for campaign comparison.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded on every axis, deliberately.</b> Capacity per tenant is a ceiling, retention is a
/// window, and no operation here grows with the number of things ever seen. A campaign detector
/// that accumulated without bound would be a slow memory leak driven by exactly the traffic an
/// attacker controls — the more mail they send, the more we retain.
/// </para>
///
/// <para>
/// <b>Comparison is over shared dimensions only.</b> A dimension the provider could not answer is
/// excluded rather than treated as zero, for the same reason it is excluded everywhere else in this
/// system: an outage must not read as calm. The count of compared dimensions is reported alongside
/// the similarity so a match over three dimensions is never mistaken for one over twelve.
/// </para>
///
/// <para>
/// Not durable, and not a system of record. It answers "does this look like something we saw
/// recently" within one process; like the semantic cache, losing it costs detection quality for one
/// deployment window rather than correctness.
/// </para>
/// </remarks>
public sealed class RecentCampaignWindow
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedList<CampaignObservation>> _byTenant = new(StringComparer.Ordinal);

    public RecentCampaignWindow(int capacityPerTenant = 512, TimeSpan? retention = null)
    {
        if (capacityPerTenant < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacityPerTenant), capacityPerTenant, "The window must be able to hold at least one observation.");
        }

        CapacityPerTenant = capacityPerTenant;
        Retention = retention ?? TimeSpan.FromHours(24);

        if (Retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), Retention, "Retention must be positive.");
        }
    }

    public int CapacityPerTenant { get; }

    public TimeSpan Retention { get; }

    /// <summary>Records an observation, evicting the oldest beyond capacity or retention.</summary>
    public void Record(CampaignObservation observation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(observation);

        lock (_gate)
        {
            if (!_byTenant.TryGetValue(observation.TenantId, out var window))
            {
                window = new LinkedList<CampaignObservation>();
                _byTenant[observation.TenantId] = window;
            }

            window.AddLast(observation);
            Expire(window, now);

            while (window.Count > CapacityPerTenant)
            {
                window.RemoveFirst();
            }
        }
    }

    /// <summary>
    /// The nearest recent observations for a message, excluding the message's own record.
    /// </summary>
    /// <param name="assessmentId">
    /// The record to exclude. The caller records the message before querying so that a burst
    /// arriving concurrently can see its own members; excluding by id is what keeps a message from
    /// matching itself at a similarity of exactly 1.
    /// </param>
    public IReadOnlyList<CampaignMatch> FindNear(
        string tenantId,
        DimensionVector vector,
        SecurityBearingFingerprint fingerprint,
        DateTimeOffset now,
        string? assessmentId,
        int k = 8,
        double minimumSimilarity = 0.85)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);

        lock (_gate)
        {
            if (!_byTenant.TryGetValue(tenantId, out var window))
            {
                return [];
            }

            Expire(window, now);

            var matches = new List<CampaignMatch>();

            foreach (var candidate in window)
            {
                if (assessmentId is not null
                    && string.Equals(candidate.AssessmentId, assessmentId, StringComparison.Ordinal))
                {
                    continue;
                }

                var (similarity, compared) = Compare(vector, candidate.Vector);

                if (compared == 0 || similarity < minimumSimilarity)
                {
                    continue;
                }

                matches.Add(new CampaignMatch
                {
                    Observation = candidate,
                    Similarity = similarity,
                    ComparedDimensions = compared,
                    SecurityBearingAgrees = string.Equals(
                        candidate.Fingerprint.Digest, fingerprint.Digest, StringComparison.Ordinal),
                });
            }

            return
            [
                .. matches
                    .OrderByDescending(match => match.Similarity)
                    .ThenByDescending(match => match.SecurityBearingAgrees)
                    .Take(k),
            ];
        }
    }

    /// <summary>Number of observations retained for one tenant. Exposed for tests and metrics.</summary>
    public int CountFor(string tenantId)
    {
        lock (_gate)
        {
            return _byTenant.TryGetValue(tenantId, out var window) ? window.Count : 0;
        }
    }

    private void Expire(LinkedList<CampaignObservation> window, DateTimeOffset now)
    {
        var cutoff = now - Retention;

        while (window.First is { } first && first.Value.ObservedAt < cutoff)
        {
            window.RemoveFirst();
        }
    }

    /// <summary>
    /// Mean absolute difference over the dimensions both vectors produced, inverted to a
    /// similarity.
    /// </summary>
    /// <remarks>
    /// A simple, explainable metric over a small bounded vector, chosen because it can be described
    /// in the ledger — "these two agree on eleven of twelve dimensions to within 0.04" — which a
    /// cosine similarity over an embedding this size cannot. Distances are not multiplied or
    /// compounded: the dimensions are correlated, and treating them as independent evidence is the
    /// mistake the scoring rules elsewhere in this system exist to avoid.
    /// </remarks>
    private static (double Similarity, int Compared) Compare(DimensionVector left, DimensionVector right)
    {
        var total = 0.0;
        var compared = 0;

        foreach (var sample in left.Dimensions)
        {
            if (sample.Value is not { } leftValue)
            {
                continue;
            }

            if (right.ValueOf(sample.DimensionId) is not { } rightValue)
            {
                continue;
            }

            total += Math.Abs(leftValue - rightValue);
            compared++;
        }

        if (compared == 0)
        {
            return (0.0, 0);
        }

        return (1.0 - total / compared, compared);
    }
}
