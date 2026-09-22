using MailKit.Net.Pop3;
using MailKit.Security;

namespace StyloMail.Integration.Tests;

/// <summary>
/// The client access proxy with a real POP3 client on one side and a real server on the other.
/// </summary>
/// <remarks>
/// <para>
/// This is the same shape as the IMAP test and is expected to reach the backend where IMAP could
/// not, for a reason worth stating: the app-password provider frames POP3 as the <c>USER</c> and
/// <c>PASS</c> pair rather than as a SASL exchange, because SASL support in POP3 is uneven. GreenMail
/// accepts those two commands, so the path the operator ships today is exercised end to end here.
/// </para>
/// <para>
/// The IMAP equivalent stops at the backend's mechanism list, which offers <c>AUTH=XOAUTH2</c> and
/// nothing else. That difference is a property of the mechanism each protocol chose, not of the
/// protocol itself, and it is recorded rather than smoothed over.
/// </para>
/// </remarks>
public sealed class Pop3ThroughProxyTests
{
    [HarnessFact]
    public async Task A_real_pop3_client_reaches_the_backend_through_the_proxy()
    {
        await using var backend = await GreenMailServer.StartAsync();

        var harness = new IntegrationHarness(
            new TcpBackendTransport(GreenMailServer.Host, backend.Pop3Port));

        await harness.EnrolAsync(GreenMailServer.Login, GreenMailServer.AppPassword);

        await using var proxy = ProxyListener.Start((channel, token) =>
            harness.NewPop3Session().RunAsync(channel, token));

        using var client = new Pop3Client();
        await client.ConnectAsync(GreenMailServer.Host, proxy.Port, SecureSocketOptions.None);

        await client.AuthenticateAsync(IntegrationHarness.ClientLogin, IntegrationHarness.ClientPassword);

        Assert.True(client.IsAuthenticated);

        await client.DisconnectAsync(true);
    }
}
