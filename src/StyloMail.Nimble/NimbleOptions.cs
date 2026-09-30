namespace StyloMail.Nimble;

/// <summary>
/// Configuration for the local decision-model classifier served by Ollama.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no credential here, and that is a property of the provider rather than an oversight.</b>
/// The model runs on this machine and the endpoint is a local socket, so this adapter has no key to
/// hold, inject or leak. The endpoint is still a real configuration decision, not a constant: see
/// <see cref="Endpoint"/>.
/// </para>
/// <para>
/// <b>Every default below is a measurement, not a preference.</b> The values are the ones the survey
/// in <c>tools/nimble-survey/</c> was run with, recorded on this machine on 30 Sep 2026 against
/// <c>nimble:latest</c>. Where a default is a judgement rather than a measurement it says so.
/// </para>
/// </remarks>
public sealed class NimbleOptions
{
    /// <summary>
    /// Where Ollama's generate endpoint is listening.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately 11435 and not Ollama's default 11434.</b> Two servers are installed on the
    /// development machine and they are different builds:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>127.0.0.1:11434</c>, Ollama.app, reported version <b>0.31.1</b>.
    /// </description></item>
    /// <item><description>
    /// <c>127.0.0.1:11435</c>, Homebrew, reported version <b>0.35.0</b>.
    /// </description></item>
    /// </list>
    /// <para>
    /// Both can see the same models and both answered the survey's probe identically, so the older
    /// server is a hazard rather than a proven behaviour difference. It is still the wrong default:
    /// a client pointed at 11434 is talking to a build nobody chose, and the failure would present as
    /// a schema or truncation oddity rather than as a connection error.
    /// </para>
    /// <para>
    /// <c>ollama --version</c> does not settle which server is which: it prints the version of the
    /// <em>server it reaches</em>, not of the binary that ran. Set this explicitly in deployment.
    /// </para>
    /// </remarks>
    public string Endpoint { get; set; } = "http://127.0.0.1:11435/api/generate";

    /// <summary>
    /// Model reference to generate with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a moving tag, and the hosted adapter's is not.</b> The Jev adapter pins
    /// <c>jev-1.13.0</c> because an alias that moves would silently change behaviour under tuned
    /// thresholds. A local Ollama reference cannot be pinned the same way: <c>/api/generate</c> echoes
    /// the name it was given and reports no resolved id, and it resolves names through the model
    /// manifest rather than accepting a content digest, so there is no versioned identifier to put
    /// here.
    /// </para>
    /// <para>
    /// The stable identity of a local model is its <b>digest</b>. The survey recorded
    /// <c>24e550a16a7081881be2f1f0d91e8cc13a597472735c04119f035a0a85c67e0c</c> for
    /// <c>Bespoke-Nimble-9B-merged-current-Q8_0.gguf</c>, Q8_0, 9.0B parameters. Compare that against
    /// <c>ollama list</c> before trusting a measurement; nothing in this adapter can check it for you.
    /// </para>
    /// <para>
    /// The consequence is recorded rather than hidden: <see cref="Core.SemanticAssessment.ResolvedModelVersion"/>
    /// from this adapter is the configured reference, not a resolved version, because Ollama does not
    /// report one.
    /// </para>
    /// </remarks>
    public string Model { get; set; } = "nimble:latest";

    /// <summary>
    /// Context window requested from the server, in tokens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The model card declares 262144 and the loaded model answers with 8194.</b> Measured: with no
    /// <c>num_ctx</c> set, <c>/api/show</c> reports a loaded <c>context_length</c> of 8194, so the
    /// effective window is 8194 regardless of what the card says. 8192 is the conventional setting
    /// near it and is the value the shipping request shape was measured with.
    /// </para>
    /// <para>
    /// Changing this forces Ollama to reload the model, which the survey measured at 3.98 s on top of
    /// an otherwise warm call. It is therefore part of the request shape and part of the cache key:
    /// an assessment memoised at one window is not reused at another.
    /// </para>
    /// </remarks>
    public int NumCtx { get; set; } = 8192;

    /// <summary>
    /// Maximum characters of a message body placed in the request state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Far below the hosted adapter's 12,000, and the reason is arithmetic rather than taste.</b>
    /// The rendered question set for the twelve dimensions is 4,264 bytes, measured from the real
    /// renderer, so at <see cref="NumCtx"/> 8192 only about 3,928 bytes remain for the state. A
    /// 12,000 character body does not fit that window at all, and a default that cannot be sent would
    /// be a default that silently becomes a shorter one.
    /// </para>
    /// <para>
    /// <b>The practical consequence, stated plainly:</b> at these settings the local provider reads
    /// much less of a long message than the hosted one does, and a long message is judged on its
    /// opening. Raising <see cref="NumCtx"/> is the lever, and it is not free: a window change forces a
    /// reload, the KV cache grows with it, and no measurement has been taken above 8192.
    /// </para>
    /// <para>
    /// <b>It is a ceiling, not the binding constraint.</b> The classifier refuses to send a prompt
    /// whose UTF-8 length exceeds <see cref="NumCtx"/> and shortens the body further when the rest of
    /// the state needs the room. Omitting a tail is recorded in the state, so the model is told the
    /// body is partial rather than being handed one and left to assume it is whole.
    /// </para>
    /// </remarks>
    public int MaxBodyCharacters { get; set; } = 2_500;

    /// <summary>Maximum links and attachments included in the state, bounding what one message can cost.</summary>
    public int MaxLinks { get; set; } = 40;

    public int MaxAttachments { get; set; } = 20;

    /// <summary>
    /// Client-side deadline for one semantic call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two orders of magnitude above the hosted adapter's one second, because the latency is two
    /// orders of magnitude different.</b> Measured on this Mac, CPU only, with the model resident: a
    /// full twelve-dimension call takes 8.0 to 8.8 s. A cold start is worse: loading the model cost 42
    /// to 55 s on its own in the first measurements, and one call took 103.1 s, of which 95.8 s was
    /// unattributed by Ollama's own counters and did not recur in eight repeats. A one-second deadline
    /// would report every correct answer as a timeout.
    /// </para>
    /// <para>
    /// This is the deadline for a single attempt, so a caller's worst case with
    /// <see cref="MaxRetries"/> at its default is two of these.
    /// </para>
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Retries for a server that is loading or busy. Backs off exponentially from
    /// <see cref="RetryBaseDelay"/>.
    /// </summary>
    /// <remarks>
    /// <b>One, not the hosted adapter's two.</b> Ollama's 503 means the model is being loaded, which a
    /// second attempt genuinely helps. A retry elsewhere buys less: a fault is mostly configuration
    /// (raised, not retried) and a local socket failure is mostly "not running", where retrying inside
    /// the caller's latency budget only delays the explicit unavailable state.
    /// </remarks>
    public int MaxRetries { get; set; } = 1;

    /// <summary>Base delay for exponential backoff between retries.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Consecutive failures before the circuit opens and every askable dimension reports
    /// <c>Unavailable</c> without calling the server.
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// <summary>How long the circuit stays open before a probe is allowed through.</summary>
    public TimeSpan CircuitBreakerOpenDuration { get; set; } = TimeSpan.FromSeconds(30);
}
