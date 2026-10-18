using System.Collections.Concurrent;
using System.Threading.Channels;
using StyloMail.Assessment.Semantic;
using StyloMail.Core;

namespace StyloMail.Assessment.Tests;

public sealed class SingleFlightCancellationTests
{
    [Fact]
    public async Task Repeated_deadlines_bound_executions_and_late_generations_cannot_publish_or_evict()
    {
        var inner = new GatedClassifier { IgnoreCancellation = true };
        var clock = new ManualClock();
        var store = new InMemorySemanticCacheStore();
        var options = new SemanticCacheOptions { ClassifierModelVersion = "jev-1.13.0", FlightMaximumDuration = TimeSpan.FromSeconds(2) };
        var diagnostics = new ConcurrentQueue<string>();
        var classifier = new SemanticCacheClassifier(inner, store, options, clock, diagnostics.Enqueue);
        var input = Input("stalled original key");
        var a = classifier.ClassifyAsync(input, CancellationToken.None).AsTask();
        var first = await inner.Next();
        Task<SemanticAssessment>? b = null, other = null, replacement = null, joined = null;
        try
        {
            Assert.Equal(options.FlightMaximumDuration, clock.NextDue);
            clock.ExpireNext();
            AssertUnavailable(await a);
            await Until(() => first.Token.IsCancellationRequested);
            b = classifier.ClassifyAsync(input, CancellationToken.None).AsTask();
            var second = await inner.Next();
            Assert.Equal(2, inner.Calls);
            Assert.Equal(2, inner.Active);
            Assert.Equal(2, inner.MaxActive);
            clock.ExpireNext();
            AssertUnavailable(await b);
            await Until(() => second.Token.IsCancellationRequested);
            for (var i = 0; i < 3; i++) AssertUnavailable(await classifier.ClassifyAsync(input, CancellationToken.None));
            Assert.Equal(2, inner.Calls); // Tombstones never start a third execution for this key.
            Assert.Equal(3, classifier.TombstoneRefusals);
            Assert.Equal(0, store.Count);
            other = classifier.ClassifyAsync(Input("independent while original tombstoned"), CancellationToken.None).AsTask();
            var independent = await inner.Next();
            Assert.Equal(3, inner.Calls);
            independent.Release.TrySetResult();
            await other;
            Assert.Equal(1, store.Count); // Only the independent key published.
            first.Release.TrySetResult();
            await Until(() => classifier.DetachedCompletions == 1);
            Assert.Equal(1, store.Count);
            replacement = classifier.ClassifyAsync(input, CancellationToken.None).AsTask();
            var current = await inner.Next();
            Assert.Equal(4, inner.Calls);
            Assert.Equal(2, inner.Active); // One expired execution plus one current for this key.
            second.Release.TrySetResult();
            await Until(() => classifier.DetachedCompletions == 2);
            Assert.Equal(1, store.Count);
            joined = classifier.ClassifyAsync(input, CancellationToken.None).AsTask();
            Assert.Equal(4, inner.Calls); // Old completion did not evict current generation.
            current.Release.TrySetResult();
            await replacement.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(ReferenceEquals(joined, await Task.WhenAny(joined, Task.Delay(300))),
                "A detached completion evicted the current generation instead of preserving its joiners.");
            var results = await Task.WhenAll(replacement, joined);
            Assert.All(results.SelectMany(result => result.Evidence), row => Assert.Equal(0.4, row.Value));
            Assert.Equal(2, store.Count);
            var hit = await classifier.ClassifyAsync(input, CancellationToken.None);
            Assert.True(hit.Cache.Hit);
            Assert.All(hit.Evidence, row => Assert.Equal(0.4, row.Value));
            Assert.Equal(4, inner.Calls);
            Assert.Equal(2, classifier.ExpiredFlights);
            await Until(() => diagnostics.Count(kind => kind == "detached_settled") == 2);
            Assert.Equal(2, diagnostics.Count(kind => kind == "deadline_expired"));
            Assert.Single(diagnostics, kind => kind == "tombstone_refused");
        }
        finally { inner.ReleaseAll(); await Drain(a, b, other, replacement, joined); }
    }

