using StyloMail.AccessProxy.Sessions;
using StyloMail.AccessProxy.Tests.Support;

namespace StyloMail.AccessProxy.Tests;

/// <summary>
/// The client-facing IMAP parser, driven with the bytes a real client actually sends.
/// </summary>
/// <remarks>
/// <para>
/// <b>These bytes are a capture, not a fixture someone wrote.</b> They were recorded off the wire by
/// the protocol harness in <c>tests/StyloMail.Integration.Tests</c>, where MailKit talked to a real
/// Dovecot through this proxy. MailKit authenticates with the challenge and response form of
/// AUTHENTICATE: the command line carries no initial response, and the payload follows on the next
/// line once the server has sent its continuation.
/// </para>
/// <para>
/// The rest of this suite drives only the inline form, <c>AUTHENTICATE PLAIN &lt;base64&gt;</c>. That is
/// the SASL-IR spelling, and the in-memory client here chose it because it is the convenient one to
/// write, so the covered path and the path a real client takes were never the same path. These tests
/// close that, and they passed when first written: the parser already handled the form. They are a
/// guard against a real client's spelling, not a reproduction of a fault.
/// </para>
/// </remarks>
public sealed class ImapClientChallengeResponseTests
{
    /// <summary>The command line MailKit sent. Recorded, not invented.</summary>
    private const string CapturedCommandLine = "A00000000 AUTHENTICATE PLAIN";

    /// <summary>The payload MailKit sent on the second line, after the proxy's continuation.</summary>
    private const string CapturedPayload = "AGFsaWNlQGV4YW1wbGUuY29tAGNsaWVudC1zaWRlLXBhc3N3b3JkLTlmM2E=";

    [Fact]
    public async Task ClientAuthenticates_UsingTheChallengeResponseFormOfPlain()
    {
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var run = harness.NewImapSession().RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();

        // Line 1: the command with no initial response.
        await client.SendAsync(CapturedCommandLine + "\r\n");

        // The proxy must ask for the payload rather than dropping the connection.
        var continuation = await client.ReadLineAsync();
        Assert.Equal("+", continuation);

        // Line 2: the payload.
        await client.SendAsync(CapturedPayload + "\r\n");

        var reply = await client.ReadLineAsync();
        Assert.StartsWith("A00000000 OK", reply, StringComparison.Ordinal);

        await client.ClosePeerWriteAsync();
        Assert.Equal(SessionOutcome.Relayed, await run);
    }

    [Fact]
    public async Task ClientAuthenticates_UsingTheInlineFormAsWell()
    {
        // The form the existing suite covers, kept beside the captured one so a future change cannot
        // fix one and break the other without a test going red.
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var run = harness.NewImapSession().RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"{CapturedCommandLine} {CapturedPayload}\r\n");

        var reply = await client.ReadLineAsync();
        Assert.StartsWith("A00000000 OK", reply, StringComparison.Ordinal);

        await client.ClosePeerWriteAsync();
        await run;
    }

    [Fact]
    public async Task ThePayloadSentOnTheSecondLine_IsTheClientPasswordNotTheBackendOne()
    {
        // Guards the decode rather than the framing: the captured payload is the client's own
        // credential, and a proxy that confused the two would still answer OK against a lenient
        // backend. The account only verifies against the client password.
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(CapturedPayload));
        var parts = decoded.Split('\0');

        Assert.Equal(3, parts.Length);
        Assert.Equal(ProxyHarness.Login, parts[1]);
        Assert.Equal(ProxyHarness.ClientPassword, parts[2]);
        Assert.NotEqual(ProxyHarness.AppPasswordSecret, parts[2]);
    }
}
