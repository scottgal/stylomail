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
/// <b>What this provider can say is narrower than what the port can express.</b> It answers with a
/// decision, so a dimension's value is 1.0 or 0.0 and a genuinely balanced result near 0.5 cannot be
/// expressed at all. That is a real limitation of the model, measured rather than assumed, and it is
/// recorded here rather than smoothed over by a mapping that would invent a gradation. See
/// <see cref="NimbleQuestionSet.MapCode"/>.
/// </para>
/// <para>
/// <b>Structural containment is not immunity, and nothing here claims it is.</b> The message travels
/// as data inside a single JSON <c>state</c> field, the system message says the description is data
/// and never an instruction, and the answer schema makes every property required with both values
/// drawn from the same two letters, so a hostile message cannot break the wire structure, cannot add
/// a question, and cannot make the decoder emit prose or an out-of-range answer. What none of that
/// can do is stop a model from being persuaded to answer the wrong letter. An attacker writes text
/// this model reads, and a small model can be talked into a false negative. So the guarantee is about
/// the *shape* of the answer, never about its truth, and a false negative arrives here
/// indistinguishable from a true one. That consequence is recorded at the policy tier as architecture
/// decision 23; this file's part in it is to produce an honest row and to claim nothing more.
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

        var system = NimbleQuestionSet.RenderSystem(askable);
        var prompt = FitPrompt(input, system);

        if (prompt is null)
        {
            // The questions plus a state holding no body at all still exceed the window. Answering
            // would mean describing a message the model did not read, so nothing is asked.
            return Unavailable(
                askable,
                notApplicableEvidence,
                now,
                "question set and message state exceed the configured context window");
        }

        var request = new NimbleGenerateRequest
        {
            Model = _options.Model,
            System = system,
            Prompt = prompt,
            Stream = false,
            Think = false,
            Format = NimbleQuestionSet.BuildSchema(askable),
            Options = new NimbleGenerationOptions
            {
                Temperature = 0,
                NumCtx = _options.NumCtx,
            },
        };

        NimbleGenerateResponse? response;
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
        if (response.PromptEvalCount is { } evaluated && evaluated >= _options.AppliedContextWindow)
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
        evidence.AddRange(MapAnswers(askable, response, now));

        return new SemanticAssessment
        {
            Evidence = evidence,
            // Ollama echoes the reference it was asked for and reports no resolved id, so unlike the
            // hosted adapter this is the configured reference rather than a provider-resolved version.
            // Recorded anyway: it is what answered, and the alternative is null, which would read as
            // "unknown provider".
            ResolvedModelVersion = response.Model,
            Cache = BuildCacheProvenance(input, askable.Count, response.Model),
            InputTokens = response.PromptEvalCount,
            OutputTokens = response.EvalCount,
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
    /// Renders the state into the prompt, shortening the body until the whole request fits the
    /// context window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a byte budget says something about tokens.</b> A tokeniser can never emit more tokens
    /// than there are bytes to cover, so a prompt of at most <c>num_ctx</c> bytes is at most
    /// <c>num_ctx</c> tokens. English text here runs about 5 characters per token, so in practice the
    /// bound is loose by roughly that factor and the body is not over-trimmed.
    /// </para>
    /// <para>
    /// <b>That is a bound, not a proof, and an earlier version of this comment called it one.</b> It
    /// shows the prompt is under the <em>requested</em> window in tokens, and the server applies about
    /// half of that (see <see cref="NimbleOptions.AppliedContextWindow"/>). The plausible worst case is
    /// dense low-entropy text, base64 or a long encoded URL, which runs nearer two characters per token
    /// than five: a prompt at the 8,192-byte ceiling can reach the 4,098-token applied window from
    /// above rather than below. So this method is best effort, and the guarantee that a partly-read
    /// message is never reported as an answer belongs to the backstop in the caller, which compares the
    /// evaluated count against the applied window.
    /// </para>
    /// <para>
    /// <b>The budget is not simply the applied window, and that is deliberate.</b> The rendered question
    /// set is 4,264 bytes on its own, so 'total bytes at most the applied window in tokens' would leave
    /// no room for a state at all and would refuse every message. The byte ceiling keeps the request
    /// small; the backstop keeps the answer honest when it is not small enough.
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
    private string? FitPrompt(SemanticMailInput input, string system)
    {
        var questionBytes = Encoding.UTF8.GetByteCount(system);
        var budget = _options.MaxBodyCharacters;

        while (true)
        {
            var prompt = JsonSerializer.Serialize(
                NimbleMessageState.Build(
                    input.Message,
                    input.TaggedContext,
                    input.Profile,
                    budget,
                    _options.MaxLinks,
                    _options.MaxAttachments).State,
                StateJson);

            var total = questionBytes + Encoding.UTF8.GetByteCount(prompt);

            if (total <= _options.NumCtx)
            {
                return prompt;
            }

            if (budget == 0)
            {
                return null;
            }

            // Remove the measured excess plus the margin, and never less than a step, so a prompt
            // that is barely over the line still converges.
            var excess = total - _options.NumCtx;
            var step = Math.Max(excess + PromptByteMargin, 64);
            budget = budget > step ? budget - step : 0;
        }
    }

    private async Task<NimbleGenerateResponse?> SendWithRetryAsync(
        NimbleGenerateRequest request,
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
                        .ReadFromJsonAsync<NimbleGenerateResponse>(WireJson, cancellationToken)
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
                    throw new NimbleContractException(
                        response.StatusCode == HttpStatusCode.NotFound
                            ? $"The local provider has no model '{request.Model}'. Check the configured "
                              + "model reference and that the endpoint is the server holding it; a "
                              + "second Ollama on another port answers here and serves different models."
                            : "The local provider rejected the request body. This indicates a bug in the "
                              + "question, schema or state shape, not a transient fault.",
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
    /// <b>A 0.0 here is a decision, and it is not clearance.</b> This provider answers with a letter, so
    /// a dimension that was not detected and a dimension the model agreed to suppress both arrive as
    /// <c>Available</c> with <c>Value</c> 0.0, and nothing in this row or in this file can tell them
    /// apart. Downstream policy that reads 0.0 as "looked at and found absent" is reading something
    /// this provider cannot say. The port has no state for "answered, but wrongly", so the honest
    /// handling is to say so here rather than to leave the number looking like a finding.
    /// </para>
    /// <para>
    /// That is the whole reason <c>Confidence</c> stays null and <c>SourceVersion</c> names the shape:
    /// the row carries every fact there is about how much it is worth, and fabricating a confidence
    /// would be inventing exactly the distinction this limitation is about.
    /// </para>
    /// </remarks>
    private static List<Evidence> MapAnswers(
        IReadOnlyList<SemanticDimension> askable,
        NimbleGenerateResponse response,
        DateTimeOffset observedAt)
    {
        var evidence = new List<Evidence>(askable.Count);
        var modelVersion = response.Model ?? "unknown";
        var codes = TryReadCodes(response.Response);

        for (var index = 0; index < askable.Count; index++)
        {
            var dimension = askable[index];

            if (codes is null
                || !codes.TryGetValue(NimbleQuestionSet.KeyFor(index), out var code)
                || NimbleQuestionSet.MapCode(code) is not { } value)
            {
                evidence.Add(UnavailableEvidence(dimension, EvidenceAvailability.Unavailable, observedAt, modelVersion));
                continue;
            }

            evidence.Add(new Evidence
            {
                SignalId = dimension.Id,
                Origin = EvidenceOrigin.Semantic,
                Availability = EvidenceAvailability.Available,
                Value = value,

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
    /// Reads the constrained answer object, or null if the body is not one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Strict, and deliberately not tolerant.</b> The request carries a JSON schema that constrains
    /// every answer to one of two letters, so the body is either that object or evidence that the
    /// server did not honour the contract. A parser flexible enough to find codes inside prose would
    /// also be flexible enough to find one inside an explanation of why no answer was given, and that
    /// is a fabricated answer with a plausible shape. A body that does not parse means the schema was
    /// not applied, most likely because the endpoint is a different server than the one that was
    /// measured, and the honest result is that nothing is available.
    /// </para>
    /// </remarks>
    private static Dictionary<string, string>? TryReadCodes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var codes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    codes[property.Name] = property.Value.GetString()!;
                }
            }

            return codes;
        }
        catch (JsonException)
        {
            return null;
        }
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
            UnavailableEvidence(d, EvidenceAvailability.Unavailable, now)));

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
        string sourceVersion = "none")
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
        };

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
    /// hosted adapter keys on.</b> Both were measured to move an answer on this provider, the window
    /// because changing it reloads the model, the shape because batching changed three of twelve
    /// answers. A key that omitted either would serve an assessment taken under one shape to a caller
    /// asking under another.
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
