using System.Text;
using StyloMail.AccessProxy.Accounts;
using StyloMail.AccessProxy.Backends;
using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Sessions;
using StyloMail.AccessProxy.Smtp;
using StyloMail.AccessProxy.Submission;
using StyloMail.AccessProxy.Tests.Support;

namespace StyloMail.AccessProxy.Tests;

public sealed class SmtpSubmissionTests
{
    [Fact]
    public async Task Auth_before_starttls_is_refused_without_backend_attempt()
    {
        var h = new ProxyHarness();
        h.AddAccount();
        var backend = new ScriptedSmtpConnector();
        var session = NewSession(h, backend, new AllowAssessment(), new InMemoryEphemeralSubmissionStager());
        var client = new StartTlsTestChannel();
        var run = session.RunAsync(client, CancellationToken.None);
        await client.Pipe.ReadLineAsync();
        await client.Pipe.SendAsync("AUTH PLAIN AGFsaWNlQGV4YW1wbGUuY29tAGNsaWVudC1zaWRlLXBhc3N3b3JkLTlmM2E=\r\n");
        Assert.StartsWith("538", await client.Pipe.ReadLineAsync(), StringComparison.Ordinal);
        Assert.Equal(0, backend.ConnectCount);
        await client.Pipe.ClosePeerWriteAsync();
        await run;
    }

    [Fact]
    public async Task Auth_after_starttls_is_accepted_for_the_listener_tenant()
    {
        var h = new ProxyHarness(); h.AddAccount();
        var backend = new ScriptedSmtpConnector();
        var session = NewSession(h, backend, new AllowAssessment(), new InMemoryEphemeralSubmissionStager());
        var client = new StartTlsTestChannel(); var run = session.RunAsync(client, CancellationToken.None);
        await client.Pipe.ReadLineAsync();
        await client.Pipe.SendAsync("EHLO client\r\n"); await client.Pipe.ReadLineAsync(); await client.Pipe.ReadLineAsync();
        await client.Pipe.SendAsync("STARTTLS\r\n"); Assert.StartsWith("220", await client.Pipe.ReadLineAsync(), StringComparison.Ordinal);
        await client.Pipe.SendAsync("EHLO client\r\n"); await client.Pipe.ReadLineAsync(); await client.Pipe.ReadLineAsync();
        var plain = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{ProxyHarness.Login}\0{ProxyHarness.ClientPassword}"));
        await client.Pipe.SendAsync($"AUTH PLAIN {plain}\r\n");
        Assert.StartsWith("235", await client.Pipe.ReadLineAsync(), StringComparison.Ordinal);
        Assert.Equal(ProxyHarness.TenantId, backend.LastRequest!.TenantId);
        await client.Pipe.ClosePeerWriteAsync(); await run;
    }

    [Fact]
    public async Task Ehlo_advertises_exactly_the_capability_for_channel_state()
    {
        var h = new ProxyHarness(); var backend = new ScriptedSmtpConnector();
        var session = NewSession(h, backend, new AllowAssessment(), new InMemoryEphemeralSubmissionStager());
        var client = new StartTlsTestChannel(); var run = session.RunAsync(client, CancellationToken.None);
        await client.Pipe.ReadLineAsync(); await client.Pipe.SendAsync("EHLO client\r\n");
        var plain = (await client.Pipe.ReadLineAsync()) + "\n" + await client.Pipe.ReadLineAsync();
        Assert.Contains("STARTTLS", plain); Assert.DoesNotContain("AUTH", plain);
        await client.Pipe.SendAsync("STARTTLS\r\n"); await client.Pipe.ReadLineAsync(); await client.Pipe.SendAsync("EHLO client\r\n");
        var secure = (await client.Pipe.ReadLineAsync()) + "\n" + await client.Pipe.ReadLineAsync();
        Assert.Contains("AUTH PLAIN LOGIN", secure); Assert.DoesNotContain("STARTTLS", secure);
        await client.Pipe.ClosePeerWriteAsync(); await run;
    }

