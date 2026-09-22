using System.Text;
using StyloMail.AccessProxy.Sessions;
using StyloMail.AccessProxy.Tests.Support;

namespace StyloMail.AccessProxy.Tests;

/// <summary>
/// The IMAP slice end to end, against a backend that exists only in memory.
/// </summary>
public sealed class ImapSessionTests
{
    [Fact]
    public async Task ClientAuthenticatesWithStyloMailCredentials_AndReachesTheBackend()
    {
        // The whole point of the feature: a client that can only speak LOGIN user pass gets a
        // working session against a provider that no longer accepts that.
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var session = harness.NewImapSession();

        var run = session.RunAsync(client, CancellationToken.None);

        Assert.StartsWith("* OK", await client.ReadLineAsync(), StringComparison.Ordinal);

        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");

        Assert.StartsWith("a1 OK", await client.ReadLineAsync(), StringComparison.Ordinal);

        await client.ClosePeerWriteAsync();
        Assert.Equal(SessionOutcome.Relayed, await run);
    }

    [Fact]
    public async Task ClientAuthenticates_ViaSaslPlain_WithTheSameResult()
    {
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var session = harness.NewImapSession();
        var run = session.RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();

        var payload = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"\0{ProxyHarness.Login}\0{ProxyHarness.ClientPassword}"));
        await client.SendAsync($"a2 AUTHENTICATE PLAIN {payload}\r\n");

        Assert.StartsWith("a2 OK", await client.ReadLineAsync(), StringComparison.Ordinal);

        await client.ClosePeerWriteAsync();
        await run;
    }

    [Fact]
    public async Task WrongClientPassword_IsRejectedWithoutTouchingTheBackend()
    {
        // The backend must not be contacted at all. Asserting only on the client's NO would let an
        // implementation pass that had already opened a provider connection, or worse, already
        // presented the backend credential.
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var session = harness.NewImapSession();
        var run = session.RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} wrong-password\r\n");

        Assert.StartsWith("a1 NO", await client.ReadLineAsync(), StringComparison.Ordinal);
        Assert.Equal(SessionOutcome.ClientRejected, await run);
        Assert.Equal(0, harness.Transport.OpenCount);
        Assert.Equal(0, harness.OAuth.CallCount);
    }

    [Fact]
    public async Task UnknownLogin_IsRejectedWithoutTouchingTheBackend()
    {
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var session = harness.NewImapSession();
        var run = session.RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN nobody@example.com {ProxyHarness.ClientPassword}\r\n");

        Assert.StartsWith("a1 NO", await client.ReadLineAsync(), StringComparison.Ordinal);
        Assert.Equal(SessionOutcome.ClientRejected, await run);
        Assert.Equal(0, harness.Transport.OpenCount);
    }

    [Fact]
    public async Task DisabledAccount_IsRejectedWithoutTouchingTheBackend()
    {
        var harness = new ProxyHarness();
        harness.AddAccount(disabled: true);
        await harness.AddBackendCredentialAsync();

        var client = new PipeDuplex("client");
        var session = harness.NewImapSession();
        var run = session.RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");

        Assert.StartsWith("a1 NO", await client.ReadLineAsync(), StringComparison.Ordinal);
        Assert.Equal(SessionOutcome.ClientRejected, await run);
        Assert.Equal(0, harness.Transport.OpenCount);
    }

    [Fact]
    public async Task RevokedBackendCredential_FailsClosedAsAnAuthenticationFailure()
    {
        // Spec §9.5 risk 4. The client authenticated perfectly well; it is the backend credential
        // that is revoked. The client still sees an authentication failure rather than a session
        // that connects and then mysteriously dies.
        var harness = new ProxyHarness();
        harness.AddAccount();
        await harness.AddBackendCredentialAsync(revoked: true);

        var client = new PipeDuplex("client");
        var session = harness.NewImapSession();
        var run = session.RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");

        Assert.StartsWith("a1 NO", await client.ReadLineAsync(), StringComparison.Ordinal);
        Assert.Equal(SessionOutcome.BackendUnavailable, await run);

        // And it never reached the provider. A revoked credential that still produces a connection
        // attempt is a failed login attempt against the user's own Google account, repeated for
        // every client that reconnects.
        Assert.Equal(0, harness.Transport.OpenCount);
    }

    [Fact]
    public async Task ProviderRefusesTheCredential_AttemptsExactlyOnce()
    {
        // The other revoked-credential shape: the credential exists and is presented, and the
        // provider says no. What must not happen is a retry loop — the proxy would be generating
        // failed logins against the user's mailbox on their behalf.
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();
        harness.Transport.AcceptCredential = false;

        var client = new PipeDuplex("client");
        var session = harness.NewImapSession();
        var run = session.RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");

        Assert.StartsWith("a1 NO", await client.ReadLineAsync(), StringComparison.Ordinal);
        Assert.Equal(SessionOutcome.BackendUnavailable, await run);
        Assert.Equal(1, harness.Transport.OpenCount);
    }

    [Fact]
    public async Task MessageBytes_AreRelayedVerbatim()
    {
        // Constraint 4: a proxy is not a licence to normalise mail. These bytes are deliberately
        // awkward — a CRLF pair that is not a line ending, a bare LF, a NUL, and high bytes that a
        // normalising layer would re-encode.
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var session = harness.NewImapSession();
        var run = session.RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
        await client.ReadLineAsync();

        byte[] payload =
        [
            (byte)'*', (byte)' ', (byte)'1', (byte)' ', (byte)'F', (byte)'E', (byte)'T', (byte)'C', (byte)'H',
            (byte)' ', (byte)'{', (byte)'1', (byte)'2', (byte)'}', (byte)'\r', (byte)'\n',
            0x00, 0x01, 0xFF, 0xFE, 0x7F, (byte)'=', (byte)'\r', (byte)'\n', (byte)'\n', (byte)'\r',
            0xC3, 0xA9, (byte)'\r', (byte)'\n',
        ];

        var backend = harness.Transport.Channel!;
        await backend.SendRawAsync(payload);

        var received = new byte[payload.Length];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.PeerInput.ReadExactlyAsync(received, cts.Token);

        Assert.Equal(payload, received);

        await client.ClosePeerWriteAsync();
        await run;
    }

    [Fact]
    public async Task NeitherClientPasswordNorBackendCredential_ReachesTheClient()
    {
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var session = harness.NewImapSession();
        var run = session.RunAsync(client, CancellationToken.None);

        var transcript = new StringBuilder();
        transcript.Append(await client.ReadLineAsync());

        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
        transcript.Append(await client.ReadLineAsync());

        await client.ClosePeerWriteAsync();
        await run;

        var text = transcript.ToString();
        Assert.DoesNotContain(ProxyHarness.ClientPassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ProxyHarness.AppPasswordSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ProxyHarness.AccessTokenSecret, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OAuthBackedSession_BehavesIdenticallyToAnAppPasswordSession()
    {
        // <b>The seam test.</b> Two accounts, identical in every way except the discriminator on
        // their stored credential, run through the same session class. If the OAuth migration
        // required changing callers, this is where it would show up: differing outcomes, a different
        // reply to the client, or a driver that had to know which kind it received.
        var appPassword = await RunSessionAsync(useOAuth: false);
        var oauth = await RunSessionAsync(useOAuth: true);

        Assert.Equal(appPassword.ClientReply, oauth.ClientReply);
        Assert.Equal(appPassword.Outcome, oauth.Outcome);
        Assert.StartsWith("a1 OK", oauth.ClientReply, StringComparison.Ordinal);

        // ...and the backend saw the mechanism the credential kind calls for, chosen below the seam.
        Assert.Contains("AUTHENTICATE PLAIN", appPassword.BackendAuthCommand, StringComparison.Ordinal);
        Assert.Contains("AUTHENTICATE XOAUTH2", oauth.BackendAuthCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OAuthBackedSession_PresentsTheAccessTokenNotTheClientPassword()
    {
        var harness = new ProxyHarness();
        await harness.EnrolOAuthAccountAsync();

        var client = new PipeDuplex("client");
        var session = harness.NewImapSession();
        var run = session.RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
        await client.ReadLineAsync();

        var authCommand = harness.Transport.LastAuthArgument!;
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authCommand));

        Assert.Contains(ProxyHarness.AccessTokenSecret, decoded, StringComparison.Ordinal);
        Assert.DoesNotContain(ProxyHarness.ClientPassword, decoded, StringComparison.Ordinal);
        Assert.DoesNotContain(ProxyHarness.RefreshTokenSecret, decoded, StringComparison.Ordinal);

        await client.ClosePeerWriteAsync();
        await run;
    }

    [Fact]
    public async Task PreAuthenticationCommands_GetAnAnswerRatherThanSilence()
    {
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var session = harness.NewImapSession();
        var run = session.RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();

        await client.SendAsync("a1 CAPABILITY\r\n");
        var capability = await client.ReadLineAsync();
        Assert.StartsWith("* CAPABILITY", capability, StringComparison.Ordinal);
        Assert.StartsWith("a1 OK", await client.ReadLineAsync(), StringComparison.Ordinal);

        // A command that is not permitted before authentication is refused, not ignored.
        await client.SendAsync("a2 SELECT INBOX\r\n");
        Assert.StartsWith("a2 BAD", await client.ReadLineAsync(), StringComparison.Ordinal);

        await client.ClosePeerWriteAsync();
        await run;
    }

    [Fact]
    public async Task MalformedTag_IsRefusedRatherThanEchoedBack()
    {
        // A tag is the one piece of client-supplied text the proxy echoes. An unvalidated one lets
        // a client inject a response line into the stream the proxy writes.
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var session = harness.NewImapSession();
        var run = session.RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();

        // "a]1" is not a legal IMAP atom, so it cannot be echoed back as a response tag.
        await client.SendAsync("a]1 LOGIN someone secret\r\n");

        Assert.Equal(SessionOutcome.ProtocolError, await run);
        Assert.Equal(0, harness.Transport.OpenCount);
    }

    [Fact]
    public async Task ClientThatNeverSends_EndsAsADisconnectNotAHang()
    {
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        await client.ClosePeerWriteAsync();

        var outcome = await harness.NewImapSession().RunAsync(client, CancellationToken.None);

        Assert.Equal(SessionOutcome.ClientDisconnected, outcome);
    }

    private static async Task<(string ClientReply, SessionOutcome Outcome, string BackendAuthCommand)> RunSessionAsync(
        bool useOAuth)
    {
        var harness = new ProxyHarness();

        if (useOAuth)
        {
            await harness.EnrolOAuthAccountAsync();
        }
        else
        {
            await harness.EnrolAppPasswordAccountAsync();
        }

        var client = new PipeDuplex("client");
        var session = harness.NewImapSession();
        var run = session.RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
        var reply = await client.ReadLineAsync();

        var authCommand = harness.Transport.ReceivedCommands
            .FirstOrDefault(c => c.StartsWith("S1 AUTHENTICATE", StringComparison.Ordinal)) ?? string.Empty;

        await client.ClosePeerWriteAsync();
        var outcome = await run;

        return (reply, outcome, authCommand);
    }
}
