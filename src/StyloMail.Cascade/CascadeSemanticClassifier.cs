using System.Security.Cryptography;
using System.Text;
using StyloMail.Core;

namespace StyloMail.Cascade;

/// <summary>
/// Asks a local decision model first, and a stronger hosted one only for the dimensions the rule says
/// the local answer cannot be taken on.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a decorator over the provider port, not a change to the assessor.</b> It implements
/// <see cref="ISemanticMailClassifier"/>, so what it returns is evidence and it cannot express an
/// action, a disposition or a permission. The only path from its output to an intervention is the one
/// that already exists: evidence to policy. The approach in
/// <c>CascadeEscalationRule</c> is the entire mechanism, and it is testable on its own.
/// </para>
/// <para>
/// <b>The cascade changes one thing: which model produced a given row.</b> The returned row set is the
/// local arm's, in the local arm's order, with the escalated rows replaced by the strong model's. No
/// row is added, removed or renamed, and <c>NotApplicable</c> rows pass through untouched, so the
/// coverage arithmetic downstream is the arithmetic of the question set and not of this component's
/// housekeeping.
/// </para>
/// <para>
/// <b>A confident local answer costs nothing, and that is a structural property rather than a
/// promise.</b> When the rule escalates nothing this method returns the local arm's own
/// <see cref="SemanticAssessment"/> instance unchanged, so there is no assembly step that could
/// quietly alter it.
/// </para>
/// <para>
/// <b>A fault propagates; a reading is recorded.</b> A contract exception from either arm (a rejected
/// credential, a request shape the provider refused) is a configuration fault and is NOT caught here:
/// a deployment that selected the cascade with a broken hosted arm must be visibly broken, which is
/// what the hosted provider's own credential handling already does, rather than degrading to
/// local-only with an attribute nobody reads. A transient failure arrives from the adapters as
/// <c>Unavailable</c> rows rather than as an exception, and that IS recorded: the row keeps the local
/// answer if there was one and carries the escalation attempt in its site.
/// </para>
/// <para>
/// <b>No request or response body is logged here, and nothing in this file reads message content.</b>
/// The rule is a function of the answers, not of the mail.
/// </para>
/// </remarks>
public sealed class CascadeSemanticClassifier : ISemanticMailClassifier
{
    /// <summary>
    /// The attribute a returned row carries when the strong model was asked for it.
    /// </summary>
    /// <remarks>
    /// <b>Namespaced deliberately, and NOT the name <c>reason</c>.</b> The console's decision
    /// projection publishes every attribute named <c>reason</c> as one of a row's AVAILABILITY reasons
    /// (<c>DecisionResponse.AvailabilityReasonsOf</c>), which is the answer to "why is this value
    /// weaker". An escalation is the answer to a different question (who else was asked, and why), and
    /// merging the two would put a strong-model call inside a list a reader consults to explain a
    /// value's strength. The cost of the distinct name is that today's projection does not publish it;
    /// that gap is named in the design note rather than papered over by overloading a name.
    /// </remarks>
    public const string EscalationAttribute = "cascade.escalation";

    private readonly ISemanticMailClassifier _local;
    private readonly ISemanticMailClassifier _hosted;
    private readonly CascadeOptions _options;
    private readonly Func<SemanticMailInput, IReadOnlyDictionary<string, double>?>? _priorLocalValues;

    public CascadeSemanticClassifier(
        ISemanticMailClassifier local,
        ISemanticMailClassifier hosted,
        CascadeOptions? options = null,
        Func<SemanticMailInput, IReadOnlyDictionary<string, double>?>? priorLocalValues = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(hosted);

        _local = local;
        _hosted = hosted;
        _options = options ?? new CascadeOptions();
        _priorLocalValues = priorLocalValues;

        _options.Validate();
    }

