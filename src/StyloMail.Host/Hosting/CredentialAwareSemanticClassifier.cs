using System.Net;
using StyloMail.Core;
using StyloMail.Jev;

namespace StyloMail.Host.Hosting;

/// <summary>
/// Records whether the semantic provider has rejected this deployment's credential, without changing
/// what the pipeline does about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A decorator because the signal exists but has nowhere to go.</b> The Jev adapter already
/// distinguishes a rejected credential from every other fault and throws a
/// <see cref="JevContractException"/> carrying the status. What was missing was a reader: the
/// exception travelled up through the pipeline and out of the request, so the only components that
/// ever learned about it were the caller and the log. This observes the same exception on the way past
/// and tells <see cref="ProviderCredentialHealth"/>, which the readiness probe consults.
/// </para>
/// <para>
/// <b>It rethrows, and that is deliberate.</b> The adapter's choice to make a `401` loud was correct:
/// a revoked key must not look like a quiet inbox, and the fix for "nobody was watching" is a
/// watcher, not a quieter exception. This wrapper changes no behaviour a caller can observe; it only
/// makes the condition visible to readiness, which is where an operator looks.
/// </para>
/// <para>
/// A `422` is not a credential problem and is not recorded here: that status means our request shape
/// is wrong, which is a bug rather than a deployment condition, and making it not-ready would take a
/// host out of service for something a restart cannot fix.
/// </para>
/// </remarks>
public sealed class CredentialAwareSemanticClassifier : ISemanticMailClassifier
{
    private readonly ISemanticMailClassifier _inner;
    private readonly ProviderCredentialHealth _health;
    private readonly TimeProvider _clock;

    public CredentialAwareSemanticClassifier(
        ISemanticMailClassifier inner,
        ProviderCredentialHealth health,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(clock);

        _inner = inner;
        _health = health;
        _clock = clock;
    }

    public async ValueTask<SemanticAssessment> ClassifyAsync(
        SemanticMailInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var assessment = await _inner.ClassifyAsync(input, cancellationToken).ConfigureAwait(false);

            // **The resolved model id is the only reliable "the provider answered" signal.** Every
            // failure path the adapter returns sets it to null, and it is populated from the response
            // body on success, so it distinguishes an answer from an `Unavailable` result without
            // this decorator having to guess from the evidence. Testing the evidence instead would be
            // wrong twice over: `Evidence` is non-empty even when every dimension is `Unavailable`,
            // and `Cache` is non-null even for a failure, because an unavailable cache is still a
            // cache provenance. Either would have cleared a rejection on the very result that proves
            // the credential is still bad.
            if (assessment.ResolvedModelVersion is not null)
            {
                _health.RecordAccepted();
            }

            return assessment;
        }
        catch (JevContractException ex) when (
            ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _health.RecordRejected(ex.StatusCode.Value, _clock.GetUtcNow());

            throw;
        }
    }
}
