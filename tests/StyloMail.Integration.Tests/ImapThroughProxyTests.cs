using MailKit.Net.Imap;
using StyloMail.AccessProxy.Sessions;
using MailKit.Security;

namespace StyloMail.Integration.Tests;

/// <summary>
/// The client access proxy with a real client on one side and a real server on the other.
/// </summary>
/// <remarks>
/// <para>
/// The in-memory suite proves the state machine. This proves the bytes: a client library this
/// project does not control, speaking the dialect over a socket, against a server this project does
/// not control, with our parser in the middle. Both hand-written halves are therefore on trial at
/// once, the server side that faces MailKit and the client side that faces Dovecot.
/// </para>
/// <para>
/// <b>The backend is Dovecot, not GreenMail, and that is a measured choice.</b> The app-password
/// provider authenticates IMAP with SASL PLAIN. GreenMail 2.1.14 advertises only
/// <c>AUTH=XOAUTH2</c>, so every session fails closed against it:
/// </para>
/// <code>
/// * CAPABILITY IMAP4rev1 LITERAL+ UIDPLUS SORT IDLE MOVE SASL-IR AUTH=XOAUTH2 QUOTA
/// a1 NO AUTHENTICATE failed. Unsupported authentication mechanism 'PLAIN'
/// </code>
/// <para>
/// Dovecot advertises <c>AUTH=PLAIN</c> and accepts the inline initial response our connector sends.
/// POP3 still runs against GreenMail, because that provider frames POP3 as the <c>USER</c> and
/// <c>PASS</c> pair and GreenMail accepts those.
/// </para>
/// </remarks>
public sealed class ImapThroughProxyTests
{
    /// <summary>
    /// Why this test does not yet reach the backend, measured and reported.
    /// </summary>
    /// <remarks>
    /// The backend is no longer the problem: Dovecot advertises AUTH=PLAIN and authenticates when
    /// driven by hand. What fails now is the proxy's client-facing IMAP parser, on the form of
    /// AUTHENTICATE that MailKit actually uses.
    /// </remarks>
    internal const string BlockedByClientSaslChallengeForm =
        "Blocked: the proxy returns ProtocolError and drops the connection when a real client uses "
        + "the challenge/response form of AUTHENTICATE. MailKit sends two lines, "
        + "\"A00000000 AUTHENTICATE PLAIN\" with no initial response, then the base64 payload on the "
        + "next line after the proxy's continuation. The SASL-IR form, where the payload rides on the "
        + "command line, is covered by the unit suite and passes; this form is not. Reported to "
        + "overview-. Replace this attribute with [HarnessFact] once the proxy is fixed.";

    [HarnessFact]
    public async Task A_real_imap_client_reaches_the_backend_through_the_proxy()
    {
        await using var backend = await DovecotServer.StartAsync();

        var harness = new IntegrationHarness(
            new TcpBackendTransport(DovecotServer.Host, backend.ImapPort));

        await harness.EnrolAsync(DovecotServer.Login, DovecotServer.AppPassword);

        // Captured so a failure names the proxy's own verdict. MailKit reports "the server has
        // unexpectedly disconnected" for several very different causes, and the session outcome is
        // what tells them apart: ClientRejected means the proxy refused the client, BackendUnavailable
        // means the backend refused the proxy, and ProtocolError means the client-side parser
        // rejected a command MailKit considers valid.
        var outcome = SessionOutcome.Relayed;
        var recorded = default(RecordingDuplex);

        await using var proxy = ProxyListener.Start(async (channel, token) =>
        {
            // Not disposed here: ProxyListener owns the channel and disposing the wrapper
            // would close the socket underneath it.
            var tap = new RecordingDuplex(channel);
            recorded = tap;
            outcome = await harness.NewImapSession().RunAsync(tap, token);
            return outcome;
        });

        using var client = new ImapClient();
        await client.ConnectAsync(DovecotServer.Host, proxy.Port, SecureSocketOptions.None);

        // The whole point of the feature: a client that can only speak LOGIN user pass gets a
        // working session against a backend that no longer accepts that.
        try
        {
            await client.AuthenticateAsync(IntegrationHarness.ClientLogin, IntegrationHarness.ClientPassword);
        }
        catch (Exception ex)
        {
            Assert.Fail(
                $"Client authentication failed. Proxy session outcome was {outcome}. "
                + $"{ex.GetType().Name}: {ex.Message}\n"
                + $"\nClient sent: {recorded?.FromClient ?? "<none>"}"
                + $"\nProxy sent back: {recorded?.ToClient ?? "<none>"}");
        }

        Assert.True(client.IsAuthenticated);

        await client.DisconnectAsync(true);
    }
}
