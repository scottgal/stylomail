using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StyloMail.Core;

namespace StyloMail.Nimble;

/// <summary>
/// Classifies message content into independent semantic dimensions using a decision model served
/// locally by Ollama.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is an option beside the hosted adapter, not a replacement for the port.</b> It implements
/// <see cref="ISemanticMailClassifier"/> and returns evidence only, never an action. Whether a local
/// provider is used, and when, is a decision for the Host's wiring and the operators, not for this
/// file.
/// </para>
/// <para>
/// <b>No silent fallback, in either direction.</b> Anything that prevents an answer produces
/// <see cref="EvidenceAvailability.Unavailable"/> on every askable dimension
/// (<see cref="EvidenceAvailability.NotApplicable"/> on the ones that could not be asked), stating
/// why in the cache provenance. Nothing here substitutes a hosted answer, a cached answer or a
/// default score, and nothing here returns an empty success.
/// </para>
/// <para>
/// <b>The narrowness this paragraph used to describe went with the letter shape, and what replaces it
/// is unmeasured rather than better.</b> It read: "It answers with a decision, so a dimension's value
/// is 1.0 or 0.0 and a genuinely balanced result near 0.5 cannot be expressed at all." That was true of
/// the A/B rendering and it was a property of the two-letter alphabet rather than of the model. The
/// SystemOne endpoint answers a Noul question with a probability, so a value is now graded and the
/// port's own notion of near-0.5 meaning genuinely balanced is expressible. What the distribution of
/// those probabilities actually is, and whether it is stable across runs, has NOT been measured: the
/// probe produced one answer at 0.9995, which is one point and says nothing about the middle. See
/// <see cref="NimbleQuestionSet.MapNoul"/>, which carries the full correction rather than a summary.
/// </para>
/// <para>
/// <b>Structural containment is not immunity, and nothing here claims it is. Two of the mechanisms
/// this paragraph used to lean on are gone with the transport, and they are recorded rather than
/// quietly reworded.</b> The message still travels as data inside a single JSON <c>state</c> member and
/// the question list is still built in code from <see cref="Core.SemanticDimensions"/>, so a hostile
/// message cannot break the wire structure and cannot add a question. What the old shape also had, and
/// this one does not, is a <c>system</c> message that told the model the description is data and never
/// an instruction, and an answer schema pinning every property to one of two letters. Whether an
/// <c>system</c> member would be accepted is UNMEASURED, so nothing here can claim the guard is
/// unnecessary; it is absent because the measured request is absent of it. The shaped-answer property
/// survives by a different route: the server types its answer, and <see cref="NimbleQuestionSet.MapNoul"/>
/// refuses anything that is not a Noul probability in [0, 1] rather than repairing it.
/// </para>
/// <para>
/// What none of it can do is stop a model from being persuaded to answer wrongly. An attacker writes
/// text this model reads, and a small model can be talked into a false negative. So the guarantee is
/// about the *shape* of the answer, never about its truth, and a false negative arrives here
/// indistinguishable from a true one. That consequence is recorded at the policy tier as architecture
/// decision 23; this file's part in it is to produce an honest row and to claim nothing more. The
/// injection question is measurable rather than a matter of argument, and the live measurement that
/// asks it already exists.
/// </para>
/// <para>
/// <b>No request or response body is ever logged.</b> Message content leaves this process only to the
/// configured local endpoint.
/// </para>
/// </remarks>
public sealed class NimbleSemanticMailClassifier : ISemanticMailClassifier
{
    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Options for rendering the state into the prompt. Deliberately without a naming policy, so the
    /// state's keys reach the model exactly as they are written here.
    /// </summary>
    private static readonly JsonSerializerOptions StateJson = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Slack, in bytes, left unspent on top of the rendered questions so the state's own punctuation
    /// and any small field added later cannot push the prompt over the window.
    /// </summary>
    private const int PromptByteMargin = 512;

