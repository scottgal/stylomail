using StyloMail.Adaptive;
using StyloMail.Adaptive.Storage;
using StyloMail.Assessment.Semantic;
using StyloMail.Core;
using StyloMail.Mime;
using StyloMail.Persistence;
using StyloMail.Queue;

namespace StyloMail.Assessment;

/// <summary>
/// Builds a fully wired assessment pipeline from the components the host already has.
/// </summary>
/// <remarks>
/// <b>Registering the result is what makes the pipeline live.</b> Until an
/// <see cref="IMailAssessor"/> is registered the host answers 503, which is the honest response:
/// an unassessed message is not a message this system has taken responsibility for, and forwarding
/// one would mean the deployment's name is on a decision nobody made.
///
/// <para>
/// This exists so that wiring is one call rather than a paragraph of construction order that every
/// host would get subtly different. The components themselves are supplied by the caller, the
/// MIME adapter, the classifier behind its cache decorator, the SQLite database, the spool and the
/// options, because a composition root that also decided where the database lives would be a
/// composition root that could not be pointed at a test's temporary directory.
/// </para>
///
/// <para>
/// <b>One clock, or none.</b> The queue's options carry their own <see cref="TimeProvider"/>, and a
/// replay that fixes the assessment clock while leaving the queue on the wall clock produces
/// retries and hold deadlines that do not move with the run. Pass the same provider to
/// <paramref name="queueOptions"/> that the assessment contexts will use. This is a wiring
/// requirement rather than something that can be asserted here, because the contexts are created
/// per request and this factory sees none of them.
/// </para>
/// </remarks>
public static class AssessmentPipeline
{
    /// <summary>Wires the pipeline over the shared SQLite database and spool.</summary>
    public static MailAssessor Create(
        IMimeMessageAnalyzer mimeAnalyzer,
        ISemanticMailClassifier classifier,
        SqliteConnectionFactory connections,
        SpoolStore spool,
        MailAssessorOptions options,
        QueueOptions? queueOptions = null,
        AdaptiveOptions? adaptiveOptions = null,
        IAssessmentPolicyContextSource? policyContext = null)
    {
        ArgumentNullException.ThrowIfNull(mimeAnalyzer);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        var profileStore = new SqliteAdaptiveProfileStore(connections);
        profileStore.EnsureCreated();

        var queue = new QueueStore(connections, spool, queueOptions);

        // The cache decorator goes between the pipeline and whatever classifier was supplied, so a
        // provider call is only ever made for a question this process has not already answered.
        var cached = new SemanticCacheClassifier(
            classifier,
            new InMemorySemanticCacheStore(),
            options.SemanticCache);

        return new MailAssessor(
            mimeAnalyzer,
            cached,
            new SqliteAdaptiveProfileStoreAdapter(profileStore),
            new QueueStoreAcceptanceQueue(queue),
            options,
            new SpoolRawMessageSource(spool),
            policyContext,
            campaignWindow: null,
            quotaLedger: null,
            learningGate: null);
    }
}
