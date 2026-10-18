using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Submission;
using StyloMail.AccessProxy.Threading;
using StyloMail.Core;
using StyloMail.Embeddings;
using StyloMail.Host.Proxy;
using StyloMail.Mime;

// No await in the measurement driver: every product ValueTask must complete synchronously.
// A changed thread or asynchronous completion aborts without publishing allocation figures.
if (args.Length != 1) throw new ArgumentException("Supply a new artifact directory.");
var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) throw new IOException("Artifact directory already exists.");
Directory.CreateDirectory(output);
var thread = Environment.CurrentManagedThreadId;
const int size = 1024 * 1024;
var readBuffer = new byte[size];
var mime = new BoundedMimeMessageAnalyzer();
using var tokens = new HmacProxyThreadTokenDeriver(new EphemeralSecrets());
var clock = new FixedClock();
var threading = new ThreadingHeaderExtractor(tokens);
var warning = new HostProxyWarningAnalyzer(mime, threading, clock);
var disabledWarning = new HostProxyWarningAnalyzer(null, threading, clock);
var classifier = new FixtureClassifier(thread, false);
var disabledClassifier = new FixtureClassifier(thread, true);
var sink = new CountingSink(thread);
var disabledSink = new CountingSink(thread);
var elements = new ProxyElementOrchestrator(classifier, sink);
var disabledElements = new ProxyElementOrchestrator(disabledClassifier, disabledSink);
var stager = new InMemoryEphemeralSubmissionStager();
var results = new List<object>();
var inputs = new List<object>();
var setupResults = new List<object>();
foreach (var shape in new[] { "plain", "html", "quoted-history" })
{
    var setupStart = Stopwatch.GetTimestamp();
    var setupAllocated = GC.GetAllocatedBytesForCurrentThread();
    var message = Message(shape);
    if (message.Length != size) throw new InvalidOperationException("Input size control failed.");
    var identity = new ProxyWarningAnalysisInput { Message = message, TenantId = "fixture-tenant", AccountId = "fixture-account", ObservedAt = clock.GetUtcNow() };
    var parsed = mime.Analyze(new MimeAnalysisRequest
    {
        RawMessage = message, TimeProvider = clock,
        Envelope = new MailEnvelope { InternalMessageId = "fixture-message", TenantId = identity.TenantId,
            TrustedPrincipalId = identity.AccountId, Direction = MailDirection.Outbound, MailFrom = string.Empty,
            RcptTo = [], ReceivedAt = identity.ObservedAt, MimeDigest = string.Empty, PayloadReference = string.Empty },
    });
    if (!parsed.IsAnalysable || parsed.Message is null || !parsed.Coverage.BodyParsed)
        throw new InvalidOperationException("Input MIME positive control failed.");
    var newBodyCharacters = parsed.Message.BodyText.Length;
    if (shape == "quoted-history" && (newBodyCharacters > 1000 || !parsed.Message.BodyText.Contains("Current request", StringComparison.Ordinal)))
        throw new InvalidOperationException("Quoted-history new-body control failed.");
    inputs.Add(new { shape, bytes = message.Length, sha256 = Convert.ToHexString(SHA256.HashData(message)),
        newBodyCharacters, coverage = parsed.Coverage });
    setupResults.Add(new { shape, elapsedMilliseconds = Stopwatch.GetElapsedTime(setupStart).TotalMilliseconds,
        allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - setupAllocated });
    ProxyWarningAnalysisResult? lastWarning = null;
    Action stage = () =>
    {
        var handle = Sync.Result(stager.CreateAsync(25 * size, CancellationToken.None));
        try
        {
            for (var offset = 0; offset < message.Length; offset += 16 * 1024)
                Sync.Complete(handle.Content.WriteAsync(message.AsMemory(offset, Math.Min(16 * 1024, message.Length - offset))));
            handle.Content.Position = 0;
            var received = 0;
            while (received < readBuffer.Length)
            {
                var count = Sync.Result(handle.Content.ReadAsync(readBuffer.AsMemory(received)));
                if (count == 0) throw new EndOfStreamException();
                received += count;
            }
            if (handle.Length != size) throw new InvalidOperationException("Stage length changed.");
        }
        finally { Sync.Complete(handle.DisposeAsync()); }
    };
    stage();
    if (!message.AsSpan().SequenceEqual(readBuffer) || stager.ActiveCount != 0 || InMemoryEphemeralSubmissionStager.Counters.ActiveStagers != 0)
        throw new InvalidOperationException("Stage exact-byte/ownership control failed.");
    results.Add(Measure(shape, "A-stage-25MiB", stage, () => new { bytesRoundTripped = size, stager.ActiveCount }, thread));
    if (!message.AsSpan().SequenceEqual(readBuffer) || stager.ActiveCount != 0) throw new InvalidOperationException("Stage post-measure control failed.");
    results.Add(Measure(shape, "B-warning-mime", () => lastWarning = Sync.Result(warning.AnalyzeAsync(identity, CancellationToken.None)),
        () => new { lastWarning!.Availability, lastWarning.AvailabilityReason, findings = lastWarning.Findings.Count }, thread));
    if (lastWarning!.Availability != ProxyWarningAvailability.Available) throw new InvalidOperationException("Warning positive control failed.");
    results.Add(Measure(shape, "B-disabled-mime", () => lastWarning = Sync.Result(disabledWarning.AnalyzeAsync(identity, CancellationToken.None)),
        () => new { lastWarning!.Availability, lastWarning.AvailabilityReason, findings = lastWarning.Findings.Count }, thread));
    if (lastWarning!.AvailabilityReason != ProxyWarningAvailabilityReason.NoAnalyserConfigured) throw new InvalidOperationException("Disabled warning control failed.");
    results.Add(Measure(shape, "C-elements-mime", () => Sync.Complete(elements.EmitAsync(identity, message, "fixture-message", "fixture-thread", MessageTrafficKind.Submission, CancellationToken.None)), sink.Snapshot, thread));
    if (sink.Segments == 0 || sink.Points != sink.Segments * ElementScorers.All.Count * ElementVocabulary.All.Count || sink.Availability != ProxyWarningAvailability.Available)
        throw new InvalidOperationException("Element positive control failed.");
    if (shape == "quoted-history"
        ? sink.Segments != 1 || sink.LastOutcome!.Truncated || sink.LastOutcome.AnySegmentDropped
        : sink.Segments != 32 || !sink.LastOutcome!.Truncated || !sink.LastOutcome.AnySegmentDropped)
        throw new InvalidOperationException("New-body segmentation/truncation control failed.");
    results.Add(Measure(shape, "C-disabled-classifier", () => Sync.Complete(disabledElements.EmitAsync(identity, message, "fixture-message", "fixture-thread", MessageTrafficKind.Submission, CancellationToken.None)), disabledSink.Snapshot, thread));
    if (disabledSink.Points != 0 || disabledSink.Reason != ProxySegmentationAvailabilityReason.NoAnalyserConfigured)
        throw new InvalidOperationException("Disabled element control failed.");
    // Measure the same preallocated task selection/callback bookkeeping without product parsing.
    var fixtureSegments = Enumerable.Repeat("preallocated", sink.Segments).ToArray();
    var fixtureOutcome = sink.LastOutcome!;
    results.Add(Measure(shape, "fixture-callback-control", () =>
    {
        var task = classifier.ClassifyAsync(fixtureSegments);
        if (!task.IsCompletedSuccessfully) throw new InvalidOperationException("Fixture became asynchronous.");
        _ = task.GetAwaiter().GetResult();
        Sync.Complete(sink.RecordBatchAsync(fixtureOutcome, Array.Empty<ProxyElementPoint>(), CancellationToken.None));
    }, () => new { preallocated = true }, thread));
    results.Add(Measure(shape, "noop-harness", static () => { }, () => new { productCalls = 0 }, thread));
}
var report = new
{
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    inputBytes = size, warmupPerArm = 10, samplesPerArm = 100, thread,
    allocationMethod = "GC.GetAllocatedBytesForCurrentThread; all product ValueTasks completed synchronously; caller/classifier/sink thread IDs checked for every call. No task scheduling in fixture.",
    scope = "SMTP full-message component costs only. A includes fixed 25MiB allocation, page faults, fragmented write, rewind/read and clearing disposal. B includes real HMAC/header/MIME warning analysis; fixture key generated once before measurement. C includes independent real MIME parsing/segmentation/point creation with preallocated deterministic classifier results and counting sink. No network, provider/model, SQLite, queue or end-to-end cost. Disabled arms price no-MIME/no-classifier named absence seams, not worker scheduling. No numerical pass budget. Values include scalar fixture callback checks; separate fixture control is reported without subtraction.",
    setup = new { scope = "Input generation, hashing and one unmeasured MIME positive-control parse, before arm measurements. Global analyzer/classifier construction and read-buffer allocation are outside these figures and all measured arms.", results = setupResults },
    inputs, results,
};
File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
Console.WriteLine($"Completed {results.Count} arms; synchronous thread {thread}; report={output}/report.json");

