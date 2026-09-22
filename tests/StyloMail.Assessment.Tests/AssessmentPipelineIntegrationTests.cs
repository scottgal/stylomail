using StyloMail.Assessment.Semantic;
using StyloMail.Core;
using StyloMail.Persistence;
using StyloMail.Queue;

namespace StyloMail.Assessment.Tests;

/// <summary>
/// The wiring, exercised against the real queue, the real spool and the real profile store.
/// </summary>
/// <remarks>
/// The unit tests above use fakes, which is what makes them fast and what lets them assert that a
/// component was <em>not</em> called. This one exists for the opposite reason: the adapters
/// (<see cref="SpoolRawMessageSource"/>, <see cref="QueueStoreAcceptanceQueue"/>,
/// <see cref="SqliteAdaptiveProfileStoreAdapter"/>) are the only place where my assumptions about
/// another component's API become code, and an assumption that compiles is not the same as one
/// that holds. A payload written by the spool has to be readable by the source; a submission the
/// assessor builds has to be acceptable by the queue.
/// </remarks>
/// <summary>
/// A profile store that lets a burst land in the window between a whole-profile load and its save.
/// </summary>
/// <remarks>
/// Exists to force a race that a background thread only sometimes produced. Determinism is the point:
/// the first version of the test it serves was flaky, and a flaky safety test is not evidence of
/// anything, it is a red build somebody will eventually call noise.
/// </remarks>
internal sealed class BurstInterposingProfileStore : StyloMail.Assessment.IAdaptiveProfileStore
{
    private readonly StyloMail.Assessment.IAdaptiveProfileStore _inner;
    private readonly FixedClock _clock;

    public BurstInterposingProfileStore(StyloMail.Assessment.IAdaptiveProfileStore inner, FixedClock clock)
    {
        _inner = inner;
        _clock = clock;
    }

    public StyloMail.Adaptive.Profiles.AdaptiveProfile? Load(StyloMail.Adaptive.Profiles.ProfileKey key) =>
        _inner.Load(key);

    /// <summary>
    /// Runs the caller's decision, with an observation landing inside the same call first.
    /// </summary>
    /// <remarks>
    /// The interposed observation is what a sustained burst does to a whole-profile write. It sits
    /// here rather than on a background thread so the race is deterministic, see the test it serves.
    /// </remarks>
    public T Update<T>(
        StyloMail.Adaptive.Profiles.ProfileKey key,
        DateTimeOffset at,
        Func<StyloMail.Adaptive.Profiles.AdaptiveProfile, T> update)
    {
        _inner.ApplyObservation(
            key,
            new StyloMail.Adaptive.Profiles.ProfileObservation
            {
                ObservedAt = _clock.GetUtcNow(),
                RecipientCount = 1,
                WasRejected = false,
            },
            at);

        return _inner.Update(key, at, update);
    }

    public void ApplyObservation(
        StyloMail.Adaptive.Profiles.ProfileKey key,
        StyloMail.Adaptive.Profiles.ProfileObservation observation,
        DateTimeOffset at) => _inner.ApplyObservation(key, observation, at);
}