    public async ValueTask<SemanticAssessment> ClassifyAsync(
        SemanticMailInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var local = await _local.ClassifyAsync(input, cancellationToken).ConfigureAwait(false);

        var prior = _priorLocalValues?.Invoke(input);
        var decision = CascadeEscalationRule.Decide(input, local.Evidence, _options, prior);

        if (!decision.Escalates)
        {
            // The normal path: the local answer is taken, whole, at no cost to the strong model.
            return local;
        }

        // Only the escalated dimensions are put to the strong model. The question set it is asked is
        // therefore smaller than the one the local model was asked, which is written onto the request
        // and is what the measurement reads as dimensions not escalated.
        var hostedInput = input with { Dimensions = decision.Escalated };
        var hosted = await _hosted.ClassifyAsync(hostedInput, cancellationToken).ConfigureAwait(false);

        var usable = UsableById(hosted.Evidence);
        var rows = new List<Evidence>(local.Evidence.Count);

        foreach (var row in local.Evidence)
        {
            if (!decision.Reasons.TryGetValue(row.SignalId, out var reasons))
            {
                rows.Add(row);
                continue;
            }

            rows.Add(usable.TryGetValue(row.SignalId, out var hostedRow)
                ? WithSite(hostedRow, reasons, hostedUnavailable: false)
                : WithSite(row, reasons, hostedUnavailable: true));
        }

        return new SemanticAssessment
        {
            Evidence = rows,

            // The cascade's own version, never a model id: see CascadeOptions.ClassifierVersion for
            // the cache contract that makes a per-message model id unusable here. Which model answered
            // a given dimension is on that row's SourceVersion.
            ResolvedModelVersion = _options.ClassifierVersion,

            Cache = BuildCacheProvenance(local, hosted),

            // The sum of both arms, because both calls happened, and null when neither reported. A
            // zero here would read as a measurement of an empty request.
            InputTokens = Sum(local.InputTokens, hosted.InputTokens),
            OutputTokens = Sum(local.OutputTokens, hosted.OutputTokens),
        };
    }

    /// <summary>The hosted rows that carry a value this cascade is willing to substitute.</summary>
    /// <remarks>
    /// A row counts only if the strong model actually answered it. A cut read counts: it is the strong
    /// model's answer over less text, which is still more than no answer, and the row keeps its own
    /// producer's reason attribute so a reader sees the cut. An <c>Unavailable</c> or
    /// <c>NotApplicable</c> row does not count, and the caller falls back to the local row.
    /// </remarks>
    private static Dictionary<string, Evidence> UsableById(IReadOnlyList<Evidence> hosted)
    {
        var usable = new Dictionary<string, Evidence>(StringComparer.Ordinal);

        foreach (var row in hosted)
        {
            if (row.Availability == EvidenceAvailability.Available && row.Value is not null)
            {
                usable.TryAdd(row.SignalId, row);
            }
        }

        return usable;
    }

    /// <summary>Attaches the site: why this dimension was asked, and whether the ask was answered.</summary>
    private static Evidence WithSite(
        Evidence row,
        IReadOnlyList<EscalationReason> reasons,
        bool hostedUnavailable)
    {
        var token = string.Join(',', reasons.Select(reason => reason.Token()));
        if (hostedUnavailable)
        {
            token = token + ";" + EscalationReasonTokens.HostedUnavailable;
        }

        var attributes = new List<EvidenceAttribute>((row.Attributes?.Count ?? 0) + 1);
        if (row.Attributes is { } existing)
        {
            attributes.AddRange(existing);
        }

        attributes.Add(new EvidenceAttribute { Name = EscalationAttribute, Value = token });

        return row with { Attributes = attributes };
    }

    /// <summary>
    /// Provenance for an assessment assembled from two calls.
    /// </summary>
    /// <remarks>
    /// <b>The digest names neither arm's, because the answer is a function of both.</b> A cache key
    /// that could be mistaken for either arm's would let an entry taken under one arm answer for the
    /// other, which is the aliasing the per-arm digests exist to prevent. The two source digests are
    /// inputs rather than the value, so a change underneath either arm moves this one.
    /// </remarks>
    private CacheProvenance BuildCacheProvenance(SemanticAssessment local, SemanticAssessment hosted)
    {
        var canonical = string.Concat(
            _options.ClassifierVersion,
            "\n",
            local.Cache.KeyDigest,
            "\n",
            hosted.Cache.KeyDigest);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return new CacheProvenance
        {
            Hit = false,
            KeyDigest = "cascade:" + Convert.ToHexStringLower(hash),
            CachedAt = null,
            ModelVersion = _options.ClassifierVersion,
            Stale = false,
        };
    }

    private static int? Sum(int? first, int? second)
        => first is null && second is null ? null : (first ?? 0) + (second ?? 0);
}
