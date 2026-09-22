namespace StyloMail.Jev;

/// <summary>
/// Configuration for the TypeSafe/Jev semantic classifier.
/// </summary>
/// <remarks>
/// <b>The API key is never stored in source.</b> Supply it from configuration bound to an
/// environment variable, .NET user-secrets, or a secret store, see
/// <see cref="ApiKeyEnvironmentVariable"/> for the conventional name. A key committed to a
/// repository must be treated as compromised and rotated, not merely deleted.
/// </remarks>
public sealed class JevOptions
{
    /// <summary>Conventional environment variable holding the bearer key.</summary>
    public const string ApiKeyEnvironmentVariable = "TYPESAFE_API_KEY";

    public string Endpoint { get; set; } = "https://api.typesafe.ai/v1/systemone";

    /// <summary>
    /// Bearer key. Populate from configuration, never from code. Empty means "not configured",
    /// which makes the classifier report every dimension as unavailable rather than failing open.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Pinned model id.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately a versioned id, not the <c>jev-latest</c> alias.</b> An alias moves whenever
    /// TypeSafe ships a release, which would silently change classifier behaviour underneath tuned
    /// confidence thresholds and invalidate memoised assessments without any signal. TypeSafe's own
    /// guidance is to pin the versioned id once thresholds are tuned. The response reports the
    /// resolved model id, which is recorded on every assessment.
    /// </remarks>
    public string Model { get; set; } = "jev-1.13.0";

    /// <summary>
    /// Client-side deadline for a semantic call.
    /// </summary>
    /// <remarks>
    /// <b>This is our policy, not a provider SLA.</b> TypeSafe documents no timeout. Exceeding it
    /// yields an explicit unavailable state, never an assumed-negative score.
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Retries for rate-limit (429) and overload (529) responses. Backs off exponentially.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Base delay for exponential backoff between retries.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Maximum characters of body text placed in the request state.
    /// </summary>
    /// <remarks>
    /// The API allows 64k context, of which 32k is shared between state and the longest question.
    /// We stay well inside that: the questions themselves are substantial, and a bounded state is
    /// what keeps per-message provider cost predictable.
    /// </remarks>
    public int MaxBodyCharacters { get; set; } = 12_000;

    /// <summary>Maximum links and attachments included in the state, bounding request size and cost.</summary>
    public int MaxLinks { get; set; } = 40;

    public int MaxAttachments { get; set; } = 20;

    /// <summary>
    /// Consecutive failures before the circuit opens and semantic evidence reports unavailable
    /// without calling the provider.
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// <summary>How long the circuit stays open before a probe is allowed.</summary>
    public TimeSpan CircuitBreakerOpenDuration { get; set; } = TimeSpan.FromSeconds(30);
}
