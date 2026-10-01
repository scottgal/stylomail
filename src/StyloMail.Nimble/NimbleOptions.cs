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
    /// a truncation oddity rather than as a connection error.
    /// </para>
    /// <para>
    /// <b>The path is now part of the hazard, and it is UNMEASURED for the older server.</b> This
    /// default ends in <c>/v1/systemone</c> rather than <c>/api/generate</c>. The probe established
    /// that <c>/v1/systemone</c> exists on 11435 and that the native <c>/api/systemone</c> is a 404
    /// there. Nothing established whether 11434, the 0.31.1 build, serves the SystemOne route at all.
    /// If it does not, pointing here at 11434 produces a 404 whose body is a bare "page not found",
    /// and the adapter's 404 message names both causes because the status cannot separate them.
    /// </para>
    /// <para>
    /// <c>ollama --version</c> does not settle which server is which: it prints the version of the
    /// <em>server it reaches</em>, not of the binary that ran. Set this explicitly in deployment.
    /// </para>
    /// </remarks>
    public string Endpoint { get; set; } = "http://127.0.0.1:11435/v1/systemone";

    /// <summary>
    /// Model reference to generate with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a moving tag, and the hosted adapter's is not.</b> The Jev adapter pins
    /// <c>jev-1.13.0</c> because an alias that moves would silently change behaviour under tuned
    /// thresholds. A local Ollama reference cannot be pinned the same way: <c>/v1/systemone</c> echoes
    /// the name it was given, as <c>/api/generate</c> did, and reports no resolved id, and it resolves
    /// names through the model manifest rather than accepting a content digest, so there is no
    /// versioned identifier to put here.
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
    /// Context window this adapter budgets against, in tokens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This value no longer goes on the wire, and that is a change with the transport rather than a
    /// tidy-up.</b> The <c>/api/generate</c> body carried it as <c>options.num_ctx</c>. The SystemOne
    /// body has no <c>options</c> member, so the server is no longer asked for a window and applies
    /// whatever it applies. This figure is therefore a client-side assumption, and both the fit in the
    /// classifier and the truncation backstop read it. UNMEASURED whether the endpoint accepts an
    /// <c>options</c> member, whether its window is configurable, and what window it applies. It stays
    /// in the cache key: the fit and the backstop are expressed against it, so an assessment taken at
    /// one figure is not reusable at another.
    /// </para>
    /// <para>
    /// <b>What is requested is not what is applied, and the halving below was measured under the OLD
    /// request.</b> <see cref="AppliedContextWindow"/> is half of this, and that relation comes from
    /// saturating <c>/api/generate</c> with <c>num_ctx</c> set. Nothing has saturated the SystemOne
    /// endpoint, so the relation is carried over rather than re-derived, and every statement on
    /// <see cref="AppliedContextWindow"/> is a measurement of the previous transport until it is
    /// re-taken.
    /// </para>
    /// <para>
    /// <b>The three numbers are not the same number, and an earlier version of this remark conflated
    /// two of them.</b> Measured on 30 Sep 2026 against <c>nimble:latest</c> on the 0.35.0 server:
    /// <c>/api/show</c> reports <c>qwen35.context_length</c> <b>262144</b> (the architecture's
    /// maximum, the figure the model card quotes), a Modelfile parameter <c>num_ctx</c> of <b>8194</b>,
    /// and a request asking 8192 is cut at <b>4098 to 4104</b> depending on what the prompt is made
    /// of. The <c>context_length</c> field is not 8194, and neither it nor the Modelfile parameter is
    /// the applied window.
    /// </para>
    /// <para>
    /// <b>The cut is a band a few tokens wide, not an exact boundary</b>, and that is measured rather
    /// than assumed. Five fillers of different content were sent at this window
    /// (<c>--window-quantum</c>, artifact <c>window-quantum.json</c>):
    /// three of them, including one that does not repeat at all, were cut at <b>4098</b>; one was cut
    /// at <b>4104</b>; and two never reached the cut and are excluded, at 2104 and 2604 tokens, which
    /// is a prompt being counted rather than truncated. The suspicion that the repeated filler used by
    /// the ladder was quantising the number is therefore refuted: a non-repeating filler lands on the
    /// same figure.
    /// </para>
    /// <para>
    /// Changing this forces Ollama to reload the model, which the survey measured at 3.98 s on top of
    /// an otherwise warm call. It is therefore part of the request shape and part of the cache key:
    /// an assessment memoised at one window is not reused at another.
    /// </para>
    /// </remarks>
    public int NumCtx { get; set; } = 8192;

    /// <summary>
    /// The context window the server applies, when it is not the relation <see cref="AppliedContextWindow"/>
    /// derives. Null means "use the derived value".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An escape hatch, not a setting anyone should need on the reference server.</b> It exists
    /// because the relation below is a measurement of one server's behaviour rather than a rule of
    /// the protocol, and a deployment against a server that honours the request exactly should say so
    /// here rather than inherit a margin it does not need.
    /// </para>
    /// <para>
    /// A client cannot read the applied window from a response. A truncated prompt comes back with
    /// <c>done_reason</c> "stop", no warning field, and a <c>prompt_eval_count</c> that describes the
    /// shortened prompt, so the only way to know the window is to measure it by saturating it, which
    /// is what <c>tools/nimble-survey/survey_nimble.py --context-slots</c> does.
    /// </para>
    /// </remarks>
    public int? EffectiveNumCtx { get; set; }

    /// <summary>
    /// The window the fit and the truncation backstop reason about: <see cref="EffectiveNumCtx"/> when
    /// set, otherwise half of <see cref="NumCtx"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Half, because that is where the cut falls, and it is deliberately the low end of the
    /// band.</b> On the reference server a requested 4096 was cut at 2050, a requested 8192 at 4098,
    /// and a requested 16384 at 8194. The last two are not single points: five fillers of different
    /// content at 8192 were cut at 4098 (three of them, one non-repeating), at 4104 (one), and two
    /// never reached the cut at all, so the cut is a band and half is at or below its floor.
    /// </para>
    /// <para>
    /// <b>Rounding down is the point, not an approximation to apologise for.</b> The property this
    /// feeds is a truncation backstop: it reports <c>Unavailable</c> when the server evaluated a
    /// prompt at the window, meaning part of the message was not read. A bound a few tokens below the
    /// real cut fires slightly early, which costs an unavailable answer on a message that was almost
    /// certainly cut anyway; a bound a few tokens above it would let a partly-read message through as
    /// a complete answer, which is the failure this exists to prevent. The derivation is therefore not
    /// adjusted upward to 4098 to match the band's floor.
    /// </para>
    /// <para>
    /// <b>Two explanations were tested and one was refuted.</b> The obvious cause is a server dividing
    /// the requested context among parallel slots, so the probe asked for one slot and then for two.
    /// The applied window did not move: 4098 for one slot, for two, and for the default. The splitting
    /// mechanism is therefore not <c>num_parallel</c>, and it remains unexplained. What is not in doubt
    /// is the number, because the same probe established that the server's token counting is absolute
    /// rather than relative to the window: a fixed 3,720-character prompt evaluated to <b>824</b> tokens
    /// under a requested 2048 and again under a requested 8192, so the saturated plateau is a real cut
    /// and not half of a prompt count.
    /// </para>
    /// <para>
    /// <b>Why this matters to a safety property rather than to tuning.</b> A silent truncation is
    /// invisible from the response, so the adapter's guarantee has to be that a prompt which could be
    /// cut is never reported as an answer. Reasoning about the requested window made that guarantee
    /// false: a prompt of 5,000 tokens was under the requested 8192 and over the applied 4098, and the
    /// backstop compared against 8192 and read clean.
    /// </para>
    /// </remarks>
    public int AppliedContextWindow => EffectiveNumCtx ?? NumCtx / 2;

    /// <summary>
    /// Maximum characters of a message body placed in the request state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Far below the hosted adapter's 12,000, and the reason is arithmetic rather than taste.</b>
    /// The declared question set takes thousands of bytes on its own, so at <see cref="NumCtx"/> 8192
    /// only a few thousand remain for the state. A 12,000 character body does not fit that window at
    /// all, and a default that cannot be sent would be a default that silently becomes a shorter one.
    /// </para>
    /// <para>
    /// <b>The figure this paragraph used to quote, 4,264 bytes, is removed rather than carried over.</b>
    /// It was measured from the A/B letter renderer, which no longer exists. The SystemOne questions
    /// serialise as JSON with a type, an instruction and two criteria each, which is a different and
    /// larger document, and its size is NOT measured: measuring it needs a run the window gate is
    /// currently refusing. The default below is therefore left where it was rather than moved to a
    /// number nobody has taken. It is a ceiling, and the classifier's fit test is what enforces the
    /// arithmetic in practice; if the question set grows enough to make this default unsendable, the
    /// fit shortens the body and records that it did.
    /// </para>
    /// <para>
    /// <b>The practical consequence, stated plainly:</b> at these settings the local provider reads
    /// much less of a long message than the hosted one does, and a long message is judged on its
    /// opening. <b>The lever that sentence used to name is gone.</b> It read "raising
    /// <see cref="NumCtx"/> is the lever", which was true while this figure travelled as
    /// <c>options.num_ctx</c>. The SystemOne body has no options member, so raising this value now
    /// changes only the client's own budget and cannot make the server widen its window.
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
