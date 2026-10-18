using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Sessions;
using StyloMail.AccessProxy.Submission;
using StyloMail.AccessProxy.Tests.Support;
using StyloMail.Embeddings;

namespace StyloMail.AccessProxy.Tests;

public sealed class RetrievalBackpressureTests
{
    [Fact]
    public async Task One_byte_chunks_hit_the_item_cap_and_do_not_clear_a_running_analyzers_buffer()
    {
        await using var pool = new ProxyRetrievalWorkPool(1024 * 1024, 1);
        var analyzer = new Analyzer(block: true);
        var sink = new Sink();
        var observer = Create(pool, analyzer, sink);
        var body = Encoding.ASCII.GetBytes("Subject: owned\r\n\r\noriginal body");
        observer.Observe(Frame(body));
        await Until(() => analyzer.Calls == 1);
        try
        {
            for (var i = 0; i < 64; i++) observer.Observe("x"u8);
            Assert.Equal(64, observer.QueuedChunks);
            Assert.False(observer.Abandoned);
            observer.Observe("x"u8);
            Assert.True(observer.Abandoned);
            Assert.Equal(64, observer.QueuedChunks);
            observer.Observe(Frame("Subject: later\r\n\r\nnever classify"u8));
            var watch = Stopwatch.StartNew();
            await observer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "Teardown awaited the noncooperative analyzer.");
            Assert.Equal(1, pool.Counters.ActiveWorkers);
            Assert.True(pool.Counters.BufferedBytes > 0);
            Assert.Equal(body, analyzer.Inputs.Single().ToArray());
            Assert.Empty(sink.Warnings);
        }
        finally { analyzer.Release.TrySetResult(); await observer.DisposeAsync(); }
        await Until(() => pool.Counters.BufferedBytes == 0 && pool.Counters.ActiveWorkers == 0);
        Assert.Equal(1, analyzer.Calls);
        Assert.Empty(sink.Warnings);
        Assert.Empty(sink.Points);
        Assert.True(analyzer.Inputs.Single().Span.IndexOfAnyExcept((byte)0) < 0);
        Assert.True(pool.Counters.DroppedChunks >= 66);
    }

    [Fact]
    public async Task Assembled_literals_share_the_global_byte_quota_across_sessions()
    {
        await using var pool = new ProxyRetrievalWorkPool(384, 4);
        var analyzer = new Analyzer(false);
        var sink = new Sink();
        var first = Create(pool, analyzer, sink);
        var second = Create(pool, analyzer, sink);
        try
        {
            first.Observe("* 1 FETCH (BODY[] {256}\r\n"u8);
            await Until(() => first.ReservedBytes == 256 && pool.Counters.ActiveWorkers == 0);
            Assert.Equal(256, pool.Counters.BufferedBytes);
            second.Observe("* 2 FETCH (BODY[] {256}\r\n"u8);
            await Until(() => second.Abandoned || second.ReservedBytes == 256);
            Assert.True(second.Abandoned, "A second assembled literal exceeded the shared copied-byte quota.");
            await Until(() => second.ReservedBytes == 0);
            Assert.Equal(256, pool.Counters.BufferedBytes);
            Assert.Equal(0, analyzer.Calls);
            Assert.Empty(sink.Warnings);
        }
        finally { await first.DisposeAsync(); await second.DisposeAsync(); }
        await Until(() => pool.Counters.BufferedBytes == 0);
    }

    [Fact]
    public async Task Session_byte_loss_mid_literal_permanently_disables_later_framing()
    {
        await using var pool = new ProxyRetrievalWorkPool(4096, 1);
        var analyzer = new Analyzer(false);
        var sink = new Sink();
        var options = new ProxyRetrievalWarningAssemblerOptions { MaximumMessageBytes = 128, MaximumSessionBufferedBytes = 256 };
        var observer = Create(pool, analyzer, sink, options: options);
        observer.Observe("* 1 FETCH (BODY[] {128}\r\npartial"u8);
        await Until(() => observer.ReservedBytes == 128 && pool.Counters.ActiveWorkers == 0);
        observer.Observe(new byte[512]);
        Assert.True(observer.Abandoned);
        observer.Observe(Frame("Subject: valid later\r\n\r\nbody"u8));
        await observer.DisposeAsync();
        Assert.Equal(0, analyzer.Calls);
        Assert.Empty(sink.Warnings);
        Assert.Empty(sink.Points);
        Assert.True(pool.Counters.DroppedChunks >= 2);
        Assert.Equal(0, pool.Counters.BufferedBytes);
        // A fresh stream is the positive control; only the damaged connection stays disabled.
        await using var positive = Create(pool, analyzer, sink, options: options);
        positive.Observe(Frame("Subject: fresh\r\n\r\nbody"u8));
        await positive.DisposeAsync();
        Assert.Equal(1, analyzer.Calls);
        Assert.Single(sink.Warnings);
    }

    [Fact]
    public async Task Four_stuck_workers_expire_queued_copies_then_refuse_until_a_worker_frees()
    {
        await using var pool = new ProxyRetrievalWorkPool(4 * 1024 * 1024, 4, queueWait: TimeSpan.FromMilliseconds(500));
        var analyzer = new Analyzer(true);
        var sink = new Sink();
        List<ProxyRetrievalWarningAssembler> observers = [];
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var observer = Create(pool, analyzer, sink); observers.Add(observer);
                observer.Observe(Frame("Subject: gate\r\n\r\nbody"u8));
            }
            await Until(() => analyzer.Calls == 4);
            foreach (var captured in observers)
                for (var i = 0; i < 3; i++) captured.Observe(new byte[256 * 1024]);
            var retained = pool.Counters.BufferedBytes;
            Assert.InRange(retained, 3L * 1024 * 1024, 4L * 1024 * 1024);
            var queuedFrame = Frame("Subject: queued\r\n\r\nnever analyze after expiry"u8);
            for (var i = 0; i < 64; i++)
            {
                var observer = Create(pool, analyzer, sink, new Classifier(false)); observers.Add(observer); observer.Observe(queuedFrame);
                Assert.False(observer.Abandoned);
            }
            Assert.Equal(64, pool.Counters.ReadySessions);
            Assert.Equal(retained + 64 * queuedFrame.Length, pool.Counters.BufferedBytes);
            await Until(() => observers.Skip(4).All(observer => observer.Abandoned && observer.ReservedBytes == 0));
            Assert.Equal(0, pool.Counters.ReadySessions);
            Assert.Equal(retained, pool.Counters.BufferedBytes);
            Assert.True(pool.Counters.Saturated);
            var refused = Create(pool, analyzer, sink); observers.Add(refused); refused.Observe("x"u8);
            Assert.True(refused.Abandoned);
            Assert.Equal(0, refused.ReservedBytes);
            Assert.Equal(4, analyzer.Calls);
            Assert.Equal(4, analyzer.MaxActive);
            Assert.Equal(4, pool.Counters.ActiveWorkers);
            await Task.WhenAll(observers.Select(observer => observer.DisposeAsync().AsTask())).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(4, analyzer.Calls);
            Assert.Equal(4, pool.Counters.ActiveWorkers);
        }
        finally
        {
            analyzer.Release.TrySetResult();
            await Task.WhenAll(observers.Select(observer => observer.DisposeAsync().AsTask()));
        }
        await Until(() => pool.Counters.BufferedBytes == 0 && pool.Counters.ActiveWorkers == 0 && pool.Counters.ReadySessions == 0);
        Assert.Empty(sink.Warnings);
        Assert.Empty(sink.Points);
        Assert.False(pool.Counters.Saturated);
        // Once a worker frees, the same pool must produce real points again, not just refuse less.
        var positiveSink = new Sink();
        await using var resumed = Create(pool, new Analyzer(false), positiveSink, new Classifier(false));
        resumed.Observe(Frame("Subject: resumed\r\n\r\nbody"u8));
        await resumed.DisposeAsync();
        Assert.Single(positiveSink.Warnings);
        Assert.NotEmpty(positiveSink.Points);
    }

    [Fact]
    public async Task Healthy_bursts_larger_than_four_are_queued_and_analyzed_as_workers_free()
    {
        await using var pool = new ProxyRetrievalWorkPool(1024 * 1024, 4);
        var analyzer = new Analyzer(true);
        var sink = new Sink();
        List<ProxyRetrievalWarningAssembler> observers = [];
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var observer = Create(pool, analyzer, sink); observers.Add(observer);
                observer.Observe(Frame("Subject: running\r\n\r\nbody"u8));
            }
            await Until(() => analyzer.Calls == 4);
            for (var i = 0; i < 8; i++)
            {
                var observer = Create(pool, analyzer, sink); observers.Add(observer);
                observer.Observe(Frame("Subject: queued\r\n\r\nbody"u8));
                Assert.False(observer.Abandoned, "A healthy burst beyond four must retain its analysis copy.");
            }
            Assert.Equal(8, pool.Counters.ReadySessions);
            Assert.False(pool.Counters.Saturated);
            analyzer.Release.TrySetResult();
            await Until(() => analyzer.Calls == 12 && sink.Warnings.Count == 12);
            Assert.InRange(analyzer.MaxActive, 1, 4);
            Assert.Equal(0, pool.Counters.DroppedChunks);
        }
        finally
        {
            analyzer.Release.TrySetResult();
            await Task.WhenAll(observers.Select(observer => observer.DisposeAsync().AsTask())).WaitAsync(TimeSpan.FromSeconds(3));
        }
        await Until(() => pool.Counters.BufferedBytes == 0 && pool.Counters.ActiveWorkers == 0);
    }

    [Fact]
    public async Task An_element_classifier_returning_after_teardown_cannot_emit_a_late_point()
    {
        await using var pool = new ProxyRetrievalWorkPool(1024 * 1024, 1);
        var analyzer = new Analyzer(false);
        var classifier = new Classifier(false);
        var positiveSink = new Sink();
        await using (var positive = Create(pool, analyzer, positiveSink, classifier))
        {
            positive.Observe(Frame("Subject: control\r\n\r\nbody"u8));
            await positive.DisposeAsync();
        }
        Assert.NotEmpty(positiveSink.Points);
        var lateSink = new Sink();
        var gated = new Classifier(true);
        var observer = Create(pool, analyzer, lateSink, gated);
        observer.Observe(Frame("Subject: late\r\n\r\nbody"u8));
        await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await observer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(pool.Counters.BufferedBytes > 0);
            Assert.Empty(lateSink.Points);
        }
        finally { gated.Release.TrySetResult(); await observer.DisposeAsync(); }
        await Until(() => pool.Counters.BufferedBytes == 0 && pool.Counters.ActiveWorkers == 0);
        Assert.Empty(lateSink.Points);
        Assert.Empty(lateSink.Segmentation);
    }

    [Fact]
    public async Task Real_tcp_imap_relay_preserves_all_bytes_and_releases_its_slot_while_analysis_is_stuck()
    {
        await using var pool = new ProxyRetrievalWorkPool(16384, 1);
        var harness = new ProxyHarness(new AccessProxyBounds { MaxConcurrentSessions = 1, MaxSessionsPerAccount = 1 });
        await harness.EnrolAppPasswordAccountAsync();
        var analyzer = new Analyzer(true);
        var sink = new Sink();
        var factory = new ProxyRetrievalWarningAssemblerFactory(sink, analyzer, TimeProvider.System,
            new ProxyRetrievalWarningAssemblerOptions { MaximumMessageBytes = 256, MaximumSessionBufferedBytes = 1024 }, null, pool);
        var done = new TaskCompletionSource<SessionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listener = new TcpAccessProxyListener(new IPEndPoint(IPAddress.Loopback, 0), ProxyHarness.TenantId,
            async (_, channel, token) =>
            {
                var result = await harness.NewImapSession(retrievalAssemblerFactory: factory).RunAsync(channel, token);
                done.TrySetResult(result); return result;
            }, harness.Limiter);
        await listener.StartAsync(CancellationToken.None);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);
        using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
        using var writer = new StreamWriter(client.GetStream(), Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Assert.StartsWith("* OK", await reader.ReadLineAsync(deadline.Token));
        await writer.WriteLineAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}");
        Assert.StartsWith("a1 OK", await reader.ReadLineAsync(deadline.Token));
        try
        {
            var first = Frame("Subject: first\r\n\r\nbody"u8);
            await harness.Transport.Channel!.SendRawAsync(first);
            var received = new byte[first.Length];
            await client.GetStream().ReadExactlyAsync(received, deadline.Token);
            Assert.Equal(first, received);
            await Until(() => analyzer.Calls == 1);
            var damaged = Encoding.ASCII.GetBytes("* 2 FETCH (BODY[] {100}\r\npartial" + new string('x', 6000));
            var later = Frame("Subject: later valid\r\n\r\nbody"u8);
            var expected = damaged.Concat(later).ToArray();
            await harness.Transport.Channel.SendRawAsync(expected);
            received = new byte[expected.Length];
            await client.GetStream().ReadExactlyAsync(received, deadline.Token);
            Assert.Equal(expected, received);
            await Until(() => pool.Counters.AbandonedStreams == 1);
            client.Client.Shutdown(SocketShutdown.Send);
            Assert.Equal(SessionOutcome.Relayed, await done.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            await Until(() => harness.Limiter.ActiveSessions == 0);
            Assert.Equal(1, pool.Counters.ActiveWorkers);
            Assert.True(pool.Counters.BufferedBytes > 0);
            Assert.Empty(sink.Warnings);
        }
        finally { analyzer.Release.TrySetResult(); }
        await Until(() => pool.Counters.BufferedBytes == 0 && pool.Counters.ActiveWorkers == 0);
        Assert.Equal(1, analyzer.Calls);
        Assert.Empty(sink.Warnings);
        Assert.Empty(sink.Points);
    }

    private static ProxyRetrievalWarningAssembler Create(ProxyRetrievalWorkPool pool, Analyzer analyzer, Sink sink,
        Classifier? classifier = null, ProxyRetrievalWarningAssemblerOptions? options = null)
        => (ProxyRetrievalWarningAssembler)new ProxyRetrievalWarningAssemblerFactory(sink, analyzer, TimeProvider.System,
            options, classifier is null ? null : new ProxyElementOrchestrator(classifier, sink), pool).Create("tenant", "account");
    private static byte[] Frame(ReadOnlySpan<byte> body) => Encoding.ASCII.GetBytes($"* 1 FETCH (BODY[] {{{body.Length}}}\r\n")
        .Concat(body.ToArray()).Concat("\r\n)\r\n"u8.ToArray()).ToArray();
    private static async Task Until(Func<bool> condition)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(5, stop.Token);
    }
    private sealed class Analyzer(bool block) : IProxyWarningAnalyzer
    {
        private int _calls, _active, _max;
        public int Calls => Volatile.Read(ref _calls);
        public int MaxActive => Volatile.Read(ref _max);
        public ConcurrentQueue<ReadOnlyMemory<byte>> Inputs { get; } = new();
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ProxyWarningAnalysisResult> AnalyzeAsync(ProxyWarningAnalysisInput input, CancellationToken cancellationToken)
        {
            Inputs.Enqueue(input.Message);
            Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            int old;
            do { old = Volatile.Read(ref _max); if (active <= old) break; }
            while (Interlocked.CompareExchange(ref _max, active, old) != old);
            try { if (block) await Release.Task; }
            finally { Interlocked.Decrement(ref _active); }
            return new ProxyWarningAnalysisResult
            {
                MessageToken = "test-message", ThreadToken = "test-thread", Availability = ProxyWarningAvailability.Available,
                AvailabilityReason = ProxyWarningAvailabilityReason.None, Findings = [],
            };
        }
    }
    private sealed class Sink : IProxyWarningEventSink, IProxyElementSink
    {
        public ConcurrentQueue<ProxyWarningAnalysisOutcome> Warnings { get; } = new();
        public ConcurrentQueue<ProxyElementPoint> Points { get; } = new();
        public ConcurrentQueue<ProxySegmentationOutcome> Segmentation { get; } = new();
        public ValueTask RecordOutcomeAsync(ProxyWarningAnalysisOutcome outcome, CancellationToken ct) { Warnings.Enqueue(outcome); return ValueTask.CompletedTask; }
        public ValueTask RecordMarkerAsync(ProxyDeterministicWarningEvent marker, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RecordOutcomeAsync(ProxySegmentationOutcome outcome, CancellationToken ct) { Segmentation.Enqueue(outcome); return ValueTask.CompletedTask; }
        public ValueTask RecordPointAsync(ProxyElementPoint point, CancellationToken ct) { Points.Enqueue(point); return ValueTask.CompletedTask; }
    }
    private sealed class Classifier(bool block) : IElementClassifier
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ClassificationOutcome> ClassifyAsync(IReadOnlyList<string> segments, IReadOnlyList<string>? scorers = null, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(); if (block) await Release.Task;
            var results = ElementScorers.All.Select(scorer => new ElementScorerResult(scorer, ElementScorers.VersionOf(scorer)!,
                segments.Select((_, ordinal) => new SegmentElementScores(ordinal,
                    ElementVocabulary.All.Select(element => new ElementScore(element, ElementAvailability.Available, 0.5, null, ElementNotProducedReason.None)).ToArray())).ToArray())).ToArray();
            return new ClassificationOutcome.Success(new ElementClassification(new EmbeddingMetadata("test", null, 3, 3, "test", "float32", "test"), ElementVocabulary.Version, results));
        }
    }
}
