using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Reflection;
using System.Text;
using StyloMail.AccessProxy.Backends;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Sessions;
using StyloMail.AccessProxy.Smtp;
using StyloMail.AccessProxy.Submission;
using StyloMail.AccessProxy.Tests.Support;

namespace StyloMail.AccessProxy.Tests;

public sealed class SmtpStagingBoundsTests
{
    [Fact]
    public async Task Real_tcp_reset_during_DATA_is_a_named_disconnect_with_released_capacity()
    {
        await using var fixture = await Fixture.Create();
        using var client = await fixture.Connect();
        Assert.StartsWith("354", await client.BeginData());
        await client.Line("partially staged body");
        await Until(() => fixture.Stager.Last!.Length > 0);
        client.Reset();
        await Until(() => fixture.Outcomes.Contains(SessionOutcome.ClientDisconnected));
        Assert.Equal(default, fixture.Budget.Counters);
        Assert.Equal(0, fixture.Backend.DataCommands);
        Assert.Empty(fixture.Errors);
        var failure = Assert.Single(fixture.Diagnostics.Failures);
        Assert.Equal("client_data_receipt", failure.Stage);
        Assert.Equal(nameof(IOException), failure.Type);
        Assert.Equal(SessionOutcome.ClientDisconnected, failure.Outcome);
        await Until(() => fixture.Harness.Limiter.ActiveSessions == 0);
    }

    [Fact]
    public async Task Final_250_waits_for_actual_staging_disposal_and_capacity_release()
    {
        await using var fixture = await Fixture.Create(blockDisposal: true);
        using var client = await fixture.Connect();
        try
        {
            Assert.StartsWith("354", await client.BeginData());
            await client.Write("body\r\n.\r\n");
            await fixture.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var finalReply = client.Read();
            Assert.NotSame(finalReply, await Task.WhenAny(finalReply, Task.Delay(100)));
            Assert.Equal(1, fixture.Budget.Counters.ActiveStagers);
            fixture.DisposalRelease.TrySetResult();
            Assert.StartsWith("250", await finalReply);
            Assert.Equal(default, fixture.Budget.Counters);
            await client.Line("QUIT"); Assert.StartsWith("221", await client.Read());
        }
        finally { fixture.DisposalRelease.TrySetResult(); }
    }

