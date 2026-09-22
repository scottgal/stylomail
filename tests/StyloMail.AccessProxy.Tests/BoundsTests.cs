using StyloMail.AccessProxy.Sessions;
using StyloMail.AccessProxy.Tests.Support;

namespace StyloMail.AccessProxy.Tests;

/// <summary>
/// Every bound the proxy claims to enforce, driven to the point where it fires.
/// </summary>
/// <remarks>
/// Constraint 6: a proxy that can be made to hold unbounded resources is a denial-of-service vector
/// against the mail it protects. That claim is only worth anything if each bound has been seen to
/// fire — a limit that is configured but never exercised is an assertion, not a defence.
/// </remarks>
public sealed class BoundsTests
{
    [Fact]
    public async Task OverlongCommandLine_IsRejectedRatherThanBuffered()
    {
        var harness = new ProxyHarness(new AccessProxyBounds { MaxCommandLineBytes = 128 });
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var run = harness.NewImapSession().RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();

        // No terminator, so without a cap the reader would allocate for as long as the client kept
        // typing — the classic unbounded-command heap exhaustion.
        await client.SendAsync("a1 LOGIN " + new string('x', 4096));

        Assert.Equal(SessionOutcome.ProtocolError, await run);
        Assert.Equal(0, harness.Transport.OpenCount);
    }

    [Fact]
    public async Task EndlessShortCommands_AreRejected()
    {
        // The line-length cap alone still permits an unbounded number of short lines, so the count
        // cap is what actually closes this one.
        var harness = new ProxyHarness(new AccessProxyBounds { MaxAuthenticationCommands = 5 });
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var run = harness.NewImapSession().RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();

        // The bound permits five commands; the sixth is refused rather than read.
        for (var i = 0; i < 5; i++)
        {
            await client.SendAsync($"a{i} NOOP\r\n");
            Assert.StartsWith($"a{i} OK", await client.ReadLineAsync(), StringComparison.Ordinal);
        }

        await client.SendAsync("a6 NOOP\r\n");

        Assert.Equal(SessionOutcome.ProtocolError, await run);
        Assert.Equal(0, harness.Transport.OpenCount);
    }

    [Fact]
    public async Task PerAccountSessionLimit_RefusesFurtherSessionsButNotOtherAccounts()
    {
        // The cap that stops one compromised client credential from consuming the whole instance.
        var harness = new ProxyHarness(new AccessProxyBounds { MaxSessionsPerAccount = 2 });
        await harness.EnrolAppPasswordAccountAsync();
        harness.AddAccount(login: "bob@example.com", accountId: "acct-2");
        await harness.AddBackendCredentialAsync(accountId: "acct-2");

        var held = new List<(PipeDuplex Client, Task<SessionOutcome> Run)>();

        for (var i = 0; i < 2; i++)
        {
            var client = new PipeDuplex($"client-{i}");
            var run = harness.NewImapSession().RunAsync(client, CancellationToken.None);
            await client.ReadLineAsync();
            await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
            Assert.StartsWith("a1 OK", await client.ReadLineAsync(), StringComparison.Ordinal);
            held.Add((client, run));
        }

        // A third session for the same account is refused, and refused immediately rather than
        // queued — an account at its limit is not a burst to absorb.
        var third = new PipeDuplex("client-third");
        var thirdRun = harness.NewImapSession().RunAsync(third, CancellationToken.None);
        await third.ReadLineAsync();
        await third.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");

        Assert.StartsWith("a1 NO", await third.ReadLineAsync(), StringComparison.Ordinal);
        Assert.Equal(SessionOutcome.AccountLimitReached, await thirdRun);

        // But a different account is unaffected: the cap is per account, not a global throttle.
        var other = new PipeDuplex("client-other");
        var otherRun = harness.NewImapSession().RunAsync(other, CancellationToken.None);
        await other.ReadLineAsync();
        await other.SendAsync($"a1 LOGIN bob@example.com {ProxyHarness.ClientPassword}\r\n");

        Assert.StartsWith("a1 OK", await other.ReadLineAsync(), StringComparison.Ordinal);

        foreach (var (client, _) in held)
        {
            await client.ClosePeerWriteAsync();
        }

        await other.ClosePeerWriteAsync();
        await otherRun;
    }

    [Fact]
    public async Task IdleSession_IsReclaimedOnTheInjectedClock()
    {
        // The bound that protects the provider: an abandoned session holds a Gmail connection, and
        // Gmail caps those per account. Reclaimed here without real time passing.
        var harness = new ProxyHarness(new AccessProxyBounds { IdleTimeout = TimeSpan.FromMinutes(5) });
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var run = harness.NewImapSession().RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
        await client.ReadLineAsync();

        // Nothing more is sent by either side.
        harness.Clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(SessionOutcome.Timeout, await run);
    }

    [Fact]
    public async Task UnauthenticatedSession_IsReclaimedOnTheAuthenticationBound()
    {
        // An unauthenticated session has consumed no backend resources yet, and must not be allowed
        // to sit in the pool waiting.
        var harness = new ProxyHarness(new AccessProxyBounds { AuthenticationTimeout = TimeSpan.FromSeconds(30) });
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var run = harness.NewImapSession().RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        harness.Clock.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal(SessionOutcome.Timeout, await run);
        Assert.Equal(0, harness.Transport.OpenCount);
    }

    [Fact]
    public async Task LargeTransfers_CrossManyBuffersWithoutCorruption()
    {
        // RelayBufferBytes is the whole per-session memory footprint, so the interesting case is a
        // transfer far larger than it. A 64-byte buffer forces many reads and writes, which is where
        // an off-by-one in a pump would show up as a corrupt byte rather than as an error.
        var harness = new ProxyHarness(new AccessProxyBounds { RelayBufferBytes = 64 });
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var run = harness.NewImapSession().RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
        await client.ReadLineAsync();

        // Deterministic but not constant, so a mid-buffer splice is detectable.
        var payload = new byte[5000];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 31 % 251);
        }

        await harness.Transport.Channel!.SendRawAsync(payload);

        var received = new byte[payload.Length];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.PeerInput.ReadExactlyAsync(received, cts.Token);

        Assert.Equal(payload, received);

        await client.ClosePeerWriteAsync();
        await run;
    }

    [Fact]
    public void IncoherentBounds_AreRejectedAtConstruction()
    {
        // A per-account cap above the global cap can never be the thing that fires, so an operator
        // setting it would reasonably believe it was protecting something it is not.
        var bounds = new AccessProxyBounds { MaxConcurrentSessions = 4, MaxSessionsPerAccount = 8 };

        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionLimiter(bounds));
    }

    [Fact]
    public async Task ConcurrentSessionCount_IsReleasedWhenSessionsEnd()
    {
        // A limiter that leaks slots is worse than no limiter: it denies service permanently after
        // enough traffic. This is the counter's liveness check.
        var harness = new ProxyHarness(new AccessProxyBounds { MaxSessionsPerAccount = 1 });
        await harness.EnrolAppPasswordAccountAsync();

        for (var round = 0; round < 5; round++)
        {
            var client = new PipeDuplex($"client-{round}");
            var run = harness.NewImapSession().RunAsync(client, CancellationToken.None);

            await client.ReadLineAsync();
            await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
            Assert.StartsWith("a1 OK", await client.ReadLineAsync(), StringComparison.Ordinal);

            await client.ClosePeerWriteAsync();
            await run;
        }

        Assert.Equal(0, harness.Limiter.ActiveSessions);
    }
}
