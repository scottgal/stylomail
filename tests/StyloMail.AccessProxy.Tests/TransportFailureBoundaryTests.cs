using System.Net.Sockets;
using System.Text;
using StyloMail.AccessProxy.Backends;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Imap;
using StyloMail.AccessProxy.Sessions;
using StyloMail.AccessProxy.Smtp;
using StyloMail.AccessProxy.Submission;
using StyloMail.AccessProxy.Tests.Support;

namespace StyloMail.AccessProxy.Tests;

public sealed class TransportFailureBoundaryTests
{
    [Theory]
    [InlineData(false, "socket", SessionOutcome.BackendUnavailable)]
    [InlineData(true, "socket", SessionOutcome.BackendUnavailable)]
    [InlineData(false, "connect-timeout", SessionOutcome.Timeout)]
    [InlineData(true, "connect-timeout", SessionOutcome.Timeout)]
    [InlineData(false, "auth-task-canceled", SessionOutcome.Timeout)]
    [InlineData(true, "auth-task-canceled", SessionOutcome.Timeout)]
    [InlineData(false, "auth-operation-canceled", SessionOutcome.Timeout)]
    [InlineData(true, "auth-operation-canceled", SessionOutcome.Timeout)]
    [InlineData(false, "auth-timeout", SessionOutcome.Timeout)]
    [InlineData(true, "auth-timeout", SessionOutcome.Timeout)]
    public async Task Known_backend_transport_failures_have_exact_outcomes_causes_and_cleanup(bool smtp, string fault, SessionOutcome expected)
    {
        var exception = fault switch
        {
            "socket" => (Exception)new SocketException((int)SocketError.ConnectionRefused),
            "connect-timeout" => new AccessProxyTimeoutException("Fixture detail must not enter diagnostics."),
            "auth-task-canceled" => new TaskCanceledException("Fixture detail must not enter diagnostics."),
            "auth-operation-canceled" => new OperationCanceledException("Fixture detail must not enter diagnostics."),
            _ => new TimeoutException("Fixture detail must not enter diagnostics."),
        };
        var auth = fault.StartsWith("auth-", StringComparison.Ordinal);
        var result = await Run(smtp, auth, exception);
        Assert.Null(result.Failure);
        Assert.Equal(expected, result.Outcome);
        var diagnostic = Assert.Single(result.Diagnostics.Failures);
        Assert.Equal(auth ? "backend_authentication" : "backend_open", diagnostic.Stage);
        Assert.Equal(exception.GetType().Name, diagnostic.Type);
        Assert.Equal(fault == "socket" ? nameof(SocketError.ConnectionRefused) : null, diagnostic.SocketError);
        Assert.Equal(expected, diagnostic.Outcome);
        Assert.Equal(auth, result.ChannelDisposed);
        Assert.Equal(0, result.ActiveSlots);
        Assert.Equal(0, result.Stages);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Unexpected_backend_failures_propagate_as_internal_after_full_cleanup(bool smtp, bool duringAuthentication, bool io)
    {
        Exception exception = io ? new IOException("Fixture detail must not enter diagnostics.") : new ApplicationException("Fixture detail must not enter diagnostics.");
        var result = await Run(smtp, duringAuthentication, exception);
        Assert.Same(exception, result.Failure);
        Assert.Null(result.Outcome);
        var diagnostic = Assert.Single(result.Diagnostics.Failures);
        Assert.Null(diagnostic.Outcome);
        Assert.Equal(exception.GetType().Name, diagnostic.Type);
        Assert.Equal(duringAuthentication, result.ChannelDisposed);
        Assert.Equal(0, result.ActiveSlots);
        Assert.Equal(0, result.Stages);
    }

    private static async Task<Result> Run(bool smtp, bool auth, Exception exception)
    {
        var h = new ProxyHarness(); await h.EnrolAppPasswordAccountAsync();
        var transport = new Transport(auth, exception);
        IBackendConnector connector = smtp ? new SmtpBackendConnector(transport, h.Resolver, h.Bounds) : new ImapBackendConnector(transport, h.Resolver, h.Bounds);
        var diagnostics = new Diagnostics();
        var stager = new InMemoryEphemeralSubmissionStager(new SubmissionStagingBudget());
        await using var client = new SecurePipe();
        var run = smtp
            ? new SmtpAccessProxySession(ProxyHarness.TenantId, h.AccountStore, h.PasswordHasher, connector, h.Limiter, h.Bounds,
                h.Clock, new Assessment(), stager, diagnostics, new Sink(), new Analyzer()).RunAsync(client, CancellationToken.None)
            : new ImapAccessProxySession(ProxyHarness.TenantId, h.AccountStore, h.PasswordHasher, h.Resolver, connector,
                h.Limiter, h.Bounds, h.Clock, contextSink: diagnostics).RunAsync(client, CancellationToken.None);
        await client.Pipe.ReadLineAsync();
        if (smtp)
        {
            await client.Pipe.SendAsync("EHLO client\r\n"); await client.Pipe.ReadLineAsync(); await client.Pipe.ReadLineAsync();
            var plain = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{ProxyHarness.Login}\0{ProxyHarness.ClientPassword}"));
            await client.Pipe.SendAsync($"AUTH PLAIN {plain}\r\n");
        }
        else await client.Pipe.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
        SessionOutcome? outcome = null; Exception? failure = null;
        try { outcome = await run.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception caught) { failure = caught; }
        return new(outcome, failure, diagnostics, transport.Channel.Disposed, h.Limiter.ActiveSessions, stager.ActiveCount);
    }
    private sealed record Result(SessionOutcome? Outcome, Exception? Failure, Diagnostics Diagnostics, bool ChannelDisposed, int ActiveSlots, int Stages);
    private sealed class Diagnostics : IProxySessionContextSink, IProxySessionFailureSink
    {
        public List<(string Stage, string Type, string? SocketError, SessionOutcome? Outcome)> Failures { get; } = [];
        public ValueTask OnAuthenticatedAsync(string accountId, DateTimeOffset at, CancellationToken ct) => ValueTask.CompletedTask;
        public void OnFailure(string stage, string exceptionType, string? socketError, SessionOutcome? outcome) => Failures.Add((stage, exceptionType, socketError, outcome));
    }
    private sealed class SecurePipe : ISecureDuplexChannel
    {
        public PipeDuplex Pipe { get; } = new();
        public bool IsSecure => true;
        public Stream Input => Pipe.Input;
        public Stream Output => Pipe.Output;
        public string Description => "fixture";
        public ValueTask DisposeAsync() => Pipe.DisposeAsync();
    }
    private sealed class Transport(bool auth, Exception exception) : IBackendTransport
    {
        public FaultChannel Channel { get; } = new(exception);
        public ValueTask<IDuplexChannel> OpenAsync(BackendConnectionRequest request, CancellationToken ct)
            => auth ? ValueTask.FromResult<IDuplexChannel>(Channel) : ValueTask.FromException<IDuplexChannel>(exception);
    }
    private sealed class FaultChannel(Exception exception) : ISecureDuplexChannel
    {
        public bool Disposed { get; private set; }
        public bool IsSecure => true;
        public Stream Input { get; } = new FaultInput(exception);
        public Stream Output => Stream.Null;
        public string Description => "fixture";
        public ValueTask DisposeAsync() { Disposed = true; Input.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class FaultInput(Exception exception) : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(exception);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class Assessment : IProxySubmissionAssessment
    {
        public ValueTask<ProxySubmissionAssessmentResult> AssessAsync(IStagedProxySubmission submission, CancellationToken ct) => throw new InvalidOperationException("Backend failed before DATA.");
    }
    private sealed class Sink : IProxyWarningEventSink
    {
        public ValueTask RecordOutcomeAsync(ProxyWarningAnalysisOutcome outcome, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RecordMarkerAsync(ProxyDeterministicWarningEvent marker, CancellationToken ct) => ValueTask.CompletedTask;
    }
    private sealed class Analyzer : IProxyWarningAnalyzer
    {
        public ValueTask<ProxyWarningAnalysisResult> AnalyzeAsync(ProxyWarningAnalysisInput input, CancellationToken ct) => throw new InvalidOperationException("Backend failed before DATA.");
    }
}
