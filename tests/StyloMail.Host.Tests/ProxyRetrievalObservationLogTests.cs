using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Sessions;
using StyloMail.Host.Proxy;

namespace StyloMail.Host.Tests;

public sealed class ProxyRetrievalObservationLogTests
{
    private static readonly string[] ExpectedFields = ["Availability", "DroppedBytes", "DroppedChunks", "IntervalEnd", "IntervalStart", "Reason", "{OriginalFormat}"];
    [Fact]
    public async Task Copy_loss_is_aggregated_once_per_interval_without_message_or_account_identity()
    {
        var logger = new Capture();
        using var service = new ProxyRetrievalObservationLog(logger);
        var sink = new Sink();
        var factory = new ProxyRetrievalWarningAssemblerFactory(sink, new UnexpectedAnalyzer(), TimeProvider.System);
        await using var observer = factory.Create("private-tenant", "private-account");
        // Drop before BackgroundService gets a chance to execute: the initial snapshot must
        // belong to service construction, not its asynchronously scheduled ExecuteAsync.
        observer.Observe(new byte[1024 * 1024 + 1]);
        for (var i = 0; i < 100; i++) observer.Observe("private-message-body"u8);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var first = await logger.First.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal(LogLevel.Warning, first.Level);
            Assert.Equal(ProxyWarningAvailability.NotProduced, first.Fields["Availability"]);
            Assert.Equal(ProxyWarningAvailabilityReason.ServiceUnavailable, first.Fields["Reason"]);
            Assert.True((long)first.Fields["DroppedChunks"]! >= 101);
            Assert.True((long)first.Fields["DroppedBytes"]! >= 1024 * 1024 + 1 + 100 * "private-message-body"u8.Length);
            Assert.True((DateTimeOffset)first.Fields["IntervalEnd"]! > (DateTimeOffset)first.Fields["IntervalStart"]!);
            Assert.DoesNotContain("private-", first.Text, StringComparison.Ordinal);
            Assert.Equal(ExpectedFields,
                first.Fields.Keys.Order(StringComparer.Ordinal).ToArray());
            // More loss after the first interval must wait for the next tick, not log per chunk.
            for (var i = 0; i < 100; i++) observer.Observe("x"u8);
            Assert.Single(logger.Entries);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            while (logger.Entries.Count < 2) await Task.Delay(5, deadline.Token);
            Assert.Equal(2, logger.Entries.Count);
            Assert.True(logger.Entries.Last().At - first.At >= TimeSpan.FromMilliseconds(500));
            Assert.Equal(0, sink.Calls);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    private sealed class Sink : IProxyWarningEventSink
    {
        public int Calls { get; private set; }
        public ValueTask RecordOutcomeAsync(ProxyWarningAnalysisOutcome outcome, CancellationToken cancellationToken) { Calls++; return ValueTask.CompletedTask; }
        public ValueTask RecordMarkerAsync(ProxyDeterministicWarningEvent marker, CancellationToken cancellationToken) { Calls++; return ValueTask.CompletedTask; }
    }
    private sealed class UnexpectedAnalyzer : IProxyWarningAnalyzer
    {
        public ValueTask<ProxyWarningAnalysisResult> AnalyzeAsync(ProxyWarningAnalysisInput input, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Dropped copies must never reach analysis.");
    }
    private sealed record Entry(LogLevel Level, Dictionary<string, object?> Fields, string Text, DateTimeOffset At);
    private sealed class Capture : ILogger<ProxyRetrievalObservationLog>
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public TaskCompletionSource<Entry> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var entry = new Entry(logLevel, ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(), formatter(state, exception), DateTimeOffset.UtcNow);
            Entries.Enqueue(entry); First.TrySetResult(entry);
        }
    }
}