static object Measure(string shape, string arm, Action action, Func<object> observation, int thread)
{
    for (var i = 0; i < 10; i++) action();
    var elapsed = new double[100];
    var allocated = new long[100];
    for (var i = 0; i < 100; i++)
    {
        if (Environment.CurrentManagedThreadId != thread) throw new InvalidOperationException("Caller thread changed.");
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        action();
        elapsed[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        allocated[i] = GC.GetAllocatedBytesForCurrentThread() - bytes;
        if (Environment.CurrentManagedThreadId != thread) throw new InvalidOperationException("Caller thread changed.");
    }
    var observed = observation();
    return new { shape, arm, medianMilliseconds = Percentile(elapsed, 0.5), p95Milliseconds = Percentile(elapsed, 0.95),
        medianAllocatedBytes = Percentile(allocated.Select(value => (double)value).ToArray(), 0.5),
        p95AllocatedBytes = Percentile(allocated.Select(value => (double)value).ToArray(), 0.95), observation = observed,
        elapsedMilliseconds = elapsed, allocatedBytes = allocated };
}
static double Percentile(double[] values, double fraction)
{
    var sorted = values.Order().ToArray();
    return sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];
}
static byte[] Message(string shape)
{
    var html = shape == "html";
    var header = "From: fixture@example.invalid\r\nTo: recipient@example.invalid\r\nDate: Sun, 18 Oct 2026 12:00:00 +0000\r\nMessage-ID: <fixture@example.invalid>\r\nMIME-Version: 1.0\r\nContent-Type: text/" + (html ? "html" : "plain") + "; charset=utf-8\r\nContent-Transfer-Encoding: 7bit\r\n\r\n";
    var prefix = html ? "<html><body><p>" : shape == "quoted-history" ? "Current request: review this fixture.\r\n\r\nOn Sun, 18 Oct 2026, Earlier Writer wrote:\r\n" : string.Empty;
    var suffix = html ? "</p></body></html>\r\n" : "\r\n";
    var repeat = shape == "quoted-history" ? "> Older fixture history must not become current body.\r\n" : "Current fixture text with ordinary words for bounded analysis.\r\n";
    var remaining = 1024 * 1024 - header.Length - prefix.Length - suffix.Length;
    var builder = new StringBuilder(1024 * 1024);
    builder.Append(header).Append(prefix);
    while (remaining >= repeat.Length) { builder.Append(repeat); remaining -= repeat.Length; }
    // Keep all generated 7bit body lines short and avoid a partial CRLF at the size boundary.
    if (remaining > 0) builder.Append('x', remaining);
    builder.Append(suffix);
    return Encoding.ASCII.GetBytes(builder.ToString());
}
static class Sync
{
    public static T Result<T>(ValueTask<T> value)
    {
        if (!value.IsCompletedSuccessfully) throw new InvalidOperationException("Asynchronous/faulted operation: per-thread allocation figures forbidden.");
        return value.GetAwaiter().GetResult();
    }
    public static void Complete(ValueTask value)
    {
        if (!value.IsCompletedSuccessfully) throw new InvalidOperationException("Asynchronous/faulted operation: per-thread allocation figures forbidden.");
        value.GetAwaiter().GetResult();
    }
}
sealed class FixedClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
}
sealed class EphemeralSecrets : IProxySecretSource
{
    // The real deriver takes ownership and clears this array when disposed.
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    public byte[] GetLoginKey() => _key;
    public byte[] GetProtectionKey() => throw new NotSupportedException();
}
sealed class FixtureClassifier : IElementClassifier
{
    private readonly Task<ClassificationOutcome>[] _results = new Task<ClassificationOutcome>[33];
    private readonly int _thread;
    public FixtureClassifier(int thread, bool disabled)
    {
        _thread = thread;
        for (var count = 0; count <= 32; count++)
        {
            var scorers = ElementScorers.All.Select(scorer => new ElementScorerResult(scorer, ElementScorers.VersionOf(scorer)!,
                Enumerable.Range(0, count).Select(ordinal => new SegmentElementScores(ordinal,
                    ElementVocabulary.All.Select(element => new ElementScore(element, ElementAvailability.Available, 0.5, null, ElementNotProducedReason.None)).ToArray())).ToArray())).ToArray();
            _results[count] = Task.FromResult<ClassificationOutcome>(disabled
                ? new ClassificationOutcome.Unavailable(ClassificationUnavailableReason.NoAnalyserConfigured)
                : new ClassificationOutcome.Success(new ElementClassification(new EmbeddingMetadata("fixture-model", null, 3, 3, "fixture-prefix", "float32", "fixture-contract"), ElementVocabulary.Version, scorers)));
        }
    }
    public Task<ClassificationOutcome> ClassifyAsync(IReadOnlyList<string> segments, IReadOnlyList<string>? scorers = null, CancellationToken cancellationToken = default)
    {
        if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Classifier thread changed.");
        return _results[segments.Count];
    }
}
sealed class CountingSink(int thread) : IProxyElementSink
{
    public int Segments { get; private set; }
    public int Points { get; private set; }
    public ProxyWarningAvailability Availability { get; private set; }
    public ProxySegmentationAvailabilityReason Reason { get; private set; }
    public ProxySegmentationOutcome? LastOutcome { get; private set; }
    public ValueTask RecordOutcomeAsync(ProxySegmentationOutcome outcome, CancellationToken cancellationToken) => RecordBatchAsync(outcome, Array.Empty<ProxyElementPoint>(), cancellationToken);
    public ValueTask RecordPointAsync(ProxyElementPoint point, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask RecordBatchAsync(ProxySegmentationOutcome outcome, IReadOnlyList<ProxyElementPoint> points, CancellationToken cancellationToken)
    {
        if (Environment.CurrentManagedThreadId != thread) throw new InvalidOperationException("Sink thread changed.");
        LastOutcome = outcome;
        Segments = outcome.SegmentCount; Points = points.Count; Availability = outcome.Availability; Reason = outcome.AvailabilityReason;
        return ValueTask.CompletedTask;
    }
    public object Snapshot() => new { Segments, Points, Availability, Reason, LastOutcome!.Truncated, LastOutcome.AnySegmentDropped };
}
