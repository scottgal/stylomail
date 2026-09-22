using System.Text;
using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Sessions;
using StyloMail.AccessProxy.Tests.Support;

namespace StyloMail.AccessProxy.Tests;

/// <summary>
/// The POP3 slice, over the same credential seam and the same backend connector base.
/// </summary>
public sealed class Pop3SessionTests
{
    [Fact]
    public async Task UserPass_AuthenticatesAndReachesTheBackend()
    {
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var run = harness.NewPop3Session().RunAsync(client, CancellationToken.None);

        Assert.StartsWith("+OK", await client.ReadLineAsync(), StringComparison.Ordinal);

        await client.SendAsync("USER " + ProxyHarness.Login + "\r\n");
        Assert.StartsWith("+OK", await client.ReadLineAsync(), StringComparison.Ordinal);

        await client.SendAsync("PASS " + ProxyHarness.ClientPassword + "\r\n");
        Assert.StartsWith("+OK", await client.ReadLineAsync(), StringComparison.Ordinal);

        await client.ClosePeerWriteAsync();
        Assert.Equal(SessionOutcome.Relayed, await run);
    }

    [Fact]
    public async Task BackendIsAuthenticatedWithUserPass_ForAnAppPassword()
    {
        // The framing choice the app-password provider makes for POP3, asserted where it lands: on
        // the wire to the provider.
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var run = harness.NewPop3Session().RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync("USER " + ProxyHarness.Login + "\r\n");
        await client.ReadLineAsync();
        await client.SendAsync("PASS " + ProxyHarness.ClientPassword + "\r\n");
        await client.ReadLineAsync();

        var commands = harness.Transport.ReceivedCommands;
        Assert.Contains(commands, c => c.StartsWith("USER " + ProxyHarness.Login, StringComparison.Ordinal));
        Assert.Contains(commands, c => c.StartsWith("PASS " + ProxyHarness.AppPasswordSecret, StringComparison.Ordinal));

        await client.ClosePeerWriteAsync();
        await run;
    }

    [Fact]
    public async Task WrongClientPassword_IsRejectedWithoutTouchingTheBackend()
    {
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var run = harness.NewPop3Session().RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync("USER " + ProxyHarness.Login + "\r\n");
        await client.ReadLineAsync();
        await client.SendAsync("PASS not-the-password\r\n");

        Assert.StartsWith("-ERR", await client.ReadLineAsync(), StringComparison.Ordinal);
        Assert.Equal(SessionOutcome.ClientRejected, await run);
        Assert.Equal(0, harness.Transport.OpenCount);
    }

    [Fact]
    public async Task PassWithoutUser_IsAProtocolErrorNotALoginWithAnEmptyUsername()
    {
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var run = harness.NewPop3Session().RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync("PASS " + ProxyHarness.ClientPassword + "\r\n");

        Assert.StartsWith("-ERR", await client.ReadLineAsync(), StringComparison.Ordinal);

        await client.ClosePeerWriteAsync();
        await run;
        Assert.Equal(0, harness.Transport.OpenCount);
    }

    [Fact]
    public async Task SaslPlain_AuthenticatesAndUsesXoauth2OnTheBackend_WhenOAuthIsStored()
    {
        var harness = new ProxyHarness();
        await harness.EnrolOAuthAccountAsync();

        var client = new PipeDuplex("client");
        var run = harness.NewPop3Session().RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();

        var payload = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"\0{ProxyHarness.Login}\0{ProxyHarness.ClientPassword}"));
        await client.SendAsync($"AUTH PLAIN {payload}\r\n");

        Assert.StartsWith("+OK", await client.ReadLineAsync(), StringComparison.Ordinal);

        var authCommand = harness.Transport.ReceivedCommands
            .FirstOrDefault(c => c.StartsWith("AUTH ", StringComparison.Ordinal));
        Assert.NotNull(authCommand);
        Assert.StartsWith("AUTH XOAUTH2", authCommand, StringComparison.Ordinal);

        await client.ClosePeerWriteAsync();
        await run;
    }

    [Fact]
    public async Task RevokedBackendCredential_FailsClosedAsAnAuthenticationFailure()
    {
        var harness = new ProxyHarness();
        harness.AddAccount();
        await harness.AddBackendCredentialAsync(revoked: true);

        var client = new PipeDuplex("client");
        var run = harness.NewPop3Session().RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync("USER " + ProxyHarness.Login + "\r\n");
        await client.ReadLineAsync();
        await client.SendAsync("PASS " + ProxyHarness.ClientPassword + "\r\n");

        Assert.StartsWith("-ERR", await client.ReadLineAsync(), StringComparison.Ordinal);
        Assert.Equal(SessionOutcome.BackendUnavailable, await run);
        Assert.Equal(0, harness.Transport.OpenCount);
    }

    [Fact]
    public async Task MessageBytes_AreRelayedVerbatim()
    {
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var run = harness.NewPop3Session().RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync("USER " + ProxyHarness.Login + "\r\n");
        await client.ReadLineAsync();
        await client.SendAsync("PASS " + ProxyHarness.ClientPassword + "\r\n");
        await client.ReadLineAsync();

        byte[] payload = [0x00, 0xFF, (byte)'\r', (byte)'\n', (byte)'.', (byte)'\r', (byte)'\n', 0xC3, 0xA9];
        await harness.Transport.Channel!.SendRawAsync(payload);

        var received = new byte[payload.Length];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.PeerInput.ReadExactlyAsync(received, cts.Token);

        Assert.Equal(payload, received);

        await client.ClosePeerWriteAsync();
        await run;
    }

    [Fact]
    public async Task UnsupportedSaslMechanism_IsRefusedRatherThanHalfSupported()
    {
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var run = harness.NewPop3Session().RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync("AUTH CRAM-MD5\r\n");

        Assert.StartsWith("-ERR", await client.ReadLineAsync(), StringComparison.Ordinal);

        await client.ClosePeerWriteAsync();
        await run;
        Assert.Equal(0, harness.Transport.OpenCount);
    }
}