    [Fact]
    public async Task Real_deadline_returns_unavailable_and_does_not_serve_opted_in_stale_evidence()
    {
        var inner = new GatedClassifier { IgnoreCancellation = true };
        var store = new InMemorySemanticCacheStore();
        var options = new SemanticCacheOptions
        {
            ClassifierModelVersion = "jev-1.13.0", FlightMaximumDuration = TimeSpan.FromMilliseconds(500),
            EntryLifetime = TimeSpan.FromMilliseconds(1), ServeStaleOnProviderUnavailable = true,
        };
        var classifier = new SemanticCacheClassifier(inner, store, options);
        var input = Input("real bounded deadline");
        var prime = classifier.ClassifyAsync(input, CancellationToken.None).AsTask();
        var operation = await inner.Next();
        operation.Release.TrySetResult();
        await prime;
        // Explicit contextual cache clock makes the cached entry expired without sleeping. The
        // shared flight still uses its separate real timer and must not launder expiry as stale.
        var later = new FixedClock();
        later.Advance(TimeSpan.FromDays(100));
        var timed = classifier.ClassifyAsync(input, later, CancellationToken.None).AsTask();
        await inner.Next();
        try
        {
            AssertUnavailable(await timed.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(2, inner.Calls);
            Assert.Equal(1, inner.Active);
            inner.ReleaseAll();
            await Until(() => classifier.DetachedCompletions == 1);
            AssertUnavailable(await timed);
        }
        finally { inner.ReleaseAll(); await Drain(prime, timed); }
    }

    [Fact]
    public void Library_lifetime_is_finite_overridable_and_rejects_nonpositive_or_infinite_values()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), new SemanticCacheOptions { ClassifierModelVersion = "test" }.FlightMaximumDuration);
        foreach (var invalid in new[] { TimeSpan.Zero, Timeout.InfiniteTimeSpan, TimeSpan.MaxValue })
            Assert.Throws<ArgumentOutOfRangeException>(() => new SemanticCacheOptions { ClassifierModelVersion = "test", FlightMaximumDuration = invalid }.Validate());
    }

    private static void AssertUnavailable(SemanticAssessment result)
    {
        Assert.False(result.Cache.Hit);
        Assert.False(result.Cache.Stale);
        Assert.NotEmpty(result.Evidence);
        Assert.All(result.Evidence, row =>
        {
            Assert.Equal(EvidenceAvailability.Unavailable, row.Availability);
            Assert.Null(row.Value);
            Assert.Equal("TimedOut", Assert.Single(row.Attributes!, attribute => attribute.Name == "reason").Value);
        });
    }
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Canceling_one_waiter_does_not_cancel_the_other_or_start_another_provider(bool cancelLeader)
    {
        var inner = new GatedClassifier();
        var classifier = Create(inner);
        var input = Input("one message");
        using var leaderToken = new CancellationTokenSource();
        using var followerToken = new CancellationTokenSource();
        var leader = classifier.ClassifyAsync(input, leaderToken.Token).AsTask();
        var operation = await inner.Next();
        var follower = classifier.ClassifyAsync(input, followerToken.Token).AsTask();
        var canceled = cancelLeader ? leader : follower;
        var survivor = cancelLeader ? follower : leader;
        Task<SemanticAssessment>? late = null;
        try
        {
            (cancelLeader ? leaderToken : followerToken).Cancel();
            await AssertCanceledPromptly(canceled);
            Assert.False(operation.Token.IsCancellationRequested);
            Assert.False(survivor.IsCompleted);
            late = classifier.ClassifyAsync(input, CancellationToken.None).AsTask();
            Assert.Equal(1, inner.Calls);
            Assert.Equal(1, inner.Active);
            operation.Release.TrySetResult();
            Assert.All((await Task.WhenAll(survivor, late)).SelectMany(result => result.Evidence),
                row => Assert.Equal(EvidenceAvailability.Available, row.Availability));
            Assert.Equal(1, inner.Calls);
            Assert.Equal(1, inner.MaxActive);
        }
        finally { inner.ReleaseAll(); await Drain(leader, follower, late); }
    }

    [Fact]
    public async Task All_waiters_can_cancel_and_a_late_waiter_joins_the_continuing_provider()
    {
        var inner = new GatedClassifier();
        var classifier = Create(inner);
        var input = Input("all cancel");
        using var aToken = new CancellationTokenSource();
        using var bToken = new CancellationTokenSource();
        var a = classifier.ClassifyAsync(input, aToken.Token).AsTask();
        var operation = await inner.Next();
        var b = classifier.ClassifyAsync(input, bToken.Token).AsTask();
        Task<SemanticAssessment>? c = null;
        try
        {
            aToken.Cancel(); bToken.Cancel();
            await AssertCanceledPromptly(a); await AssertCanceledPromptly(b);
            Assert.False(operation.Token.IsCancellationRequested);
            Assert.Equal(1, inner.Active);
            c = classifier.ClassifyAsync(input, CancellationToken.None).AsTask();
            Assert.Equal(1, inner.Calls);
            operation.Release.TrySetResult();
            Assert.All((await c).Evidence, row => Assert.Equal(EvidenceAvailability.Available, row.Availability));
            Assert.Equal(1, inner.MaxActive);
        }
        finally { inner.ReleaseAll(); await Drain(a, b, c); }
    }

    [Fact]
    public async Task A_precanceled_caller_never_starts_the_provider()
    {
        var inner = new GatedClassifier();
        var classifier = Create(inner);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var call = classifier.ClassifyAsync(Input("pre canceled"), cancellation.Token).AsTask();
        try
        {
            await AssertCanceledPromptly(call);
            Assert.Equal(0, inner.Calls);
        }
        finally { inner.ReleaseAll(); await Drain(call); }
    }

    [Fact]
    public async Task Different_keys_run_independently_while_the_first_provider_is_gated()
    {
        var inner = new GatedClassifier();
        var classifier = Create(inner);
        var a = classifier.ClassifyAsync(Input("first independent message"), CancellationToken.None).AsTask();
        await inner.Next();
        var b = classifier.ClassifyAsync(Input("second independent message"), CancellationToken.None).AsTask();
        try
        {
            await inner.Next();
            Assert.Equal(2, inner.Calls);
            Assert.Equal(2, inner.Active);
            Assert.Equal(2, inner.MaxActive);
        }
        finally { inner.ReleaseAll(); await Drain(a, b); }
    }

    private static SemanticCacheClassifier Create(GatedClassifier inner) => new(inner, new InMemorySemanticCacheStore(),
        new SemanticCacheOptions { ClassifierModelVersion = "jev-1.13.0" });
    private static SemanticMailInput Input(string body) => new() { Message = Builders.Message(body), Dimensions = SemanticDimensions.All };
    private static async Task AssertCanceledPromptly(Task task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(300))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }
    private static async Task Drain(params Task?[] tasks)
    {
        foreach (var task in tasks)
        {
            if (task is null) continue;
            try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class Operation(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class GatedClassifier : ISemanticMailClassifier
    {
        public bool IgnoreCancellation { get; init; }
        private readonly Channel<Operation> _started = Channel.CreateUnbounded<Operation>();
        private readonly ConcurrentQueue<Operation> _operations = new();
        private int _calls, _active, _maxActive, _releaseAll;
        public int Calls => Volatile.Read(ref _calls);
        public int Active => Volatile.Read(ref _active);
        public int MaxActive => Volatile.Read(ref _maxActive);
        public async Task<Operation> Next() => await _started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        public void ReleaseAll()
        {
            Interlocked.Exchange(ref _releaseAll, 1);
            foreach (var operation in _operations) operation.Release.TrySetResult();
        }
        public async ValueTask<SemanticAssessment> ClassifyAsync(SemanticMailInput input, CancellationToken cancellationToken)
        {
            var invocation = Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            int previous;
            do { previous = Volatile.Read(ref _maxActive); if (active <= previous) break; }
            while (Interlocked.CompareExchange(ref _maxActive, active, previous) != previous);
            var operation = new Operation(cancellationToken);
            _operations.Enqueue(operation);
            if (Volatile.Read(ref _releaseAll) != 0) operation.Release.TrySetResult();
            _started.Writer.TryWrite(operation);
            try
            {
                if (IgnoreCancellation) await operation.Release.Task;
                else await operation.Release.Task.WaitAsync(cancellationToken);
                return new SemanticAssessment
                {
                    ResolvedModelVersion = "jev-1.13.0", Cache = new CacheProvenance { Hit = false, KeyDigest = "provider", Stale = false },
                    Evidence = input.Dimensions.Select(dimension => new Evidence
                    {
                        SignalId = dimension.Id, Origin = EvidenceOrigin.Semantic, Availability = EvidenceAvailability.Available,
                        Value = invocation / 10d, SourceVersion = "jev-1.13.0", ObservedAt = DateTimeOffset.UtcNow,
                    }).ToArray(),
                };
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly ConcurrentQueue<ManualTimer> _timers = new();
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        public TimeSpan NextDue => _timers.First(timer => !timer.Disposed).Due;
        public void ExpireNext()
        {
            while (_timers.TryDequeue(out var timer))
                if (!timer.Disposed) { timer.Fire(); return; }
            throw new InvalidOperationException("No live deadline timer.");
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            _timers.Enqueue(timer);
            return timer;
        }
        private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            private int _disposed;
            public TimeSpan Due { get; } = due;
            public bool Disposed => Volatile.Read(ref _disposed) != 0;
            public void Fire() { if (!Disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