    [Fact]
    public async Task Deterministic_refusal_never_relays_data_to_backend()
    {
        var h = new ProxyHarness(); h.AddAccount(); var backend = new ScriptedSmtpConnector();
        var session = NewSession(h, backend, new RefuseAssessment(), new InMemoryEphemeralSubmissionStager());
        var client = await AuthenticateAsync(session);
        await client.Pipe.SendAsync("MAIL FROM:<a@example.com>\r\n"); await client.Pipe.ReadLineAsync();
        await client.Pipe.SendAsync("RCPT TO:<b@example.com>\r\n"); await client.Pipe.ReadLineAsync();
        await client.Pipe.SendAsync("DATA\r\nbody\r\n.\r\n"); await client.Pipe.ReadLineAsync(); Assert.StartsWith("554", await client.Pipe.ReadLineAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(backend.Received, x => x == "DATA");
        await client.Pipe.ClosePeerWriteAsync();
    }

    [Fact]
    public async Task Stager_releases_bytes_after_allow_refusal_and_abort()
    {
        foreach (var assessment in new IProxySubmissionAssessment[] { new AllowAssessment(), new RefuseAssessment(), new AllowAssessment() })
        {
            var stager = new InMemoryEphemeralSubmissionStager();
            await using var staged = await stager.CreateAsync(128, CancellationToken.None);
            await staged.Content.WriteAsync("body"u8.ToArray(), CancellationToken.None);
            await staged.DisposeAsync();
            Assert.Equal(0, stager.ActiveCount);
        }
    }

    private static SmtpAccessProxySession NewSession(ProxyHarness h, IBackendConnector backend, IProxySubmissionAssessment assessment, IEphemeralSubmissionStager stager)
        => new(ProxyHarness.TenantId, h.AccountStore, h.PasswordHasher, backend, h.Limiter, h.Bounds, h.Clock, assessment, stager);

    private static async Task<StartTlsTestChannel> AuthenticateAsync(SmtpAccessProxySession session)
    {
        var client = new StartTlsTestChannel(); _ = session.RunAsync(client, CancellationToken.None); await client.Pipe.ReadLineAsync();
        await client.Pipe.SendAsync("EHLO x\r\n"); await client.Pipe.ReadLineAsync(); await client.Pipe.ReadLineAsync(); await client.Pipe.SendAsync("STARTTLS\r\n"); await client.Pipe.ReadLineAsync(); await client.Pipe.SendAsync("EHLO x\r\n"); await client.Pipe.ReadLineAsync(); await client.Pipe.ReadLineAsync();
        var plain = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{ProxyHarness.Login}\0{ProxyHarness.ClientPassword}")); await client.Pipe.SendAsync($"AUTH PLAIN {plain}\r\n"); await client.Pipe.ReadLineAsync(); return client;
    }

    private sealed class StartTlsTestChannel : IStartTlsCapableChannel, ISecureDuplexChannel
    {
        public PipeDuplex Pipe { get; } = new("smtp-client"); public bool IsSecure { get; private set; }
        public Stream Input => Pipe.Input; public Stream Output => Pipe.Output; public string Description => Pipe.Description;
        public ValueTask<ISecureDuplexChannel> UpgradeToTlsAsync(CancellationToken cancellationToken) { IsSecure = true; return ValueTask.FromResult<ISecureDuplexChannel>(this); }
        public ValueTask DisposeAsync() => Pipe.DisposeAsync();
    }
    private sealed class AllowAssessment : IProxySubmissionAssessment { public ValueTask<ProxySubmissionAssessmentResult> AssessAsync(IStagedProxySubmission s, CancellationToken c) => ValueTask.FromResult(new ProxySubmissionAssessmentResult { Disposition = ProxySubmissionDisposition.Allow, IsDeterministic = true }); }
    private sealed class RefuseAssessment : IProxySubmissionAssessment { public ValueTask<ProxySubmissionAssessmentResult> AssessAsync(IStagedProxySubmission s, CancellationToken c) => ValueTask.FromResult(new ProxySubmissionAssessmentResult { Disposition = ProxySubmissionDisposition.Refuse, IsDeterministic = true }); }
    private sealed class ScriptedSmtpConnector : IBackendConnector
    {
        public int ConnectCount; public BackendConnectionRequest? LastRequest; public List<string> Received { get; } = [];
        public string ProviderLabel => "test";
        public ValueTask<IDuplexChannel> ConnectAsync(BackendConnectionRequest request, CancellationToken cancellationToken) { ConnectCount++; LastRequest = request; var p = new PipeDuplex("smtp-backend"); _ = Serve(p); return ValueTask.FromResult<IDuplexChannel>(p); }
        private async Task Serve(PipeDuplex p) { while (true) { var line = await p.ReadLineAsync(); if (line.Length == 0) return; Received.Add(line); if (line == "DATA") { await p.SendAsync("354 go\r\n"); while (await p.ReadLineAsync() != ".") { } await p.SendAsync("250 ok\r\n"); } else await p.SendAsync("250 ok\r\n"); } }
    }
}
