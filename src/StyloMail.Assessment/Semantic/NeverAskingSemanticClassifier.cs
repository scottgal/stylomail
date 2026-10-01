using StyloMail.Core;

namespace StyloMail.Assessment.Semantic;

/// <summary>
/// The semantic tier of a deployment that has no provider, and says so in its evidence.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a declaration, not a fallback, and the distinction is the whole point of the type.</b>
/// A deployment selects it to state that nothing here will ever put a question to a semantic
/// provider, which is a different fact from a configured provider that could not answer. The two
/// facts travel as different availabilities and they may never be reachable from one another: a
/// runtime outage arrives as <see cref="EvidenceAvailability.Unavailable"/> and must not be
/// convertible into an allow on local evidence alone, while a question that was never asked is
/// <see cref="EvidenceAvailability.NotApplicable"/>, the availability this design already defines as
/// "nothing was asked of the provider, so nothing was lost".
/// </para>
/// <para>
/// <b>A flag would not do, and that is why this is a class.</b> The deployment's shape is a
/// composition decision, made once per deployment, and expressing it as a classifier means it is
/// chosen where every other composition decision is chosen, it cannot be reached by a caller that
/// merely wants an outage to be quiet, and the evidence it produces has one definition rather than
/// one per call site.
/// </para>
/// <para>
/// <b>One row per asked dimension, rather than no rows at all.</b> A dimension missing from the
/// ledger cannot be told apart from a dimension nobody asked about, and the pipeline's own rule is
/// that the questions considered are named. The deterministic side already emits a
/// <c>NotApplicable</c> row when a message cannot be asked something, and this is the same answer
/// from the other half of the pipeline.
/// </para>
/// <para>
/// <b>Nothing is dialled and no credential is read.</b> There is no endpoint and no key in scope in
/// this class, which is what makes selecting it a locality decision an operator can rely on rather
/// than a promise: a deployment running this cannot be sending message content to a semantic
/// provider, because it holds nothing to send it with.
/// </para>
/// </remarks>
public sealed class NeverAskingSemanticClassifier : ISemanticMailClassifier
{
    /// <summary>
    /// The producer stamp on every row this emits.
    /// </summary>
    /// <remarks>
    /// Deliberately not a model id. <c>SourceVersion</c> is the field a reader uses to tell which
    /// producer answered, and no model answered here: a stamp that looked like one would let a
    /// deployment's shape pass for a provider that spoke. It is versioned like the other producers
    /// so that changing what "never asked" means is a change a replay can see.
    /// </remarks>
    public const string ProducerVersion = "never-asked/1";

    private readonly TimeProvider _clock;

    /// <param name="clock">
    /// The clock the rows are stamped with. Taken from the composition rather than the system
    /// default where one is available, because the stamp lands in the ledger and a replay that
    /// fixed the assessment clock would otherwise write a row the run it replays did not.
    /// </param>
    public NeverAskingSemanticClassifier(TimeProvider? clock = null) =>
        _clock = clock ?? TimeProvider.System;

    public ValueTask<SemanticAssessment> ClassifyAsync(
        SemanticMailInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        var observedAt = _clock.GetUtcNow();
        var evidence = new List<Evidence>(input.Dimensions.Count);

        foreach (var dimension in input.Dimensions)
        {
            evidence.Add(new Evidence
            {
                SignalId = dimension.Id,
                Origin = EvidenceOrigin.Semantic,
                Availability = EvidenceAvailability.NotApplicable,
                // No value, in either direction: a zero here would be the fabricated low answer
                // the availability model exists to prevent, and there is no probability to carry.
                Value = null,
                Confidence = null,
                SampleSupport = null,
                SourceVersion = ProducerVersion,
                ObservedAt = observedAt,
                ObservedScope = EvidenceBuilder.MessageScope,
            });
        }

        return ValueTask.FromResult(new SemanticAssessment
        {
            Evidence = evidence,

            // Null, because no alias resolved: there is no model and naming one would put a version
            // in the ledger that no deployment ran.
            ResolvedModelVersion = null,

            // The reason stamps follow the established "never reached a provider" convention, so a
            // reader can tell this from a cached clean result at a glance.
            Cache = new CacheProvenance
            {
                Hit = false,
                KeyDigest = "never-asked",
                CachedAt = null,
                ModelVersion = null,
                Stale = false,
            },
        });
    }
}