    /// <summary>
    /// Recorded on a semantic row whose prompt was shortened, so the row explains the weaker value
    /// it carries without leaving the covered set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a reason and not a downgrade.</b> The row stays <c>Available</c>. The policy engine's
    /// security check reads this lane's ids by availability and reads the covered weight beside it,
    /// so reporting a shortened read as <c>ReducedCoverage</c> would take the row out of both at
    /// once on every message whose body exceeds the character budget. The reason is the channel that
    /// survives that ruling, and it is the one a console can render beside the availability.
    /// </para>
    /// <para>
    /// <b>Scoped to the CLIENT deliberately.</b> This adapter can see that it cut the body itself;
    /// it cannot see what the server then did with the request, because the request carries no
    /// window and the response reports no window either. A reason that claimed the message was read
    /// over less than it contains, without saying by whose hand, would be read as covering both.
    /// </para>
    /// </remarks>
    private const string PromptShortenedReason =
        "the client shortened the message body to fit the context window";

    private readonly HttpClient _http;
    private readonly NimbleOptions _options;
    private readonly TimeProvider _time;
    private readonly NimbleCircuitBreaker _breaker;

    public NimbleSemanticMailClassifier(
        HttpClient http,
        NimbleOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        _http = http;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _breaker = new NimbleCircuitBreaker(
            options.CircuitBreakerFailureThreshold,
            options.CircuitBreakerOpenDuration,
            _time);
    }

