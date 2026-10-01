using StyloMail.Assessment;
using StyloMail.Assessment.Semantic;
using StyloMail.Core;
using StyloMail.Mime;
using StyloMail.Persistence;
using StyloMail.Queue;

namespace StyloMail.Conversation.Measure;

/// <summary>
/// Builds the classifier input the way the Host builds it, and hands back what came out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The site experiment rebuilt the Host's classifier input by hand from the
/// request artifact and could not reproduce the digest the Host served. That reconstruction is a
/// guess about a path it does not run: it calls the MIME parser directly and assembles a
/// <see cref="SemanticMailInput"/> itself, so every step <c>MailAssessor</c> performs between the
/// bytes and the classifier is missing from it. Everything this lane believes about the Host's input
/// is a reading of that code rather than a measurement of it.
/// </para>
/// <para>
/// <b>So let the assessor build it.</b> This composes the shipping <c>MailAssessor</c> through the
/// same <c>AssessmentPipeline.Create</c> the Host calls, with the same options, and puts a capturing
/// stub where the model would be. The subclassed decorator the pipeline installs computes the cache
/// key over the input it was actually handed, so the digest that comes back is the Host's own key for
/// the Host's own input, with no reconstruction anywhere in the path.
/// </para>
/// <para>
/// <b>No model call, and none is possible.</b> The stub answers immediately and never opens a socket,
/// so this measures the input assembly and nothing else. It is the one form of the comparison that
/// cannot be confounded by a shared model, and it is the reason it can be run as often as the
/// argument needs it rather than budgeted.
/// </para>
/// <para>
/// <b>What it is not.</b> The database and the spool are empty scratch, so the profile store holds no
/// snapshot and the durable payload source resolves to nothing. That is not a shortcut: it is the
/// same state the end-to-end arms run against, whose Host is started on a fresh run directory.
/// </para>
/// </remarks>
internal static class HostInputCapture
{
    /// <summary>What one assessment produced, reduced to the two things the comparison needs.</summary>
    internal sealed record Capture(string? Digest, SemanticMailInput? Input);

    /// <summary>
    /// Assesses one message through the shipping assessor and returns the classifier's input.
    /// </summary>
    internal static async Task<Capture> CaptureAsync(
        string directory,
        MailAnalysisInput analysis,
        string tenantId,
        ReadOnlyMemory<byte>? rawMessage)
    {
        Directory.CreateDirectory(directory);

        var options = new MailAssessorOptions
        {
            ProfileKeyHasher = new StyloMail.Adaptive.Profiles.ProfileKeyHasher(new byte[32]),
        };

        var capturing = new CapturingClassifier();

        // The pipeline installs the cache decorator itself, so the stub goes in underneath it. That is
        // the same position the provider occupies in a live Host, which is what makes the digest the
        // decorator computes comparable with the one a response served.
        var assessor = AssessmentPipeline.Create(
            new BoundedMimeMessageAnalyzer(),
            capturing,
            new SqliteConnectionFactory(Path.Combine(directory, "capture.db")),
            new SpoolStore(Path.Combine(directory, "spool")),
            options,
            policyContext: StaticPolicyContextSource.Instance);

        var context = new AssessmentContext
        {
            TenantId = tenantId,
            ShadowMode = false,
            AssessmentOnly = true,
            CorrelationId = "cor_conversation_capture",
            TimeProvider = TimeProvider.System,
        };

        var assessment = await assessor
            .AssessAsync(analysis, context, CancellationToken.None, rawMessage)
            .ConfigureAwait(false);

        return new Capture(assessment.Cache.KeyDigest, capturing.Input);
    }

    /// <summary>Stands where the model stands, and keeps what it was shown.</summary>
    /// <remarks>
    /// <b>It returns an empty assessment rather than throwing.</b> The pipeline calls the classifier
    /// for the semantic step and then composes policy over whatever evidence came back; an empty
    /// answer is the same shape a provider that answered nothing produces, so the rest of the
    /// assessment still runs and the cache key is still computed. Throwing would skip exactly the
    /// step whose output is being measured.
    /// </remarks>
    private sealed class CapturingClassifier : ISemanticMailClassifier
    {
        public SemanticMailInput? Input { get; private set; }

        public ValueTask<SemanticAssessment> ClassifyAsync(
            SemanticMailInput input,
            CancellationToken cancellationToken)
        {
            Input = input;

            return ValueTask.FromResult(new SemanticAssessment
            {
                Evidence = [],
                ResolvedModelVersion = "conversation-capture",
                Cache = new CacheProvenance { Hit = false, KeyDigest = null, Stale = false },
            });
        }
    }
}
