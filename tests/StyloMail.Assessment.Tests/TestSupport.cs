using StyloMail.Adaptive.Profiles;
using StyloMail.Assessment;
using StyloMail.Assessment.Semantic;
using StyloMail.Core;
using StyloMail.Mime;
using StyloMail.Queue;

namespace StyloMail.Assessment.Tests;

/// <summary>
/// A clock that only moves when a test moves it.
/// </summary>
/// <remarks>
/// Every timestamp in the pipeline comes from the assessment context, so a fixed clock here is what
/// makes an assertion about expiry, direction or ordering reproducible rather than a race against
/// the wall. Nothing in the pipeline creates a timer, so the timer factory is deliberately
/// unsupported: a test that needed one would be testing something the pipeline does not do.
/// </remarks>
internal sealed class FixedClock : TimeProvider
{
    private DateTimeOffset _now;

    public FixedClock(DateTimeOffset? start = null) =>
        _now = start ?? new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        throw new NotSupportedException("The assessment pipeline creates no timers.");
}

/// <summary>Records the order in which pipeline stages ran.</summary>
internal sealed class StepRecorder
{
    private readonly Lock _gate = new();
    private readonly List<string> _steps = [];

    public void Record(string step)
    {
        lock (_gate)
        {
            _steps.Add(step);
        }
    }

    public IReadOnlyList<string> Steps
    {
        get
        {
            lock (_gate)
            {
                return [.. _steps];
            }
        }
    }

    public int IndexOf(string step)
    {
        lock (_gate)
        {
            return _steps.IndexOf(step);
        }
    }
}

/// <summary>A MIME analyzer that returns whatever it was configured with, and records its turn.</summary>
internal sealed class RecordingMimeAnalyzer : IMimeMessageAnalyzer
{
    private readonly StepRecorder _recorder;
    private readonly FixedClock _clock;

    public RecordingMimeAnalyzer(StepRecorder recorder, FixedClock clock)
    {
        _recorder = recorder;
        _clock = clock;
    }

    public int CallCount { get; private set; }

    public string EvidenceSignalId { get; set; } = "mime.link.display_mismatch";

    public bool Reject { get; set; }

    public MimeParseDisposition RejectionDisposition { get; set; } = MimeParseDisposition.LimitExceeded;

    public MimeAnalysisResult Analyze(MimeAnalysisRequest request)
    {
        CallCount++;
        _recorder.Record("mime");

        var evidence = new List<Evidence>
        {
            new()
            {
                SignalId = EvidenceSignalId,
                Origin = EvidenceOrigin.Deterministic,
                Availability = EvidenceAvailability.Available,
                Value = 1.0,
                SourceVersion = "mime/1",
                ObservedAt = _clock.GetUtcNow(),
                ObservedScope = "message",
            },
        };

        if (Reject)
        {
            return new MimeAnalysisResult
            {
                Disposition = RejectionDisposition,
                Message = null,
                Evidence = evidence,
                Coverage = Coverage(),
                Rejection = new MimeParseRejection
                {
                    Disposition = RejectionDisposition,
                    Reason = "part-count",
                    LimitName = "MaxParts",
                    Observed = 900,
                    Limit = 100,
                },
            };
        }

        return new MimeAnalysisResult
        {
            Disposition = MimeParseDisposition.Parsed,
            Message = request.Envelope is null ? null : InputFor(request.Envelope),
            Evidence = evidence,
            Coverage = Coverage(),
        };
    }

    private static AnalysisCoverage Coverage() => new()
    {
        BodyParsed = true,
        HtmlPresent = false,
        HasAttachments = false,
        HtmlTextDisagreement = false,
        ParserLimitExceeded = false,
        ContentEncrypted = false,
        Truncated = false,
        ConversationContextMissing = true,
    };

    private static MailAnalysisInput InputFor(MailEnvelope envelope) => new()
    {
        Envelope = envelope,
        Authentication = Builders.AuthenticationContext(),
        Subject = "subject",
        BodyText = "body",
        Links = [],
        Attachments = [],
        Coverage = Coverage(),
    };
}

/// <summary>
/// A semantic classifier under the test's control.
/// </summary>
/// <remarks>
/// Counts calls, because most of what the cache must be shown to do is about <em>not</em> calling
/// this. It can also block, which is how single-flight is tested without sleep-based races.
/// </remarks>
internal sealed class RecordingSemanticClassifier : ISemanticMailClassifier
{
    private readonly StepRecorder? _recorder;
    private readonly FixedClock _clock;

