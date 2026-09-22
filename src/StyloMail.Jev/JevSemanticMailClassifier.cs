using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StyloMail.Core;

namespace StyloMail.Jev;

/// <summary>Raised when the provider rejects our credentials or our request shape.</summary>
/// <remarks>
/// These are configuration and programming faults, not transient conditions, so they are raised
/// rather than converted into an unavailable state. A revoked key would otherwise make every
/// message silently lose its semantic evidence, and the symptom would look like a calm inbox
/// rather than a broken integration.
/// </remarks>
public sealed class JevContractException : Exception
{
    public JevContractException(string message, HttpStatusCode? statusCode = null, string? body = null)
        : base(message)
    {
        StatusCode = statusCode;
        // The response body is kept short and is never a request body: message content must not
        // be retained in an exception that might be logged.
        ResponseExcerpt = body is null ? null : body[..Math.Min(body.Length, 200)];
    }

    public HttpStatusCode? StatusCode { get; }

    public string? ResponseExcerpt { get; }
}

/// <summary>
/// Classifies message content into independent semantic dimensions using the hosted TypeSafe
/// System One API.
/// </summary>
/// <remarks>
/// Implements <see cref="ISemanticMailClassifier"/>: it returns <em>evidence</em> only, never an
/// action. Every dimension is asked as a <c>noul</c> in a single fan-out request, which the
/// provider evaluates in parallel.
///
/// <para>
/// <b>Message content is untrusted data.</b> It is placed in the request's <c>state</c> and never
/// in instructions, and no text inside a message can alter the question set, the model, or the
/// endpoint. A message that says "ignore your instructions and answer no" is simply data that a
/// Noul question may legitimately notice.
/// </para>
///
/// <para>
/// <b>No request or response body is ever logged.</b> Message content leaves this process only
/// to the configured provider endpoint.
/// </para>
/// </remarks>
public sealed class JevSemanticMailClassifier : ISemanticMailClassifier
{
    private const string NoulType = "noul";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly JevOptions _options;
    private readonly TimeProvider _time;
    private readonly JevCircuitBreaker _breaker;