    public async ValueTask<SemanticAssessment> ClassifyAsync(
        SemanticMailInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var now = _time.GetUtcNow();
        var (askable, notApplicable) = PartitionDimensions(input);

        // Dimensions that cannot be asked are reported as NotApplicable. They are never scored low:
        // "there was no conversation context" is not evidence of a mismatched conversation.
        var notApplicableEvidence = notApplicable
            .Select(d => UnavailableEvidence(d, EvidenceAvailability.NotApplicable, now))
            .ToList();

        if (askable.Count == 0)
        {
            return new SemanticAssessment
            {
                Evidence = notApplicableEvidence,
                ResolvedModelVersion = null,
                Cache = BuildCacheProvenance(input, askable.Count, modelVersion: null),
            };
        }

        if (_breaker.IsOpen)
        {
            return Unavailable(askable, notApplicableEvidence, now, "provider circuit open");
        }

        var questions = NimbleQuestionSet.BuildQuestions(askable);
        var fitted = FitState(input, questions);

        if (fitted is null)
        {
            // The questions plus a state holding no body at all still exceed the window. Answering
            // would mean describing a message the model did not read, so nothing is asked.
            return Unavailable(
                askable,
                notApplicableEvidence,
                now,
                "question set and message state exceed the configured context window");
        }

        var request = new NimbleSystemOneRequest
        {
            Model = _options.Model,
            State = fitted.Value.State,
            Questions = questions,
        };

        NimbleSystemOneResponse? response;
        try
        {
            response = await SendWithRetryAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (NimbleContractException)
        {
            // Configuration and contract faults must surface, not be smoothed over.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            _breaker.RecordFailure();
            return Unavailable(askable, notApplicableEvidence, now, ex.GetType().Name);
        }

        if (response is null)
        {
            _breaker.RecordFailure();
            return Unavailable(askable, notApplicableEvidence, now, "provider returned no usable response");
        }

        // Backstop for a server that shortened the prompt anyway. The fit above is what should make
        // this unreachable, and the survey is why it is here at all: a shortened prompt comes back
        // with done_reason "stop", no warning, and an evaluated-token count that describes the
        // shortened prompt rather than the one that was sent. An answer about a message the model
        // only partly saw is worse than no answer, so it is not reported as one.
        //
        // Against the APPLIED window, never the requested one. Comparing with NumCtx was blind by
        // exactly the amount that matters: the server applies about half of it, so a prompt cut to
        // 4098 tokens reported 4098, the check asked whether that was >= 8192, and a completely
        // truncated prompt came back as a clean and complete answer. That is decision 26, and it was
        // measured rather than reasoned about.
        // The count now comes from `usage.input_tokens`, because the SystemOne answer carries no
        // `prompt_eval_count` and no `done_reason`. The old shape reported both and this one reports
        // only the pair of counts, so a check written against the old field would read null forever
        // and the backstop would stop firing without anything failing. That is why the field moved
        // with the transport rather than after it.
        //
        // UNMEASURED, and it is the load-bearing assumption of this whole block: whether
        // `usage.input_tokens` is the prompt as SENT or as the server APPLIED it. On /api/generate the
        // equivalent figure was the post-shortening count, which is what made the comparison below
        // meaningful. Nothing in the probe's artifact settles it. So this is still a backstop resting
        // on an assumption rather than a proven property, and it stays marked as one until a run
        // saturates the window through this shape and reads the number back.
        if (response.Usage?.InputTokens is { } evaluated && evaluated >= _options.AppliedContextWindow)
        {
            _breaker.RecordSuccess();
            return Unavailable(
                askable,
                notApplicableEvidence,
                now,
                $"server evaluated {evaluated} prompt tokens at an applied window of "
                + $"{_options.AppliedContextWindow}");
        }

        _breaker.RecordSuccess();

        var evidence = notApplicableEvidence;
        evidence.AddRange(MapAnswers(askable, response, now, fitted.Value.BodyShortened));

        return new SemanticAssessment
        {
            Evidence = evidence,
            // Ollama echoes the reference it was asked for and reports no resolved id, so unlike the
            // hosted adapter this is the configured reference rather than a provider-resolved version.
            // Recorded anyway: it is what answered, and the alternative is null, which would read as
            // "unknown provider".
            ResolvedModelVersion = response.Model,
            Cache = BuildCacheProvenance(input, askable.Count, response.Model),

            // From the usage block, which is where this shape reports them. Null when the server
            // omitted it: an absent count is reported as absent rather than as zero, because a zero
            // here would read as a measurement of an empty prompt.
            InputTokens = response.Usage?.InputTokens,
            OutputTokens = response.Usage?.OutputTokens,
        };
    }

    /// <summary>
    /// Splits dimensions into those the message can support and those it cannot. Conversational
    /// continuity needs bounded prior context; without it the question has no referent.
    /// </summary>
    private static (List<SemanticDimension> Askable, List<SemanticDimension> NotApplicable)
        PartitionDimensions(SemanticMailInput input)
    {
        var askable = new List<SemanticDimension>(input.Dimensions.Count);
        var notApplicable = new List<SemanticDimension>();

        // Single source of truth: ask the question when context is actually present. Also consulting
        // Coverage.ConversationContextMissing would let the two disagree and silently suppress the
        // question, a missing answer that looks like a negative one.
        var hasConversation = input.Message.ConversationContext is { Count: > 0 };

        foreach (var dimension in input.Dimensions)
        {
            if (dimension.Id == SemanticDimensions.ConversationalContinuityId && !hasConversation)
            {
                notApplicable.Add(dimension);
            }
            else
            {
                askable.Add(dimension);
            }
        }

        return (askable, notApplicable);
    }

    /// <summary>
    /// Renders the state and shortens the body until the whole request fits the context window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What a byte budget does and does not say about tokens.</b> A tokeniser cannot emit more
    /// tokens than there are bytes to cover, so the request this method renders is, in the CLIENT's own
    /// tokenisation of it, at most as many tokens as it has bytes. <b>That is a bound on the request
    /// and NOT on the quantity the truncation guard compares, and the difference is measured rather
    /// than argued.</b> This comment used to conclude from it that a prompt of at most <c>num_ctx</c>
    /// bytes is at most <c>num_ctx</c> tokens, which is true only if the guarded count is a tokenisation
    /// of what was sent. It is not: 7,246 request bytes evaluate to <b>20,642</b> input tokens on this
    /// endpoint, because <c>usage.input_tokens</c> counts the prompt the SERVER constructs and grows
    /// faster than linearly in the question count. See the backstop's remark in the caller.
    /// </para>
    /// <para>
    /// <b>The byte ceiling is therefore a budget and not a guarantee, and the density that breaks it is
    /// ordinary rather than pathological.</b> Measured on this endpoint at twelve questions and at the
    /// FULL <see cref="NimbleOptions.MaxBodyCharacters"/> budget, so that the arm is the largest one this
    /// class can produce, the expansion from request bytes to input tokens runs from <b>2.867x</b> on a
    /// prose body to <b>6.252x</b> on a hexadecimal one, so the largest evaluation an adapter-producible
    /// state has been measured to reach is <b>43,202</b> tokens (a 2,500-character body, 6,910 request
    /// bytes, both inside their budgets). This paragraph used to name dense text as the worst case and
    /// put it at "nearer two characters per token than five"; the measured figure is about <b>six
    /// characters per token</b> for a hex body, so the estimate understated it threefold. So this method
    /// is best effort, and the guarantee that a partly-read message is never reported as an answer
    /// belongs to the backstop in the caller, which compares the evaluated count against the applied
    /// window.
    /// </para>
    /// <para>
    /// <b>The budget is not simply the applied window, and that is deliberate.</b> The declared question
    /// set is thousands of bytes on its own, so 'total bytes at most the applied window in tokens'
    /// would leave no room for a state at all and would refuse every message. The byte ceiling keeps
    /// the request small; the backstop keeps the answer honest when it is not small enough.
    /// </para>
    /// <para>
    /// <b>UNMEASURED, and new with this shape: <see cref="NimbleOptions.NumCtx"/> no longer goes on the
    /// wire.</b> The SystemOne request has no <c>options</c> member, so the window is now a client-side
    /// assumption about what the server will accept rather than a figure the server was asked for. The
    /// probe established neither what window the endpoint applies, nor whether it can be configured,
    /// nor whether an <c>options</c> member would be honoured if sent. Both this fit and the backstop
    /// in the caller are expressed against that assumption, so both rest on it until a run measures the
    /// endpoint's own window through this shape. The byte figure itself is deliberately not quoted
    /// here: the old remark quoted 4,264 for the letter rendering, and this shape serialises the
    /// questions differently, so carrying that number over would be a stale figure reading as a
    /// measured one.
    /// </para>
    /// <para>
    /// The loop shortens by at least the measured excess each pass and stops at zero, so it terminates.
    /// In practice the first pass is enough and the second confirms it.
    /// </para>
    /// <para>
    /// Returns null when even an empty body does not fit, which is the caller's signal to refuse
    /// rather than to send something that will be cut.
    /// </para>
    /// <para>
    /// <b>The shortening is recorded in the state, not in a return value.</b> The request itself says
    /// the body is partial, which is where the model and any reader of the request need to see it, and
    /// the state is hashed into the cache key, so a shortened assessment is never reused for the
    /// complete message.
    /// </para>
    /// </remarks>
    private (string State, bool BodyShortened)? FitState(
        SemanticMailInput input,
        IReadOnlyDictionary<string, NimbleQuestion> questions)
    {
        var budget = _options.MaxBodyCharacters;

        while (true)
        {
            var built = NimbleMessageState.Build(
                input.Message,
                input.TaggedContext,
                input.Profile,
                budget,
                _options.MaxLinks,
                _options.MaxAttachments);

            var state = JsonSerializer.Serialize(built.State, StateJson);

            // The WHOLE request is measured, not the two halves added together. Under the old shape
            // the questions and the state travelled in two fields, so their byte counts could simply
            // be summed. Here they are members of one JSON document, and the braces, the question
            // keys, the type names and the escaping of every quote inside the state are all part of
            // what the server receives. Summing two halves would understate the request by exactly
            // the amount the envelope costs, which is the direction that lets a message be sent that
            // the window cannot hold.
            var request = new NimbleSystemOneRequest
            {
                Model = _options.Model,
                State = state,
                Questions = questions,
            };

            var total = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, WireJson));

            if (total <= _options.NumCtx)
            {
                // The flag travels out with the state rather than being re-derived by the caller.
                // The loop's exit is the only place that knows whether the body was cut, and a
                // caller comparing budgets afterwards would be reconstructing a fact it was just
                // handed. It is the same flag the state already carries for the model, which is why
                // nothing new is computed here.
                return (state, built.BodyShortened);
            }

            if (budget == 0)
            {
                return null;
            }

            // Remove the measured excess plus the margin, and never less than a step, so a request
            // that is barely over the line still converges.
            var excess = total - _options.NumCtx;
            var step = Math.Max(excess + PromptByteMargin, 64);
            budget = budget > step ? budget - step : 0;
        }
    }

    private async Task<NimbleSystemOneResponse?> SendWithRetryAsync(
        NimbleSystemOneRequest request,
        CancellationToken cancellationToken)
    {
        var delay = _options.RetryBaseDelay;

        for (var attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
            {
                Content = JsonContent.Create(request, options: WireJson),
            };

            HttpResponseMessage response;
            try
            {
                response = await _http
                    .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Our own deadline elapsed, not the caller's cancellation.
                //
                // The hosted adapter raises here and this one does not, deliberately. Its deadline is
                // one second, which only a misconfiguration reaches. This deadline is two minutes and
                // a healthy machine has been measured stalling for 103 seconds, so a deadline here is
                // a transient condition, and a transient condition is what the port's unavailable
                // state is for. Raising would fail the pipeline for a slow answer.
                return null;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content
                        .ReadFromJsonAsync<NimbleSystemOneResponse>(WireJson, cancellationToken)
                        .ConfigureAwait(false);
                }

                var status = (int)response.StatusCode;

                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    // The server is up but the model is loading or busy. This is the one status where
                    // a second attempt genuinely helps.
                    if (attempt >= _options.MaxRetries)
                    {
                        return null;
                    }

                    await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
                    delay *= 2;
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    // A 404 now has two causes and the status cannot tell them apart, which is new with
                    // this path. The old endpoint answered 404 only for an unknown model. This one also
                    // answers 404 for an unknown ROUTE, measured: the probe posted the same body to the
                    // native path /api/systemone on the server that serves /v1/systemone and got
                    // "404 page not found". So on this transport a 404 is at least as likely to mean
                    // "this build has no SystemOne route" as "this build lacks the model", and a message
                    // naming only the model would send a reader to the wrong configuration value.
                    throw new NimbleContractException(
                        response.StatusCode == HttpStatusCode.NotFound
                            ? $"The local provider answered 404 for POST /v1/systemone and model "
                              + $"'{request.Model}'. That status covers two causes here and does not say "
                              + "which: the server is a build without the SystemOne route, or the model "
                              + "reference is not one it holds. Check the endpoint path first, then the "
                              + "model. A second Ollama on another port serves both a different route "
                              + "set and different models."
                            : "The local provider rejected the request body. This indicates a bug in the "
                              + "question or state shape, not a transient fault.",
                        response.StatusCode,
                        body);
                }

                // Everything else, including a 500 from a model that will not fit in memory: the
                // provider is not answering, which is a transient condition until it is not.
                return null;
            }
        }
    }

    /// <summary>
    /// Turns the answer object into one evidence row per asked dimension.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A 0.0 here is a decision, and it is not clearance.</b> A dimension that was not detected and a
    /// dimension the model agreed to suppress both arrive as <c>Available</c> with <c>Value</c> 0.0, and
    /// nothing in this row or in this file can tell them apart. Downstream policy that reads 0.0 as
    /// "looked at and found absent" is reading something this provider cannot say. The port has no state
    /// for "answered, but wrongly", so the honest handling is to say so here rather than to leave the
    /// number looking like a finding.
    /// </para>
    /// <para>
    /// <b>That paragraph used to derive the 0.0 case from the alphabet, and the derivation is retired
    /// with the alphabet rather than the conclusion.</b> It read "This provider answers with a letter,
    /// so ..." — under <c>nimble-request-shape/1</c> every value was 1.0 or 0.0 by construction, which
    /// made the collision above unavoidable rather than merely possible. A SystemOne Noul answer is a
    /// probability in [0, 1], so a 0.0 is now one point on a scale the model chose. The consequence a
    /// reader acts on is unchanged and is why the paragraph stays: 0.0 still does not distinguish
    /// "absent" from "suppressed". What is NOT claimed, and is not implied by anything above, is where
    /// the values in between actually fall. The probe produced one answer at 0.9995, which is one point
    /// near an end of the range and says nothing about the middle; see
    /// <see cref="NimbleQuestionSet.MapNoul"/>, which carries the full correction.
    /// </para>
    /// <para>
    /// That is the whole reason <c>Confidence</c> stays null and <c>SourceVersion</c> names the shape:
    /// the row carries every fact there is about how much it is worth, and fabricating a confidence
    /// would be inventing exactly the distinction this limitation is about. The SystemOne answer
    /// carries no <c>confidence</c> member for a Noul question, so there is not even a field to read.
    /// </para>
    /// <para>
    /// <b>An answered row is not always a FULL one, and it says so without leaving the covered
    /// set.</b> Where <paramref name="bodyShortened"/> is set the answer was produced over a body
    /// this adapter cut to make the request fit, and the row carries a reason naming that. It stays
    /// <see cref="EvidenceAvailability.Available"/> deliberately rather than being downgraded: these
    /// are the eight ids the policy engine's security check reads <em>by availability</em>, so a
    /// downgrade to <see cref="EvidenceAvailability.ReducedCoverage"/> would take the row out of that
    /// check and out of the covered weight at once, on every message whose body or quoted text
    /// exceeds the character budget. The reason is what makes the shortening visible where a fraction
    /// cannot, and it is the same principle the state already applies to the model one step out: the
    /// request tells the model the body is partial, and this tells the consumer the same.
    /// </para>
    /// </remarks>
    private static List<Evidence> MapAnswers(
        IReadOnlyList<SemanticDimension> askable,
        NimbleSystemOneResponse response,
        DateTimeOffset observedAt,
        bool bodyShortened)
    {
        var evidence = new List<Evidence>(askable.Count);
        var modelVersion = response.Model ?? "unknown";

        for (var index = 0; index < askable.Count; index++)
        {
            var dimension = askable[index];

            // The answer to this question, or nothing usable. A missing entry, an entry whose type is
            // not the type that was asked for, and a value outside [0, 1] all arrive here as a null
            // map and are reported as unavailable rather than repaired. There is no scavenging step
            // any more, and that is a property of the shape: the server keys each answer by the
            // question it was asked and types it, so the answer is either the object for this key or
            // evidence that the request and the answer are not about the same question.
            var answer = response.Answers is { } answers
                && answers.TryGetValue(NimbleQuestionSet.KeyFor(index), out var found)
                    ? found
                    : null;

            if (NimbleQuestionSet.MapNoul(answer) is not { } value)
            {
                evidence.Add(UnavailableEvidence(dimension, EvidenceAvailability.Unavailable, observedAt, modelVersion));
                continue;
            }

            // A shortened read stays Available and carries a REASON, rather than being downgraded.
            // The ruling is that it must stay COUNTED: these are the same eight ids the policy
            // engine's elevated-security check reads by availability, and it reads them together with
            // the covered weight, so a downgrade would drop a row out of both at once for any message
            // whose body or quoted text exceeds the character budget. The reason is what makes the
            // shortening visible instead, and it names the CLIENT's hand, because that is the only
            // one this adapter can see.
            evidence.Add(new Evidence
            {
                SignalId = dimension.Id,
                Origin = EvidenceOrigin.Semantic,
                Availability = EvidenceAvailability.Available,
                Value = value,

                // Present only when the client cut the body, so a reader can tell a full read from a
                // partial one without inferring it from the state.
                Attributes = bodyShortened ? [Attribute("reason", PromptShortenedReason)] : null,

                // Left null deliberately, as the hosted adapter leaves it. The model reports no
                // confidence, and defaulting one would fabricate certainty it never expressed. This
                // model does not even have the field to report.
                Confidence = null,
                SampleSupport = null,

                // Names the provider, the model reference and the request shape, so a reader can tell
                // a local row from a hosted one without guessing which model wrote it (decision 20).
                SourceVersion = NimbleQuestionSet.SourceVersion(modelVersion),
                ObservedAt = observedAt,
                ObservedScope = "message",
            });
        }

        return evidence;
    }

    /// <summary>
    /// Every askable dimension reports <c>Unavailable</c>. Policy downstream must treat this as
    /// reduced evidence and prefer a bounded hold over an irreversible rejection, never as a
    /// low-risk result.
    /// </summary>
    private static SemanticAssessment Unavailable(
        IReadOnlyList<SemanticDimension> askable,
        List<Evidence> notApplicableEvidence,
        DateTimeOffset now,
        string reason)
    {
        var evidence = new List<Evidence>(notApplicableEvidence);
        evidence.AddRange(askable.Select(d =>
            UnavailableEvidence(d, EvidenceAvailability.Unavailable, now, reason: reason)));

        return new SemanticAssessment
        {
            Evidence = evidence,
            ResolvedModelVersion = null,
            Cache = CacheUnavailable(reason),
        };
    }

    private static Evidence UnavailableEvidence(
        SemanticDimension dimension,
        EvidenceAvailability availability,
        DateTimeOffset observedAt,
        string sourceVersion = "none",
        string? reason = null)
        => new()
        {
            SignalId = dimension.Id,
            Origin = EvidenceOrigin.Semantic,
            Availability = availability,
            Value = null,
            Confidence = null,
            SampleSupport = null,
            SourceVersion = sourceVersion,
            ObservedAt = observedAt,
            ObservedScope = "message",

            // THE REASON GOES WHERE THE OTHER ORIGINS PUT THEIRS. Every evidence row the pipeline persists
            // carries `attributes`, and the rows other origins produce fill a `reason` attribute there; the
            // semantic rows arrived null, so a refusal this adapter can name reached the cache key and
            // nothing else. `Evidence.cs:63` is the field and `CampaignNearDuplicateDetector.cs:185-186` is
            // the shape. Null stays null rather than becoming an empty string, so an unnamed refusal is
            // still visible as unnamed.
            Attributes = reason is null ? null : [Attribute("reason", reason)],
        };

    /// <summary>One evidence attribute, matching how the other origins build theirs.</summary>
    private static EvidenceAttribute Attribute(string name, string value) => new() { Name = name, Value = value };

    /// <summary>
    /// Cache provenance for a call that did not consult a cache. The key digest is still computed,
    /// because a caching decorator wrapping this classifier keys on exactly this value.
    /// </summary>
    private CacheProvenance BuildCacheProvenance(
        SemanticMailInput input,
        int askableCount,
        string? modelVersion)
        => new()
        {
            Hit = false,
            KeyDigest = ComputeCacheKeyDigest(input, askableCount, modelVersion),
            CachedAt = null,
            ModelVersion = modelVersion,
            Stale = false,
        };

    /// <summary>
    /// Provenance for a call that never produced an answer. The digest records why, so an unavailable
    /// assessment is never mistaken for a cached clean result.
    /// </summary>
    private static CacheProvenance CacheUnavailable(string reason) => new()
    {
        Hit = false,
        KeyDigest = $"unavailable:{reason}",
        CachedAt = null,
        ModelVersion = null,
        Stale = false,
    };

    /// <summary>
    /// Digest over everything that could change the answer: model, the question text's version, this
    /// adapter's request-shape version, the context window, and the canonical request state.
    /// </summary>
    /// <remarks>
    /// <b>The shape version and the window belong here, not just the two model-and-question terms the
    /// hosted adapter keys on.</b> Both were measured to move an answer on this provider: the window
    /// because changing it reloads the model, and the shape because batching changed three of twelve
    /// answers. A key that omitted either would serve an assessment taken under one shape to a caller
    /// asking under another. <b>The batching figure is a measurement of <c>nimble-request-shape/1</c>
    /// and has not been re-taken under <c>/2</c>.</b> It is the reason the shape term is in the key at
    /// all, and it is a fact about the A/B letter rendering rather than about the shape that ships; see
    /// <see cref="NimbleQuestionSet"/> for the full status.
    /// <para>
    /// <b>Both windows are keyed, not only the requested one.</b> <see cref="NimbleOptions.NumCtx"/> is
    /// what is sent, but <see cref="NimbleOptions.AppliedContextWindow"/> is what truncation actually
    /// answers to, and <see cref="NimbleOptions.EffectiveNumCtx"/> can move the second without moving
    /// the first. Keying <c>num_ctx</c> alone would let a caller who set <c>EffectiveNumCtx</c> be
    /// served an assessment taken under the halved window, which is the divergence this digest exists
    /// to prevent. Keying the applied window rather than the option means an override that lands on the
    /// same window as the default does not fragment the cache, which is the behaviour the pair of
    /// assertions in the classifier tests pins down.
    /// </para>
    /// </remarks>
    private string ComputeCacheKeyDigest(
        SemanticMailInput input,
        int askableCount,
        string? modelVersion)
    {
        var (askable, _) = PartitionDimensions(input);
        var canonical = JsonSerializer.Serialize(
            new
            {
                model = modelVersion ?? _options.Model,
                schema = SemanticDimensions.QuestionSchemaVersion,
                request_shape = NimbleQuestionSet.Version,
                num_ctx = _options.NumCtx,
                applied_window = _options.AppliedContextWindow,
                asked = askable.Count,
                state = NimbleMessageState.Build(
                    input.Message,
                    input.TaggedContext,
                    input.Profile,
                    _options.MaxBodyCharacters,
                    _options.MaxLinks,
                    _options.MaxAttachments).State,
            },
            StateJson);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }
}
