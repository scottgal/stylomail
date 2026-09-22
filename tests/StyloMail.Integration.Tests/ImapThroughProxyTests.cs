using MailKit.Net.Imap;
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
    [HarnessFact]
    public async Task A_real_imap_client_reaches_the_backend_through_the_proxy()
    {
        await using var backend = await DovecotServer.StartAsync();

        var harness = new IntegrationHarness(
            new TcpBackendTransport(DovecotServer.Host, backend.ImapPort));

        await harness.EnrolAsync(DovecotServer.Login, DovecotServer.AppPassword);

        await using var proxy = ProxyListener.Start((channel, token) =>
            harness.NewImapSession().RunAsync(channel, token));

        using var client = new ImapClient();
        await client.ConnectAsync(DovecotServer.Host, proxy.Port, SecureSocketOptions.None);

        // The whole point of the feature: a client that can only speak LOGIN user pass gets a
        // working session against a backend that no longer accepts that.
        await client.AuthenticateAsync(IntegrationHarness.ClientLogin, IntegrationHarness.ClientPassword);

        Assert.True(client.IsAuthenticated);

        await client.DisconnectAsync(true);
    }
}