    public RecordingSemanticClassifier(FixedClock clock, StepRecorder? recorder = null)
    {
        _clock = clock;
        _recorder = recorder;
    }

    public int CallCount { get; private set; }

    public string ResolvedModelVersion { get; set; } = "jev-1.13.0";

    /// <summary>When true, every dimension is reported unavailable, as during a provider outage.</summary>
    public bool Unavailable { get; set; }

    /// <summary>Per-dimension probability overrides, so a test can make one message differ.</summary>
    public Dictionary<string, double> Values { get; } = new(StringComparer.Ordinal)
    {
        ["semantic.payment_redirection"] = 0.1,
        ["semantic.credential_request"] = 0.1,
    };

    public int ConcurrentCalls { get; private set; }

    public int MaxConcurrentCalls { get; private set; }

    /// <summary>Released by the test to let a deliberately-blocked classification finish.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async ValueTask<SemanticAssessment> ClassifyAsync(
        SemanticMailInput input,
        CancellationToken cancellationToken)
    {
        CallCount++;
        _recorder?.Record("semantic");

        ConcurrentCalls++;
        MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, ConcurrentCalls);

        try
        {
            if (Gate is { } gate)
            {
                await gate.Task.ConfigureAwait(false);
            }

            var evidence = new List<Evidence>();

            foreach (var dimension in input.Dimensions)
            {
                evidence.Add(new Evidence
                {
                    SignalId = dimension.Id,
                    Origin = EvidenceOrigin.Semantic,
                    Availability = Unavailable
                        ? EvidenceAvailability.Unavailable
                        : EvidenceAvailability.Available,
                    Value = Unavailable ? null : Values.GetValueOrDefault(dimension.Id, 0.05),
                    // Noul carries no confidence. Left null rather than defaulted, which is the
                    // contract the provider actually offers.
                    Confidence = null,
                    SourceVersion = ResolvedModelVersion,
                    ObservedAt = _clock.GetUtcNow(),
                    ObservedScope = "message",
                });
            }

            return new SemanticAssessment
            {
                Evidence = evidence,
                ResolvedModelVersion = Unavailable ? null : ResolvedModelVersion,
                Cache = new CacheProvenance
                {
                    Hit = false,
                    KeyDigest = "inner",
                    Stale = false,
                },
            };
        }
        finally
        {
            ConcurrentCalls--;
        }
    }
}

/// <summary>An in-memory profile store, so the pipeline is testable without a database.</summary>
internal sealed class FakeProfileStore : IAdaptiveProfileStore
{
    private readonly StepRecorder? _recorder;
    private readonly Dictionary<ProfileKey, AdaptiveProfile> _profiles = [];

    public FakeProfileStore(StepRecorder? recorder = null) => _recorder = recorder;

    public int LoadCount { get; private set; }

    /// <summary>
    /// Returns the stored instance <b>by reference</b>, unlike the real store, which reconstructs a
    /// profile from the database on every load.
    /// </summary>
    /// <remarks>
    /// <b>This is a fidelity gap and it changes what a test can prove.</b> With a live reference,
    /// mutating a loaded profile also mutates what is stored, so a caller that reloads after a
    /// failed write would see its own previous attempt rather than the untouched stored row.
    ///
    /// <para>
    /// Consequence: <b>anything that depends on reload-after-conflict semantics belongs in
    /// <c>AssessmentPipelineIntegrationTests</c> against the real store</b>, not here.
    /// </para>
    /// </remarks>
    public AdaptiveProfile? Load(ProfileKey key)
    {
        LoadCount++;
        _recorder?.Record("profile.read");
        return _profiles.GetValueOrDefault(key);
    }

    /// <summary>Observations applied through the store's delta path.</summary>
    public int ApplyObservationCount { get; private set; }

    /// <summary>Whole-profile updates applied through the store's transactional path.</summary>
    public int UpdateCount { get; private set; }

    /// <summary>
    /// The delta write: merge into whatever is stored, creating the profile if nobody has seen it.
    /// </summary>
    /// <remarks>
    /// Deliberately without any conflict behaviour, because that is the contract: a delta takes the
    /// write lock before it reads, so it has nothing to conflict with. A fake that could refuse one
    /// would make the two store operations look interchangeable, which is the opposite of why they
    /// are separate.
    /// </remarks>
    public void ApplyObservation(ProfileKey key, ProfileObservation observation, DateTimeOffset at)
    {
        ApplyObservationCount++;
        _recorder?.Record("profile.observe");

        var profile = _profiles.TryGetValue(key, out var existing) ? existing : new AdaptiveProfile(key);
        profile.Observe(observation);
        _profiles[key] = profile;
    }

