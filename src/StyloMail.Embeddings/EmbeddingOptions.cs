namespace StyloMail.Embeddings;

/// <summary>
/// Configuration for the local embedding provider, and the contract it enforces on the service.
/// </summary>
/// <remarks>
/// <para>
/// <b>The expected values are configuration rather than constants because a mismatch must be
/// detectable, not because they are expected to move.</b> A vector carries meaning only inside the
/// space that produced it: the same model at another dimension, or under another task prefix, is a
/// different space, and a comparison across the two is a defect rather than drift. So the provider
/// is told what to expect and refuses anything else, instead of discovering the difference later in
/// a similarity score that looks plausible.
/// </para>
/// <para>
/// <b>The token is a path, never a value.</b> A token held here would be copied into every
/// configuration dump and log line that renders an options object. The provider reads the file on
/// each call, which also makes rotation a file write rather than a restart.
/// </para>
/// </remarks>
public sealed class EmbeddingOptions
{
    /// <summary>Base address of the local embedding service.</summary>
    /// <remarks>
    /// Loopback only. The service refuses to bind anything else, and pointing this elsewhere would
    /// mean message segments leaving the machine, which is the property the local path exists for.
    /// </remarks>
    public string Endpoint { get; set; } = "http://127.0.0.1:11440";

    /// <summary>The model tag every response must name.</summary>
    public string ModelTag { get; set; } = "google/embeddinggemma-2";

    /// <summary>
    /// Vector width expected from the service, and the width the caller stores.
    /// </summary>
    /// <remarks>
    /// 256 is the service default: the model's Matryoshka training is close to lossless down to 256,
    /// and 128 costs multimodal quality. Queries and prototypes must share this number, so it is
    /// checked rather than assumed.
    /// </remarks>
    public int Dimension { get; set; } = 256;

    /// <summary>
    /// The prefix version every response must name.
    /// </summary>
    /// <remarks>
    /// The service applies the prefix server side, so callers cannot mix one. What can still happen
    /// is a service redeployed with a different prefix, and this is how that is caught: the version
    /// string moves with the prefix, and a stored vector from another version is not comparable.
    /// </remarks>
    public string PrefixVersion { get; set; } = "classification/1";

    /// <summary>File holding the bearer token. Read per call, never logged.</summary>
    public string TokenFilePath { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config",
            "stylomail",
            "embedding-token");

    /// <summary>
    /// Deadline for one HTTP call, metadata or encode.
    /// </summary>
    /// <remarks>
    /// Bounded and short: a segment embedding is a millisecond-scale operation on a warm GPU, and a
    /// caller that is already deciding whether to hold a message must not wait on it. The measured
    /// figure and the arm it came from are in <c>tools/embeddings/README.md</c>.
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Retries allowed for a 503, which is the service saying the model is still loading.
    /// </summary>
    /// <remarks>
    /// One, and only for 503. Every other failure here is a configuration fault or a dead socket,
    /// and retrying either delays the explicit not-produced state that the caller needs.
    /// </remarks>
    public int MaxRetries { get; set; } = 1;

    /// <summary>Base delay for the single retry's backoff.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Consecutive failures before the circuit opens and calls are skipped.</summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// <summary>How long the circuit stays open before one probe is allowed through.</summary>
    public TimeSpan CircuitBreakerOpenDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum segments in one call. The service enforces its own, equal, bound.</summary>
    public int MaxSegments { get; set; } = 32;

    /// <summary>Maximum characters in one segment. The service enforces its own, equal, bound.</summary>
    public int MaxSegmentCharacters { get; set; } = 6000;

    /// <summary>
    /// How long a validated metadata document is trusted before it is fetched again.
    /// </summary>
    /// <remarks>
    /// Metadata is static for the lifetime of a service process, so the check is cheap and rarely
    /// needs repeating. Repeating it on an interval is what notices a redeploy with a different
    /// model or prefix without requiring a Host restart.
    /// </remarks>
    public TimeSpan MetadataValidationInterval { get; set; } = TimeSpan.FromMinutes(15);
}