    [Fact]
    public async Task Every_write_surface_is_bounded_before_growth_and_fragmented_writes_keep_fixed_capacity()
    {
        var budget = new SubmissionStagingBudget();
        var stager = new InMemoryEphemeralSubmissionStager(budget);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stager.CreateAsync(25L * 1024 * 1024 + 1, CancellationToken.None).AsTask());
        await using var staged = await stager.CreateAsync(4096, CancellationToken.None);
        // Instrument the actual backing store, so a reservation counter cannot claim a fixed
        // capacity while a hidden expandable stream grows behind it. No production API exposes it.
        var memory = (MemoryStream)staged.Content.GetType().GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(staged.Content)!;
        var backing = (byte[])typeof(MemoryStream).GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(memory)!;
        Assert.Equal(4096, backing.Length);
        var chunk = Enumerable.Repeat((byte)41, 1024).ToArray();
        for (var i = 0; i < 4; i++)
        {
            await staged.Content.WriteAsync(chunk.AsMemory());
            Assert.Equal(new SubmissionStagingCounters(1, 4096), budget.Counters);
            Assert.Equal(budget.Counters.CapacityBytes, memory.Capacity);
        }
        Assert.Equal(4096, staged.Length);
        Assert.Throws<ProxySubmissionSizeLimitException>(() => staged.Content.Write(chunk));
        Assert.Throws<ProxySubmissionSizeLimitException>(() => staged.Content.Write(chunk, 0, 1));
        Assert.Throws<ProxySubmissionSizeLimitException>(() => staged.Content.WriteByte(42));
        await Assert.ThrowsAsync<ProxySubmissionSizeLimitException>(() => staged.Content.WriteAsync(chunk.AsMemory()).AsTask());
        await Assert.ThrowsAsync<ProxySubmissionSizeLimitException>(() => staged.Content.WriteAsync(chunk, 0, 1));
        Assert.Throws<ProxySubmissionSizeLimitException>(() => staged.Content.SetLength(4097));
        Assert.Throws<ProxySubmissionSizeLimitException>(() => staged.Content.Seek(4097, SeekOrigin.Begin));
        Assert.Throws<ProxySubmissionSizeLimitException>(() => staged.Content.Position = 4097);
        Assert.Equal(4096, staged.Length);
        staged.Content.Position = 0;
        var actual = new byte[4096]; await staged.Content.ReadExactlyAsync(actual);
        Assert.All(actual, b => Assert.Equal(41, b));
        staged.Content.SetLength(1); // Disposal must clear the tail too, not just current Length.
        await staged.DisposeAsync(); await staged.DisposeAsync();
        Assert.All(backing, b => Assert.Equal(0, b));
        Assert.Equal(default, budget.Counters);
        Assert.Equal(0, stager.ActiveCount);
        Assert.False(staged.Content.CanRead);
    }

    [Fact]
    public async Task Completed_receipt_does_not_time_out_mandatory_assessment_or_upstream_delivery()
    {
        await using var fixture = await Fixture.Create(blockAssessment: true);
        using var client = await fixture.Connect();
        try
        {
            Assert.StartsWith("354", await client.BeginData());
            await client.Write("body\r\n.\r\n");
            await fixture.AssessmentEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.Harness.Clock.Advance(TimeSpan.FromMinutes(3));
            Assert.Equal(1, fixture.Budget.Counters.ActiveStagers);
            fixture.AssessmentRelease.TrySetResult();
            Assert.StartsWith("250", await client.Read());
            await Until(() => fixture.Budget.Counters == default);
            Assert.Equal("body\r\n"u8.ToArray(), Assert.Single(fixture.Backend.Messages));
            Assert.Empty(fixture.Diagnostics.Timeouts);
            Assert.Empty(fixture.Errors);
            await client.Line("QUIT"); Assert.StartsWith("221", await client.Read());
        }
        finally { fixture.AssessmentRelease.TrySetResult(); }
    }

    [Fact]
    public async Task Eight_real_tls_DATA_transactions_hold_capacity_ninth_gets_452_then_release_allows_exact_delivery()
    {
        await using var fixture = await Fixture.Create();
        var clients = new List<Peer>();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var client = await fixture.Connect(); clients.Add(client);
                Assert.StartsWith("354", await client.BeginData());
                Assert.Equal(i + 1, fixture.Budget.Counters.ActiveStagers);
                Assert.Equal((i + 1) * 25L * 1024 * 1024, fixture.Budget.Counters.CapacityBytes);
            }
            using var ninth = await fixture.Connect();
            Assert.StartsWith("452 4.3.1", await ninth.BeginData());
            Assert.Null(await ninth.Read()); // No 354 or false 250 after the capacity refusal.
            await Until(() => fixture.Outcomes.Contains(SessionOutcome.SubmissionCapacityReached));
            Assert.Equal(0, fixture.Backend.DataCommands);
            Assert.Equal(8, fixture.Budget.Counters.ActiveStagers);
            clients[0].Dispose();
            await Until(() => fixture.Budget.Counters.ActiveStagers == 7);
            using var resumed = await fixture.Connect();
            Assert.StartsWith("354", await resumed.BeginData());
            const string data = "Subject: exact\r\n\r\nASCII body\r\n..already-stuffed\r\n";
            await resumed.Write(data + ".\r\n");
            Assert.StartsWith("250", await resumed.Read());
            await Until(() => fixture.Budget.Counters.ActiveStagers == 7);
            Assert.Equal(1, fixture.Backend.DataCommands);
            Assert.Equal(Encoding.ASCII.GetBytes(data), Assert.Single(fixture.Backend.Messages));
            await resumed.Line("QUIT"); Assert.StartsWith("221", await resumed.Read());
        }
        finally { foreach (var client in clients) client.Dispose(); }
        await Until(() => fixture.Budget.Counters == default && fixture.Harness.Limiter.ActiveSessions == 0);
        Assert.Empty(fixture.Errors);
    }

    [Fact]
    public async Task Progress_cannot_extend_the_absolute_120_second_DATA_receipt_lifetime()
    {
        await using var fixture = await Fixture.Create();
        using var client = await fixture.Connect();
        Assert.StartsWith("354", await client.BeginData());
        for (var i = 1; i <= 4; i++)
        {
            await client.Line("progress");
            var expected = i * "progress\r\n"u8.Length;
            await Until(() => fixture.Stager.Last!.Length == expected);
            fixture.Harness.Clock.Advance(TimeSpan.FromSeconds(i == 4 ? 31 : 30));
        }
        Assert.StartsWith("421 4.4.2", await client.Read());
        Assert.Null(await client.Read());
        await Until(() => fixture.Outcomes.Contains(SessionOutcome.Timeout));
        Assert.True(Assert.Single(fixture.Diagnostics.Timeouts));
        Assert.Empty(fixture.Diagnostics.Bounds);
        Assert.Equal(default, fixture.Budget.Counters);
        Assert.Equal(0, fixture.Backend.DataCommands);
        Assert.Empty(fixture.Errors);
    }

    [Fact]
    public async Task Per_line_idle_has_a_distinct_cause_and_releases_staging()
    {
        await using var fixture = await Fixture.Create(new AccessProxyBounds { IdleTimeout = TimeSpan.FromSeconds(10), MaxConcurrentSessions = 32, MaxSessionsPerAccount = 32 });
        using var client = await fixture.Connect();
        Assert.StartsWith("354", await client.BeginData());
        await client.Line("progress"); await Until(() => fixture.Stager.Last!.Length > 0);
        fixture.Harness.Clock.Advance(TimeSpan.FromSeconds(11));
        Assert.StartsWith("421 4.4.2", await client.Read());
        await Until(() => fixture.Outcomes.Contains(SessionOutcome.Timeout));
        Assert.False(Assert.Single(fixture.Diagnostics.Timeouts));
        Assert.Equal(ProxyBoundReason.IdleTimeout, Assert.Single(fixture.Diagnostics.Bounds));
        Assert.Equal(default, fixture.Budget.Counters);
        Assert.Equal(0, fixture.Backend.DataCommands);
    }

    [Fact]
    public async Task Oversize_552_releases_capacity_without_backend_DATA()
    {
        await using var fixture = await Fixture.Create(new AccessProxyBounds { MaxSubmissionBytes = 64, MaxConcurrentSessions = 32, MaxSessionsPerAccount = 32 });
        using var client = await fixture.Connect();
        Assert.StartsWith("354", await client.BeginData());
        await client.Line(new string('x', 100));
        Assert.StartsWith("552", await client.Read());
        await Until(() => fixture.Outcomes.Contains(SessionOutcome.ProtocolError));
        Assert.Equal(default, fixture.Budget.Counters);
        Assert.Equal(0, fixture.Backend.DataCommands);
    }

    [Fact]
    public async Task Mandatory_assessment_exception_releases_capacity_without_forwarding_DATA()
    {
        await using var fixture = await Fixture.Create(failAssessment: true);
        using var client = await fixture.Connect();
        Assert.StartsWith("354", await client.BeginData());
        await client.Write("body\r\n.\r\n");
        Assert.Null(await client.Read());
        await Until(() => fixture.Errors.Count == 1);
        var internalFailure = Assert.Single(fixture.Diagnostics.Failures);
        Assert.Null(internalFailure.Outcome);
        Assert.Equal(nameof(InvalidOperationException), internalFailure.Type);
        Assert.Equal(default, fixture.Budget.Counters);
        Assert.Equal(0, fixture.Backend.DataCommands);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, stop.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public ProxyHarness Harness { get; private set; } = null!;
        public SubmissionStagingBudget Budget { get; } = new();
        public TrackingStager Stager { get; private set; } = null!;
        public Backend Backend { get; } = new();
        public Diagnostics Diagnostics { get; } = new();
        public ConcurrentQueue<SessionOutcome> Outcomes { get; } = new();
        public ConcurrentQueue<Exception> Errors { get; } = new();
        public TaskCompletionSource AssessmentEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AssessmentRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposalRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private X509Certificate2 _certificate = null!;
        private TcpAccessProxyListener _listener = null!;
        public static async Task<Fixture> Create(AccessProxyBounds? bounds = null, bool failAssessment = false, bool blockAssessment = false, bool blockDisposal = false)
        {
            var fixture = new Fixture();
            fixture.Harness = new ProxyHarness(bounds ?? new AccessProxyBounds { MaxConcurrentSessions = 32, MaxSessionsPerAccount = 32 });
            fixture.Harness.AddAccount(); fixture.Stager = new TrackingStager(new InMemoryEphemeralSubmissionStager(fixture.Budget),
                blockDisposal ? fixture.DisposalRelease.Task : null, () => fixture.DisposalEntered.TrySetResult());
            using var key = RSA.Create(2048);
            fixture._certificate = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
            var tls = new TlsServerOptions { ServerCertificate = fixture._certificate };
            fixture._listener = new TcpAccessProxyListener(new IPEndPoint(IPAddress.Loopback, 0), ProxyHarness.TenantId,
                async (tenant, raw, token) =>
                {
                    try
                    {
                        var result = await ProxySessionRunnerFactory.RunWithTlsAsync(ProxyListenerProtocol.Smtp, tls, fixture.Harness.Bounds,
                            fixture.Harness.Clock, async (_, secure, ct) => await new SmtpAccessProxySession(tenant,
                                fixture.Harness.AccountStore, fixture.Harness.PasswordHasher, fixture.Backend, fixture.Harness.Limiter,
                                fixture.Harness.Bounds, fixture.Harness.Clock,
                                new Assessment(failAssessment, blockAssessment ? fixture.AssessmentRelease.Task : null, () => fixture.AssessmentEntered.TrySetResult()), fixture.Stager,
                                fixture.Diagnostics, new Sink(), new Analyzer()).RunAsync(secure, ct), tenant, raw, token);
                        fixture.Outcomes.Enqueue(result); return result;
                    }
                    catch (Exception exception) { fixture.Errors.Enqueue(exception); return SessionOutcome.ProtocolError; }
                }, fixture.Harness.Limiter);
            await fixture._listener.StartAsync(CancellationToken.None); return fixture;
        }
        public Task<Peer> Connect() => Peer.Connect(_listener.BoundPort);
        public async ValueTask DisposeAsync()
        {
            await _listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Backend.DisposeAsync();
            _certificate.Dispose(); Harness.Limiter.Dispose();
            Assert.Equal(default, Budget.Counters);
        }
    }
    private sealed class Peer : IDisposable
    {
        private readonly TcpClient _tcp = new(AddressFamily.InterNetwork);
        private StreamReader _reader = null!;
        private StreamWriter _writer = null!;
        private void Streams(Stream stream)
        {
            _reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            _writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        }
        public static async Task<Peer> Connect(int port)
        {
            var peer = new Peer(); await peer._tcp.ConnectAsync(IPAddress.Loopback, port); peer.Streams(peer._tcp.GetStream());
            Assert.StartsWith("220", await peer.Read());
            await peer.Line("EHLO client"); await peer.Read(); await peer.Read();
            await peer.Line("STARTTLS"); Assert.StartsWith("220", await peer.Read());
            peer._reader.Dispose(); peer._writer.Dispose();
            var tls = new SslStream(peer._tcp.GetStream(), false, (_, _, _, _) => true);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, stop.Token); peer.Streams(tls);
            await peer.Line("EHLO client"); await peer.Read(); await peer.Read();
            var plain = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{ProxyHarness.Login}\0{ProxyHarness.ClientPassword}"));
            await peer.Line("AUTH PLAIN " + plain); Assert.StartsWith("235", await peer.Read()); return peer;
        }
        public async Task<string?> BeginData()
        {
            await Line("MAIL FROM:<a@example.com>"); Assert.StartsWith("250", await Read());
            await Line("RCPT TO:<b@example.com>"); Assert.StartsWith("250", await Read());
            await Line("DATA"); return await Read();
        }
        public async Task<string?> Read()
        { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); return await _reader.ReadLineAsync(stop.Token); }
        public Task Line(string text) => Write(text + "\r\n");
        public async Task Write(string text)
        { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await _writer.WriteAsync(text.AsMemory(), stop.Token); await _writer.FlushAsync(stop.Token); }
        public void Dispose() => _tcp.Dispose();
        public void Reset() { _tcp.Client.LingerState = new LingerOption(true, 0); _tcp.Dispose(); }
    }
    private sealed class Backend : IBackendConnector, IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<Task> _servers = new();
        private int _data;
        public int DataCommands => Volatile.Read(ref _data);
        public ConcurrentQueue<byte[]> Messages { get; } = new();
        public string ProviderLabel => "scripted";
        public ValueTask<IDuplexChannel> ConnectAsync(BackendConnectionRequest request, CancellationToken cancellationToken)
        { var pipe = new PipeDuplex(); _servers.Enqueue(Serve(pipe)); return ValueTask.FromResult<IDuplexChannel>(pipe); }
        private async Task Serve(PipeDuplex pipe)
        {
            using var reader = new StreamReader(pipe.PeerInput, Encoding.ASCII, leaveOpen: true);
            try
            {
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    if (line == "DATA")
                    {
                        Interlocked.Increment(ref _data); await pipe.SendAsync("354 go\r\n");
                        using var data = new MemoryStream();
                        while (await reader.ReadLineAsync(_stop.Token) is { } body && body != ".") data.Write(Encoding.ASCII.GetBytes(body + "\r\n"));
                        Messages.Enqueue(data.ToArray());
                    }
                    await pipe.SendAsync("250 ok\r\n");
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException) { }
        }
        public async ValueTask DisposeAsync() { await _stop.CancelAsync(); await Task.WhenAll(_servers).WaitAsync(TimeSpan.FromSeconds(3)); _stop.Dispose(); }
    }
    private sealed class TrackingStager(InMemoryEphemeralSubmissionStager inner, Task? gate, Action entered) : IEphemeralSubmissionStager
    {
        public IStagedProxySubmission? Last { get; private set; }
        public async ValueTask<IStagedProxySubmission> CreateAsync(long maximumBytes, CancellationToken ct)
        { var staged = await inner.CreateAsync(maximumBytes, ct); Last = gate is null ? staged : new GatedDisposal(staged, gate, entered); return Last; }
        public ValueTask DeleteExpiredAsync(DateTimeOffset olderThan, CancellationToken ct) => inner.DeleteExpiredAsync(olderThan, ct);
    }
    private sealed class GatedDisposal(IStagedProxySubmission inner, Task gate, Action entered) : IStagedProxySubmission
    {
        private Task? _disposing;
        public Stream Content => inner.Content;
        public long Length => inner.Length;
        public ValueTask DisposeAsync() => new(_disposing ??= DisposeCore());
        private async Task DisposeCore() { entered(); await gate; await inner.DisposeAsync(); }
    }
    private sealed class Diagnostics : IProxySessionContextSink, IProxySubmissionReceiptSink, IProxySessionBoundSink, IProxySessionFailureSink
    {
        public ConcurrentQueue<bool> Timeouts { get; } = new();
        public ConcurrentQueue<ProxyBoundReason> Bounds { get; } = new();
        public ConcurrentQueue<(string Stage, string Type, SessionOutcome? Outcome)> Failures { get; } = new();
        public ValueTask OnAuthenticatedAsync(string accountId, DateTimeOffset at, CancellationToken ct) => ValueTask.CompletedTask;
        public void OnDataReceiptTimeout(bool absolute) => Timeouts.Enqueue(absolute);
        public void OnBound(ProxyBoundReason reason) => Bounds.Enqueue(reason);
        public void OnFailure(string stage, string exceptionType, string? socketError, SessionOutcome? outcome) => Failures.Enqueue((stage, exceptionType, outcome));
    }
    private sealed class Assessment(bool fail, Task? gate, Action entered) : IProxySubmissionAssessment
    {
        public async ValueTask<ProxySubmissionAssessmentResult> AssessAsync(IStagedProxySubmission submission, CancellationToken ct)
        {
            entered();
            if (fail) throw new InvalidOperationException("Assessment control.");
            if (gate is not null) await gate.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            return new ProxySubmissionAssessmentResult { Disposition = ProxySubmissionDisposition.Allow, IsDeterministic = true };
        }
    }
    private sealed class Sink : IProxyWarningEventSink
    {
        public ValueTask RecordOutcomeAsync(ProxyWarningAnalysisOutcome outcome, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RecordMarkerAsync(ProxyDeterministicWarningEvent marker, CancellationToken ct) => ValueTask.CompletedTask;
    }
    private sealed class Analyzer : IProxyWarningAnalyzer
    {
        public ValueTask<ProxyWarningAnalysisResult> AnalyzeAsync(ProxyWarningAnalysisInput input, CancellationToken ct)
            => ValueTask.FromResult(new ProxyWarningAnalysisResult { MessageToken = "control-message", ThreadToken = "control-thread", Availability = ProxyWarningAvailability.Available, AvailabilityReason = ProxyWarningAvailabilityReason.None, Findings = [] });
    }
}