    /// <summary>The transactional update: run the caller's decision, then write.</summary>
    /// <remarks>
    /// Returns the delegate's own result, as the store does, so a caller that reads a promotion
    /// outcome gets the one its decision produced.
    /// </remarks>
    public T Update<T>(ProfileKey key, DateTimeOffset at, Func<AdaptiveProfile, T> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        UpdateCount++;
        _recorder?.Record("profile.update");

        var profile = _profiles.TryGetValue(key, out var existing) ? existing : new AdaptiveProfile(key);
        var result = update(profile);
        _profiles[key] = profile;

        return result;
    }

    /// <summary>Reads what is stored without going through the port. For assertions only.</summary>
    public AdaptiveProfile? Peek(ProfileKey key) => _profiles.GetValueOrDefault(key);

    /// <summary>Every profile currently held. Used to assert that nothing learned.</summary>
    public IReadOnlyCollection<AdaptiveProfile> Profiles => [.. _profiles.Values];

    /// <summary>Total baseline version across every profile — zero until something is genuinely learned.</summary>
    public int TotalBaselineVersion => _profiles.Values.Sum(profile => profile.Baseline.Version);
}

/// <summary>
/// An acceptance queue that records what it was asked, or refuses to be asked at all.
/// </summary>
/// <remarks>
/// The strict mode is the point of several tests: a queue that throws when reached is how
/// "assessment-only never touches the acceptance path" becomes an assertion rather than a
/// reading of the code.
/// </remarks>
internal sealed class RecordingAcceptanceQueue : IMessageAcceptanceQueue
{
    private readonly StepRecorder? _recorder;
    private readonly bool _throwIfReached;

    public RecordingAcceptanceQueue(StepRecorder? recorder = null, bool throwIfReached = false)
    {
        _recorder = recorder;
        _throwIfReached = throwIfReached;
    }

    public List<QueueSubmission> Submissions { get; } = [];

    public QueueAdmission Admission { get; set; } = QueueAdmission.Accepted;

    public bool ThrowSpoolUnavailable { get; set; }

    public int CallCount => Submissions.Count;

    /// <summary>Queue ids minted, keyed by tenant and idempotency key, so a replay behaves like one.</summary>
    private readonly Dictionary<(string TenantId, string Key), string> _byIdempotencyKey = [];

    /// <summary>
    /// Queue ids that exist, so an assertion can distinguish "accepted" from "accepted twice".
    /// </summary>
    public int DistinctQueueItems => _byIdempotencyKey.Count == 0
        ? Submissions.Count
        : _byIdempotencyKey.Count;

    /// <summary>
    /// Every queue id this fake has returned, in call order.
    /// </summary>
    /// <remarks>
    /// Recorded so a test can assert the assessor reports the id the <em>queue</em> minted rather
    /// than merely a non-null one. "Is there an id?" and "is it the right id?" are different
    /// questions, and a fabricated value answers yes to the first.
    /// </remarks>
    public List<string> ReturnedQueueIds { get; } = [];

    public Task<QueueAcceptResult> AcceptAsync(QueueSubmission submission, CancellationToken cancellationToken)
    {
        if (_throwIfReached)
        {
            throw new InvalidOperationException(
                "The acceptance path was reached for a message that must never reach it.");
        }

        _recorder?.Record("queue");
        Submissions.Add(submission);

        if (ThrowSpoolUnavailable)
        {
            throw new SpoolUnavailableException("the spool is unavailable");
        }

        if (Admission != QueueAdmission.Accepted)
        {
            return Task.FromResult(QueueAcceptResult.Refused(Admission, "refused by the test"));
        }

        // Modelled rather than ignored. A fake that accepts every call returns success for both
        // halves of a duplicated acceptance, which is precisely how a seam that queued one message
        // twice stayed green in the host's suite.
        if (submission.IdempotencyKey is { Length: > 0 } key)
        {
            var lookup = (submission.TenantId, key);

            if (_byIdempotencyKey.TryGetValue(lookup, out var existing))
            {
                ReturnedQueueIds.Add(existing);
                return Task.FromResult(QueueAcceptResult.Duplicate(existing, "replay"));
            }

            var minted = $"q_{_byIdempotencyKey.Count + 1}";
            _byIdempotencyKey[lookup] = minted;
            ReturnedQueueIds.Add(minted);

            return Task.FromResult(QueueAcceptResult.Accepted(minted));
        }

        var anonymous = $"q_anon_{Submissions.Count}";
        ReturnedQueueIds.Add(anonymous);

        return Task.FromResult(QueueAcceptResult.Accepted(anonymous));
    }
}

