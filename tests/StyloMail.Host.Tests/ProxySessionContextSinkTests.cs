using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using StyloMail.AccessProxy.Hosting;
using StyloMail.Host.Proxy;

namespace StyloMail.Host.Tests;

/// <summary>
/// The approved authenticated-account sink: one per connection, updates the existing live row, and
/// never lets a telemetry failure affect the relay.
/// </summary>
public sealed class ProxySessionContextSinkTests
{
    [Theory]
    [InlineData(true, "SmtpDataAbsoluteTimeout")]
    [InlineData(false, "SmtpDataIdleTimeout")]
    public void DATA_timeout_logs_distinct_content_free_causes(bool absolute, string cause)
    {
        var logger = new RecordingLogger();
        var sink = new ProxySessionContextSink(new RecordingAuthenticator(), logger);
        ((IProxySubmissionReceiptSink)sink).OnDataReceiptTimeout(absolute);
        var line = Assert.Single(logger.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal($"SMTP DATA receipt timeout: {cause}.", line.Text);
    }

    [Fact]
    public void A_settled_relay_fault_logs_only_its_type_at_debug_level()
    {
        var logger = new RecordingLogger();
        var sink = new ProxySessionContextSink(new RecordingAuthenticator(), logger);
        ((IProxyRelayBufferSink)sink).OnRelayIoSettled(nameof(InvalidOperationException));
        var line = Assert.Single(logger.Lines);
        Assert.Equal(LogLevel.Debug, line.Level);
        Assert.Equal("Closed relay I/O settled with InvalidOperationException.", line.Text);
    }

    [Fact]
    public void An_abandoned_relay_buffer_emits_one_content_free_warning()
    {
        var logger = new RecordingLogger();
        var sink = new ProxySessionContextSink(new RecordingAuthenticator(), logger);
        ((IProxyRelayBufferSink)sink).OnRelayBufferAbandoned();
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Lines).Level);
        Assert.Equal("Closed relay I/O did not settle within one second; its buffer was abandoned without clearing or pooling.", logger.Lines.Single().Text);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task The_sink_attributes_the_account_to_the_ambient_session()
    {
        var authenticator = new RecordingAuthenticator();
        var sink = new ProxySessionContextSink(authenticator, NullLogger.Instance);

        ProxySessionContext.Current.Value =
            new ProxySessionBinding("ps_42", "acme", ProxyListenerProtocol.Imap);
        try
        {
            await sink.OnAuthenticatedAsync(
                "pa_account", new DateTimeOffset(2026, 10, 17, 21, 0, 0, TimeSpan.Zero), CancellationToken.None);
        }
        finally
        {
            ProxySessionContext.Current.Value = null;
        }

        Assert.Equal(("acme", "ps_42", "pa_account"), authenticator.Last);
    }

    [Fact]
    public async Task A_failing_write_is_swallowed_so_the_session_continues()
    {
        var sink = new ProxySessionContextSink(new ThrowingAuthenticator(), NullLogger.Instance);

        ProxySessionContext.Current.Value =
            new ProxySessionBinding("ps_42", "acme", ProxyListenerProtocol.Imap);
        try
        {
            await sink.OnAuthenticatedAsync("pa_account", DateTimeOffset.UtcNow, CancellationToken.None);
        }
        finally
        {
            ProxySessionContext.Current.Value = null;
        }
    }

    private sealed class RecordingAuthenticator : IProxySessionAuthenticator
    {
        public (string Tenant, string Session, string Account)? Last { get; private set; }

        public ValueTask MarkAuthenticatedAsync(
            string tenantId,
            string sessionId,
            string accountId,
            DateTimeOffset authenticatedAt,
            CancellationToken cancellationToken)
        {
            Last = (tenantId, sessionId, accountId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingAuthenticator : IProxySessionAuthenticator
    {
        public ValueTask MarkAuthenticatedAsync(
            string tenantId,
            string sessionId,
            string accountId,
            DateTimeOffset authenticatedAt,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("telemetry storage is down");
    }
}