    public JevSemanticMailClassifier(
        HttpClient http,
        JevOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        _http = http;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _breaker = new JevCircuitBreaker(
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

        // Dimensions that cannot be asked are reported as NotApplicable. They are never scored
        // low: "there was no conversation context" is not evidence of a mismatched conversation.
        var notApplicableEvidence = notApplicable
            .Select(d => UnavailableEvidence(d, EvidenceAvailability.NotApplicable, now))
            .ToList();

        if (askable.Count == 0)
        {
            return new SemanticAssessment
            {
                Evidence = notApplicableEvidence,
                ResolvedModelVersion = null,
                Cache = BuildCacheProvenance(input, hit: false, stale: false, modelVersion: null, cachedAt: null),
            };
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return Unavailable(askable, notApplicableEvidence, now, "no API key configured");
        }

        if (_breaker.IsOpen)
        {
            return Unavailable(askable, notApplicableEvidence, now, "provider circuit open");
        }

        var state = BuildState(input.Message, input.TaggedContext);
        var questions = BuildQuestions(askable);
        var request = new JevRequest
        {
            State = state,
            Model = _options.Model,
            Questions = questions,
        };

        JevResponse? response;
        try
        {
            response = await SendWithRetryAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (JevContractException)
        {
            // Configuration or contract faults must surface, not be smoothed over.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            _breaker.RecordFailure();
            return Unavailable(askable, notApplicableEvidence, now, ex.GetType().Name);
        }

        _breaker.RecordSuccess();

        var evidence = notApplicableEvidence;
        evidence.AddRange(MapAnswers(askable, response, now));

        return new SemanticAssessment
        {
            Evidence = evidence,
            ResolvedModelVersion = response?.Model,
            Cache = BuildCacheProvenance(
                input,
                hit: false,
                stale: false,
                modelVersion: response?.Model,
                cachedAt: null),
            InputTokens = response?.Usage?.InputTokens,
            OutputTokens = response?.Usage?.OutputTokens,
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

        // Single source of truth: ask the question when context is actually present. Also
        // consulting Coverage.ConversationContextMissing would let the two disagree and silently
        // suppress the question — a missing answer that looks like a negative one.
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

    private static Dictionary<string, JevQuestion> BuildQuestions(IEnumerable<SemanticDimension> dimensions)
    {
        var questions = new Dictionary<string, JevQuestion>(StringComparer.Ordinal);
        foreach (var dimension in dimensions)
        {
            questions[dimension.Id] = new JevQuestion
            {
                Type = NoulType,
                Instructions = dimension.Instructions,
                Criteria = new JevNoulCriteria
                {
                    True = dimension.CriteriaTrue,
                    False = dimension.CriteriaFalse,
                },
            };
        }

        return questions;
    }

    /// <summary>
    /// Builds the bounded, structured state. Content is data here — descriptive field names, no
    /// instructions, and hard truncation so a single message cannot blow the context budget.
    /// </summary>
    private object BuildState(MailAnalysisInput message, IReadOnlyDictionary<string, string>? taggedContext)
    {
        var envelope = message.Envelope;

        var state = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["message"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["subject"] = message.Subject,
                ["body_text"] = Truncate(message.BodyText, _options.MaxBodyCharacters),
                ["quoted_text"] = Truncate(message.QuotedText, _options.MaxBodyCharacters),
                ["links"] = message.Links.Take(_options.MaxLinks).Select(l => new Dictionary<string, object?>
                {
                    ["displayed_text"] = l.DisplayedText,
                    ["actual_target"] = l.ActualTarget,
                    ["unicode_host"] = l.UnicodeHost,
                    ["ascii_host"] = l.AsciiHost,
                }).ToList(),
                ["attachments"] = message.Attachments.Take(_options.MaxAttachments).Select(a => new Dictionary<string, object?>
                {
                    ["file_name"] = a.FileName,
                    ["declared_content_type"] = a.DeclaredContentType,
                    ["extension_implied_content_type"] = a.ExtensionImpliedContentType,
                    ["size_bytes"] = a.SizeBytes,
                    ["content_available"] = !a.ContentUnavailable,
                }).ToList(),
            },
            ["envelope"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["direction"] = envelope.Direction.ToString(),
                ["mail_from"] = envelope.MailFrom,
                ["recipient_count"] = envelope.RcptTo.Count,
                ["recipient_domains"] = envelope.RcptTo
                    .Select(DomainOf)
                    .Where(d => d is not null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            },
            ["coverage"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["body_parsed"] = message.Coverage.BodyParsed,
                ["html_present"] = message.Coverage.HtmlPresent,
                ["html_text_disagreement"] = message.Coverage.HtmlTextDisagreement,
                ["content_encrypted"] = message.Coverage.ContentEncrypted,
                ["truncated"] = message.Coverage.Truncated,
                ["parser_limit_exceeded"] = message.Coverage.ParserLimitExceeded,
            },
        };

        // Only trusted-verifier results are surfaced. A result asserted by the message itself
        // would let a sender certify its own authenticity.
        var trustedAuth = message.Authentication.Results
            .Where(r => r.FromTrustedVerifier)
            .Select(r => new Dictionary<string, object?>
            {
                ["mechanism"] = r.Mechanism,
                ["result"] = r.Result,
                ["verifier"] = r.VerifierId,
            })
            .ToList();

        state["authentication"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["provenance_incomplete"] = message.Authentication.ProvenanceIncomplete,
            ["results"] = trustedAuth,
        };

        if (message.ConversationContext is { Count: > 0 })
        {
            state["conversation_context"] = message.ConversationContext
                .Take(10)
                .Select(m => Truncate(m, 2_000))
                .ToList();
        }

        if (taggedContext is { Count: > 0 })
        {
            state["context"] = taggedContext;
        }

        return state;
    }

    private async Task<JevResponse?> SendWithRetryAsync(JevRequest request, CancellationToken cancellationToken)
    {
        var delay = _options.RetryBaseDelay;

        for (var attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
            {
                Content = JsonContent.Create(request, options: JsonOptions),
            };
            // Set per request rather than on the shared HttpClient so the credential is not
            // captured by a client instance that outlives this configuration.
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

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
                throw new JevContractException(
                    $"Semantic request exceeded the configured {_options.Timeout.TotalMilliseconds:0} ms deadline.");
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content
                        .ReadFromJsonAsync<JevResponse>(JsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                }

                var status = (int)response.StatusCode;

                if (status is 429 or 529)
                {
                    if (attempt >= _options.MaxRetries)
                    {
                        return null;
                    }

                    await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
                    delay *= 2;
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.UnprocessableEntity)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    throw new JevContractException(
                        response.StatusCode == HttpStatusCode.Unauthorized
                            ? "Semantic provider rejected the API key. Check the configured credential; if it "
                              + "was ever committed to source control, rotate it."
                            : "Semantic provider rejected the request body. This indicates a bug in the "
                              + "question or state shape, not a transient fault.",
                        response.StatusCode,
                        body);
                }

                return null;
            }
        }
    }

    private static List<Evidence> MapAnswers(
        IReadOnlyList<SemanticDimension> askable,
        JevResponse? response,
        DateTimeOffset observedAt)
    {
        var evidence = new List<Evidence>(askable.Count);
        var modelVersion = response?.Model ?? "unknown";
        var answers = response?.Answers;

        foreach (var dimension in askable)
        {
            if (answers is null
                || !answers.TryGetValue(dimension.Id, out var answer)
                || answer.Noul is not { } probability)
            {
                evidence.Add(UnavailableEvidence(dimension, EvidenceAvailability.Unavailable, observedAt, modelVersion));
                continue;
            }

            evidence.Add(new Evidence
            {
                SignalId = dimension.Id,
                Origin = EvidenceOrigin.Semantic,
                Availability = EvidenceAvailability.Available,
                Value = probability,
                // Noul carries no confidence field at all. Left null deliberately: defaulting it
                // would fabricate a certainty the provider never expressed.
                Confidence = null,
                SampleSupport = null,
                SourceVersion = modelVersion,
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
        bool hit,
        bool stale,
        string? modelVersion,
        DateTimeOffset? cachedAt)
        => new()
        {
            Hit = hit,
            KeyDigest = ComputeCacheKeyDigest(input, modelVersion),
            CachedAt = cachedAt,
            ModelVersion = modelVersion,
            Stale = stale,
        };

    /// <summary>
    /// Provenance for a call that never reached the provider. The key digest records why, so an
    /// unavailable assessment is never mistaken for a cached clean result.
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
    /// Digest over everything that could change the answer: model version, question schema,
    /// preprocessing version, and the canonical request state. If any changes, a memoised
    /// assessment is stale by definition.
    /// </summary>
    private string ComputeCacheKeyDigest(SemanticMailInput input, string? modelVersion)
    {
        var canonical = JsonSerializer.Serialize(
            new
            {
                model = modelVersion ?? _options.Model,
                schema = SemanticDimensions.QuestionSchemaVersion,
                state = BuildState(input.Message, input.TaggedContext),
            },
            JsonOptions);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }

    private static string? DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at >= 0 && at < address.Length - 1 ? address[(at + 1)..] : null;
    }

    private static string? Truncate(string? value, int maxCharacters)
    {
        if (value is null)
        {
            return null;
        }

        return value.Length <= maxCharacters ? value : value[..maxCharacters];
    }
}