/// <summary>Builders for the Core types the pipeline consumes.</summary>
internal static class Builders
{
    public static readonly byte[] RawMessage = "Subject: test\r\n\r\nhello"u8.ToArray();

    public static AuthenticationContext AuthenticationContext(
        IReadOnlyList<string>? approvedSenderIdentities = null) => new()
        {
            AuthenticatedAccount = null,
            ConnectingIp = "203.0.113.10",
            Results =
            [
                new AuthenticationResult
                {
                    Mechanism = "dkim",
                    Result = "pass",
                    VerifierId = "boundary-1",
                    FromTrustedVerifier = true,
                    Detail = "d=example.com; s=sel",
                },
            ],
            ApprovedSenderIdentities = approvedSenderIdentities ?? [],
            ProvenanceIncomplete = false,
        };

    public static MailEnvelope Envelope(
        string tenantId = "tenant-1",
        MailDirection direction = MailDirection.Inbound,
        string mailFrom = "sender@example.com",
        IReadOnlyList<string>? recipients = null,
        string payloadReference = "spool://tenant-1/msg-1",
        string internalMessageId = "msg-1",
        int? hopCount = null) => new()
        {
            InternalMessageId = internalMessageId,
            TenantId = tenantId,
            Direction = direction,
            TrustedPrincipalId = "principal-1",
            MailFrom = mailFrom,
            RcptTo = recipients ?? ["recipient@example.com"],
            ReceivedAt = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero),
            MimeDigest = "digest-" + internalMessageId,
            PayloadReference = payloadReference,
            UntrustedMessageIdHeader = null,
            HopCount = hopCount,
        };

    public static AnalysisCoverage Coverage(bool conversationContextMissing = true) => new()
    {
        BodyParsed = true,
        HtmlPresent = false,
        HasAttachments = false,
        HtmlTextDisagreement = false,
        ParserLimitExceeded = false,
        ContentEncrypted = false,
        Truncated = false,
        ConversationContextMissing = conversationContextMissing,
    };

    public static MailAnalysisInput Message(
        string bodyText = "Please update the payment details for invoice 4471.",
        MailEnvelope? envelope = null,
        string? subject = "Invoice",
        IReadOnlyList<LinkObservation>? links = null,
        IReadOnlyList<AttachmentMetadata>? attachments = null) => new()
        {
            Envelope = envelope ?? Envelope(),
            Authentication = AuthenticationContext(),
            Subject = subject,
            BodyText = bodyText,
            QuotedText = null,
            Links = links ?? [],
            Attachments = attachments ?? [],
            Coverage = Coverage(),
        };

    public static AssessmentContext Context(
        FixedClock clock,
        string tenantId = "tenant-1",
        bool shadowMode = false,
        bool assessmentOnly = false,
        string correlationId = "corr-1",
        string? clientIdempotencyKey = null) => new()
        {
            TenantId = tenantId,
            ShadowMode = shadowMode,
            AssessmentOnly = assessmentOnly,
            CorrelationId = correlationId,
            TimeProvider = clock,
            ClientIdempotencyKey = clientIdempotencyKey,
        };

    public static MailAssessorOptions Options(
        string? assessmentPathLearningRuleId = null,
        SemanticCacheOptions? semanticCache = null) => new()
        {
            ProfileKeyHasher = new ProfileKeyHasher(new byte[32]),
            SemanticCache = semanticCache ?? new SemanticCacheOptions { ClassifierModelVersion = "jev-1.13.0" },
            AssessmentPathLearningRuleId = assessmentPathLearningRuleId,
        };

    /// <summary>Builds a message with one link, which is enough to make a fingerprint differ.</summary>
    public static MailAnalysisInput MessageWithLink(
        string target,
        string bodyText = "Please update the payment details.",
        string? envelopeInternalId = null)
    {
        var link = new LinkObservation
        {
            DisplayedText = "here",
            ActualTarget = target,
        };

        return Message(
            bodyText,
            Envelope(internalMessageId: envelopeInternalId ?? "msg-1"),
            links: [link]);
    }
}
