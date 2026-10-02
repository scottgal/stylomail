namespace StyloMail.Cascade;

/// <summary>The cascade's settings: the two thresholds, the trust table, and the version it reports.</summary>
/// <remarks>
/// <para>
/// <b>Every threshold here has exactly one home.</b> The indecisive band lives in
/// <see cref="CascadeTrust"/> and the run-disagreement tolerance lives here, and neither is a
/// comparison written out at a call site. A threshold with two homes is a threshold that drifts, and
/// the drift shows up as a rule whose behaviour cannot be predicted from its table.
/// </para>
/// <para>
/// <b>Neither threshold is read from configuration in this first cut.</b> A band an operator can move
/// without a test is a band nobody can cite when a figure is compared against another lane's.
/// </para>
/// </remarks>
public sealed class CascadeOptions
{
    /// <summary>
    /// The default tolerance above which two local runs on the same input count as disagreeing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>STATED, and chosen below a measurement rather than above it.</b> Two hosts running this
    /// model at the same tag and digest, given byte-identical requests, answered with a maximum
    /// absolute difference of <b>2.270e-02</b> across 78 probabilities (measured by `nimble-`,
    /// `pair-{local,point15}.json`, quoted in `docs/running.md` section 6). A tolerance of 0.01 is
    /// below that spread, so the one disagreement this fleet has actually measured WOULD fire this
    /// condition. A tolerance above 2.270e-02 would be a detector that cannot detect the only
    /// disagreement anyone has observed.
    /// </para>
    /// <para>
    /// <b>And a repeat call cannot substitute for it.</b> The control inside that same run answered
    /// one body twice on each host and came back bit-identical, so a single host is deterministic on
    /// a repeated identical request. This condition is therefore fed by a PRIOR answer from a
    /// different run or a different endpoint, never by asking the same host twice.
    /// </para>
    /// </remarks>
    public const double DefaultDisagreementTolerance = 0.01;

    /// <summary>
    /// The version this deployment's cascade reports as its resolved model version.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately not a model id, and the reason is a cache contract rather than a preference.</b>
    /// <c>InMemorySemanticCacheStore.IsVersionCompatible</c> serves a stored entry only when
    /// <c>entry.ResolvedModelVersion == options.ClassifierModelVersion</c> (read at
    /// <c>ISemanticCacheStore.cs:199-206</c>, 2026-10-02). A cascade that reported the answering model
    /// per message would fail that equality on every message whose dimensions were answered by both
    /// arms, so the version check would invalidate entry after entry and the cache would stop serving
    /// hits. The per-ROW <c>SourceVersion</c> is where which model answered is recorded, and it is the
    /// right granularity anyway: a mixed assessment has no single answering model.
    /// </para>
    /// <para>
    /// <b>The string must move when either arm moves, and it is the only term that does.</b>
    /// <c>SemanticCacheKey.Digest</c> covers this version and not the inner arms' endpoints or model
    /// references, so a cascade version string that omitted an arm would serve an entry taken under
    /// the arm that was replaced. The wiring that composes the arms supplies this value.
    /// </para>
    /// <para>
    /// <b>And it must name each arm's HOST, not only its model.</b> The same model at the same digest
    /// on two hosts, given byte-identical requests, answered differently: all 78 Noul probabilities
    /// differed, maximum absolute difference 2.270e-02, 30 of 67 rendered dimensions differing at
    /// three decimals (measured by the Nimble lane on 2026-10-02, and independently re-measured by
    /// this lane over a corpus batch, where the maximum over 352 instances was 2.966e-02 and no
    /// instance agreed exactly). So a version string naming models without addresses would still
    /// serve an entry taken under a REPLACED HOST, which is the same defect one level down. The
    /// composite form is therefore <c>cascade/1+nimble:latest@host:port+jev-1.13.0@host</c>.
    /// </para>
    /// </remarks>
    public string ClassifierVersion { get; set; } = "cascade/1";

    /// <summary>Above this absolute difference, two local runs on the same input disagree.</summary>
    public double DisagreementTolerance { get; set; } = DefaultDisagreementTolerance;

    /// <summary>The trust table: the decisiveness band, and anything distrusted by name.</summary>
    public CascadeTrust Trust { get; set; } = CascadeTrust.Default;

    /// <summary>
    /// The policy weights, injected rather than copied, used to narrow the two conditions that can
    /// fire on a value alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Injected because a copy would go stale in the direction that looks healthy.</b> The weights
    /// are the policy engine's own (<c>PolicyOptions.DimensionWeights</c>), and a second copy here
    /// would keep escalating on a dimension the engine had stopped weighing, or stop escalating on one
    /// it had started to. The wiring passes the engine's table.
    /// </para>
    /// <para>
    /// <b>An absent or unknown weight means RELEVANT, which is the fail-closed direction.</b> A
    /// dimension with no entry is treated as decision-relevant, so a wiring mistake or a dimension
    /// added to the question set escalates more than it should rather than less. Escalating too much
    /// costs the strong model a call; escalating too little silently keeps a weak answer on a
    /// dimension policy is scoring.
    /// </para>
    /// <para>
    /// <b>Why the weights narrow the rule at all.</b> A strong-model call that cannot move any
    /// decision is not a minimal intervention, and "intervene minimally" is a commitment of this
    /// system rather than a preference. A dimension the engine scores at weight zero is one where a
    /// more decisive answer changes nothing.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, double> DimensionWeights { get; set; } =
        new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>True when an indecisive or disagreeing answer on this dimension could move a decision.</summary>
    internal bool CarriesWeight(string dimensionId)
    {
        if (!DimensionWeights.TryGetValue(dimensionId, out var weight))
        {
            return true;
        }

        return weight > 0;
    }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ClassifierVersion);

        if (DisagreementTolerance < 0 || DisagreementTolerance > 1)
        {
            // Outside the unit interval the condition is either always true (a tolerance above 1
            // cannot be exceeded by two values in [0, 1]) or the wrong sign, and both would ship as a
            // rule that fires on every message or on none.
            throw new InvalidOperationException(
                $"CascadeOptions.DisagreementTolerance must be within [0, 1], and it is "
                + $"{DisagreementTolerance}.");
        }

        ArgumentNullException.ThrowIfNull(Trust);
        Trust.Validate();
    }
}