public sealed class AssessmentPipelineIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "stylomail-assessment-" + Guid.NewGuid().ToString("N"));

    public AssessmentPipelineIntegrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A test that leaves a spool file locked should not fail teardown for it.
        }
    }

    [Fact]
    public async Task ASubmissionTravelsFromTheSpoolThroughPolicyAndIntoTHeDurableQueue()
    {
        var clock = new FixedClock();
        var connections = new SqliteConnectionFactory(Path.Combine(_root, "stylomail.db"));
        var spool = new SpoolStore(Path.Combine(_root, "spool"));
        var classifier = new RecordingSemanticClassifier(clock);

        var assessor = AssessmentPipeline.Create(
            new RecordingMimeAnalyzer(new StepRecorder(), clock),
            classifier,
            connections,
            spool,
            Builders.Options(),
            new QueueOptions { TimeProvider = clock });

        // Written by the spool itself, so the reference is one the spool will recognise, which is
        // the assumption the payload source rests on and the one worth proving.
        var payloadReference = await spool.WriteAsync(
            "tenant-1",
            "queue-1",
            Builders.RawMessage,
            CancellationToken.None);

        var envelope = Builders.Envelope(payloadReference: payloadReference);
        var message = Builders.Message("Please update the payment details.", envelope);

        var assessment = await assessor.AssessAsync(
            message,
            Builders.Context(clock, clientIdempotencyKey: "client-key-1"),
            CancellationToken.None);

        Assert.Equal(MailAction.Allow, assessment.Action);
        Assert.NotEmpty(assessment.RecipientDispositions);

        // The queue really holds it: acceptance is defined by a durable row existing, so looking for
        // one is the only assertion that means anything.
        var queue = new QueueStore(connections, spool, new QueueOptions { TimeProvider = clock });
        var lookup = await queue.FindSubmissionAsync(
            "tenant-1",
            "client-key-1",
            CancellationToken.None);

        Assert.NotNull(lookup);
        Assert.Equal("digest-msg-1", lookup!.MimeDigest);

        // The id the caller is handed back is the one the queue actually minted, looked up
        // independently here rather than taken on trust from the value under test.
        Assert.Equal(lookup.QueueId, assessment.SubmissionId);
    }

    [Fact]
    public async Task AReplayedSubmissionQueuesOneItemAndReturnsOneId()
    {
        var clock = new FixedClock();
        var connections = new SqliteConnectionFactory(Path.Combine(_root, "stylomail.db"));
        var spool = new SpoolStore(Path.Combine(_root, "spool"));

        var assessor = AssessmentPipeline.Create(
            new RecordingMimeAnalyzer(new StepRecorder(), clock),
            new RecordingSemanticClassifier(clock),
            connections,
            spool,
            Builders.Options(),
            new QueueOptions { TimeProvider = clock });

        var payloadReference = await spool.WriteAsync(
            "tenant-1",
            "queue-1",
            Builders.RawMessage,
            CancellationToken.None);

        var message = Builders.Message(
            "Please update the payment details.",
            Builders.Envelope(payloadReference: payloadReference));

        var context = Builders.Context(clock, clientIdempotencyKey: "client-key-1");

        var first = await assessor.AssessAsync(message, context, CancellationToken.None);
        var replay = await assessor.AssessAsync(message, context, CancellationToken.None);

        // The seam defect this guards against: the assessor and the host each accepting under a
        // different key, which the queue cannot dedupe. Here there is one acceptor and one key, so a
        // retry lands on the item that already exists instead of becoming a second delivery.
        Assert.Equal(first.SubmissionId, replay.SubmissionId);

        var queue = new QueueStore(connections, spool, new QueueOptions { TimeProvider = clock });
        var lookup = await queue.FindSubmissionAsync("tenant-1", "client-key-1", CancellationToken.None);

        Assert.NotNull(lookup);
        Assert.Equal(first.SubmissionId, lookup!.QueueId);
    }

    [Fact]
    public async Task AHopCountAtTheLimitIsRefusedByTheRealQueueAndBecomesADeferral()
    {
        var clock = new FixedClock();
        var connections = new SqliteConnectionFactory(Path.Combine(_root, "stylomail.db"));
        var spool = new SpoolStore(Path.Combine(_root, "spool"));
        var queueOptions = new QueueOptions { TimeProvider = clock };

        var assessor = AssessmentPipeline.Create(
            new RecordingMimeAnalyzer(new StepRecorder(), clock),
            new RecordingSemanticClassifier(clock),
            connections,
            spool,
            Builders.Options(),
            queueOptions);

        var payloadReference = await spool.WriteAsync(
            "tenant-1",
            "queue-1",
            Builders.RawMessage,
            CancellationToken.None);

        // Exactly MaxHops: the mail-loop backstop's first refusing value. Before the envelope carried
        // a hop count this could not be reached at all, the queue compared a permanent default of 0
        // against the limit, so the guard read as present and was inert end to end.
        var message = Builders.Message(
            "Please update the payment details.",
            Builders.Envelope(payloadReference: payloadReference, hopCount: queueOptions.MaxHops));

        var assessment = await assessor.AssessAsync(
            message,
            Builders.Context(clock),
            CancellationToken.None);

        // Refused before acceptance, and reported as a deferral rather than a claimed delivery.
        Assert.Equal(MailAction.Defer, assessment.Action);
        Assert.Null(assessment.SubmissionId);
        Assert.Contains(assessment.Reasons, r => r.Code == AssessmentReasonCodes.AcceptanceRefused);
    }

    [Fact]
    public async Task AnAssessmentOnlyCallLeavesTheQueueEmpty()
    {
        var clock = new FixedClock();
        var connections = new SqliteConnectionFactory(Path.Combine(_root, "stylomail.db"));
        var spool = new SpoolStore(Path.Combine(_root, "spool"));

        var assessor = AssessmentPipeline.Create(
            new RecordingMimeAnalyzer(new StepRecorder(), clock),
            new RecordingSemanticClassifier(clock),
            connections,
            spool,
            Builders.Options(),
            new QueueOptions { TimeProvider = clock });

        var message = Builders.Message(
            envelope: Builders.Envelope(payloadReference: PayloadReferences.Ephemeral));

        var assessment = await assessor.AssessAsync(
            message,
            Builders.Context(clock, assessmentOnly: true),
            CancellationToken.None);

        Assert.Empty(assessment.RecipientDispositions);

        var states = await new QueueStore(connections, spool, new QueueOptions { TimeProvider = clock })
            .CountByStateAsync("tenant-1", CancellationToken.None);

        Assert.Empty(states);
    }

    [Fact]
    public async Task AConcurrentBurstOnOneProfileIsSerialisedRatherThanRaced()
    {
        var clock = new FixedClock();
        var connections = new SqliteConnectionFactory(Path.Combine(_root, "stylomail.db"));
        var store = new StyloMail.Adaptive.Storage.SqliteAdaptiveProfileStore(connections);
        store.EnsureCreated();

        var coordinator = new ProfileCoordinator(new SqliteAdaptiveProfileStoreAdapter(store));
        var key = StyloMail.Adaptive.Profiles.ProfileScopes.OutboundSender("tenant-1", "principal-1");

        const int writers = 24;
        const int observationsEach = 4;
        const int total = writers * observationsEach;

        // Task.Run so the writers occupy real threads: Microsoft.Data.Sqlite's async methods are
        // synchronous underneath, so a bare WhenAll over these would serialise and prove nothing.
        var tasks = Enumerable
            .Range(0, writers)
            .Select(_ => Task.Run(async () =>
            {
                for (var i = 0; i < observationsEach; i++)
                {
                    coordinator.Observe(
                        key,
                        new StyloMail.Adaptive.Profiles.ProfileObservation
                        {
                            ObservedAt = clock.GetUtcNow(),
                            RecipientCount = 1,
                            WasRejected = false,
                        },
                        clock.GetUtcNow());
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        var final = store.Load(key);
        Assert.NotNull(final);

        // Every observation counted. This is the counter that bounds a compromised sender's
        // throughput, and an undercount is invisible in exactly the direction that matters.
        Assert.Equal(total, final!.Observed.Attempts);
        Assert.Equal(total, final.Observed.Recipients);

        // The two assertions that make this test load-bearing rather than merely passing. Asserting
        // only that everything landed is not enough: the compare-and-swap retry is quite capable of
        // absorbing this burst on its own, and it did, a version of this test without the gate
        // passed in isolation and failed only under full-suite load, which is the least useful kind
        // of regression test there is. These two are deterministic.
        //
        // Observing happened, and no whole-profile write did. The delta write takes the lock before
        // it reads, so there is nothing for a burst to lose, and asserting the *absence* of the
        // whole-profile path is what proves observations are not quietly back on it, where a burst
        // would be many writers racing one row instead of queueing on one lock.
        Assert.Equal(total, coordinator.Statistics.Observed);
        Assert.Equal(0, coordinator.Statistics.Mutated);
    }

    [Fact]
    public async Task APromotionSurvivesSustainedIngestOnTheSameProfile()
    {
        var clock = new FixedClock();
        var connections = new SqliteConnectionFactory(Path.Combine(_root, "stylomail.db"));
        var store = new StyloMail.Adaptive.Storage.SqliteAdaptiveProfileStore(connections);
        store.EnsureCreated();

        var key = StyloMail.Adaptive.Profiles.ProfileScopes.OutboundSender("tenant-1", "principal-1");

        // Interposes an ingest write before every whole-profile save, which is what a sustained burst
        // does to it. The first version of this test ran a real burst on a background thread instead
        // and was FLAKY: it passed three runs in a row and then a probe caught the failure,         // 4 save attempts, 4 conflicts, the promotion lost. A test that reddens only sometimes is
        // worse than none, because the failure reads as flakiness rather than as a regression, so
        // the race is forced here instead of hoped for.
        var interposing = new BurstInterposingProfileStore(
            new SqliteAdaptiveProfileStoreAdapter(store),
            clock);

        var coordinator = new ProfileCoordinator(interposing);

        var sample = new StyloMail.Adaptive.Profiles.TrustedSample
        {
            Dimensions = StyloMail.Adaptive.Profiles.DimensionVector.Create(
                StyloMail.Adaptive.Profiles.DimensionSample.Available(
                    SemanticDimensions.ConversationalContinuityId,
                    0.5)),
            Provenance = StyloMail.Adaptive.Profiles.LabelProvenance.AuthenticatedOperator,
            RecordedAt = clock.GetUtcNow(),
        };

        var version = coordinator.Mutate(key, clock.GetUtcNow(), profile =>
        {
            profile.Promote(sample);
            return profile.Baseline.Version;
        });

        // The promotion LANDS while a burst lands around it. This is the assertion that used to be
        // its inverse: the previous version of this test asserted a ProfileUpdateConflictException,
        // because an optimistic compare-and-swap with a bounded retry could not survive this. A
        // promotion racing a burst is an operator intervening in exactly the incident that produced
        // the burst, so the moment the operation most needs to succeed was the moment it was most
        // likely to fail. The store's transactional update removes the conflict instead of retrying
        // it, and this now asserts what we actually want.
        Assert.Equal(1, version);

        // The interposed observations all landed too, the write lock is taken before the read, so
        // the burst queued behind the promotion rather than being lost to it.
        var final = store.Load(key)!;
        Assert.True(final.Observed.Attempts >= 1);
        Assert.Equal(0, final.Baseline.Version - 1);
    }

    [Fact]
    public async Task AReloadedRetrySeesTheStoredRowRatherThanItsOwnLostAttempt()
    {
        var clock = new FixedClock();
        var connections = new SqliteConnectionFactory(Path.Combine(_root, "stylomail.db"));
        var store = new StyloMail.Adaptive.Storage.SqliteAdaptiveProfileStore(connections);
        store.EnsureCreated();

        var coordinator = new ProfileCoordinator(new SqliteAdaptiveProfileStoreAdapter(store));
        var key = StyloMail.Adaptive.Profiles.ProfileScopes.OutboundSender("tenant-1", "principal-1");

        coordinator.Observe(
            key,
            new StyloMail.Adaptive.Profiles.ProfileObservation
            {
                ObservedAt = clock.GetUtcNow(),
                RecipientCount = 2,
                WasRejected = false,
            },
            clock.GetUtcNow());

        // Another writer moves the row between this coordinator's load and its save, so the first
        // save loses and the retry must reload. Against the real store the reload reconstructs from
        // the database, so the promotion is applied to the stored state rather than to a live object
        // the first attempt already mutated, which is the property the fake cannot demonstrate.
        var interloper = new ProfileCoordinator(new SqliteAdaptiveProfileStoreAdapter(store));
        interloper.Mutate(key, clock.GetUtcNow(), profile =>
        {
            profile.FreezeBaseline("concurrent writer", clock.GetUtcNow());
            return true;
        });

        var sample = new StyloMail.Adaptive.Profiles.TrustedSample
        {
            Dimensions = StyloMail.Adaptive.Profiles.DimensionVector.Create(
                StyloMail.Adaptive.Profiles.DimensionSample.Available(
                    SemanticDimensions.ConversationalContinuityId,
                    0.5)),
            Provenance = StyloMail.Adaptive.Profiles.LabelProvenance.AuthenticatedOperator,
            RecordedAt = clock.GetUtcNow(),
        };

        coordinator.Mutate(key, clock.GetUtcNow(), profile =>
        {
            profile.Promote(sample);
            return true;
        });

        var final = store.Load(key)!;

        // The observation beside the promotion survived, the promotion landed exactly once, and the
        // concurrent freeze is still in force, a whole-profile write that reloaded correctly keeps
        // everything it did not mean to change.
        Assert.Equal(1, final.Observed.Attempts);
        Assert.Equal(2, final.Observed.Recipients);
        Assert.True(final.Baseline.IsFrozen);
    }

    [Fact]
    public async Task TheProfileStoreRoundTripsWhatThePipelineObserved()
    {
        var clock = new FixedClock();
        var connections = new SqliteConnectionFactory(Path.Combine(_root, "stylomail.db"));
        var spool = new SpoolStore(Path.Combine(_root, "spool"));

        var assessor = AssessmentPipeline.Create(
            new RecordingMimeAnalyzer(new StepRecorder(), clock),
            new RecordingSemanticClassifier(clock),
            connections,
            spool,
            Builders.Options(),
            new QueueOptions { TimeProvider = clock });

        var payloadReference = await spool.WriteAsync(
            "tenant-1",
            "queue-1",
            Builders.RawMessage,
            CancellationToken.None);

        for (var i = 0; i < 3; i++)
        {
            await assessor.AssessAsync(
                Builders.Message(
                    "Please update the payment details.",
                    Builders.Envelope(
                        payloadReference: payloadReference,
                        internalMessageId: $"msg-{i}")),
                Builders.Context(clock, correlationId: $"corr-{i}"),
                CancellationToken.None);
        }

        // Observed state is durable and accumulates, which is the only reason a rate means anything:
        // three attempts across three messages is a rate, and one read-modify-write that lost two of
        // them would not be.
        var store = new StyloMail.Adaptive.Storage.SqliteAdaptiveProfileStore(connections);
        var profiles = store.LoadForTenant("tenant-1");

        // Two profiles per attempt, the sender and the sender-recipient relationship, and every
        // one of them saw all three attempts. A lost read-modify-write shows up here as a count of
        // two on a profile that was written three times.
        Assert.True(profiles.Count >= 2);
        Assert.All(profiles, profile => Assert.Equal(3, profile.Observed.Attempts));
        Assert.All(profiles, profile => Assert.Equal(3, profile.Observed.Recipients));
    }
}
