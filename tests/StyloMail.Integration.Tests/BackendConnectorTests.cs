using StyloMail.AccessProxy.Backends;
using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.Integration.Tests;

/// <summary>
/// The backend half on its own, with no client in front of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists to bisect.</b> When the full IMAP path fails, the fault is either the client-facing
/// parser or the backend dialogue, and the session collapses both into one `SessionOutcome` because
/// they throw the same exception type. Driving the connector directly removes the client from the
/// picture entirely, so a failure here is the backend and a pass here points at the client.
/// </para>
/// <para>
/// It is worth keeping beyond the bisect. <c>ImapBackendConnector</c> hand-writes the client side of
/// IMAP, and this is the only test that points it at a server nobody in this project wrote.
/// </para>
/// </remarks>
public sealed class BackendConnectorTests
{
    [HarnessFact]
    public async Task TheImapBackendConnectorAuthenticatesAgainstDovecot()
    {
        await using var backend = await DovecotServer.StartAsync();

        var transport = new RecordingTransport(
            new TcpBackendTransport(DovecotServer.Host, backend.ImapPort));

        var harness = new IntegrationHarness(transport);
        await harness.EnrolAsync(DovecotServer.Login, DovecotServer.AppPassword);

        var connector = harness.NewImapConnector();

        IDuplexChannel channel;
        try
        {
            channel = await connector.ConnectAsync(
                new BackendConnectionRequest
                {
                    AccountId = "acct-1",
                    Protocol = BackendProtocol.Imap,
                    TimeProvider = harness.Clock,
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            var tap = transport.LastChannel;
            Assert.Fail(
                $"{ex.GetType().Name}: {ex.Message}\n"
                + $"Connector sent to backend: {tap?.ToClient ?? "<none>"}\n"
                + $"Backend sent to connector: {tap?.FromClient ?? "<none>"}");
            return;
        }

        await channel.DisposeAsync();

        // Reaching here means the greeting was read, the SASL exchange completed and the server
        // accepted the credential the proxy resolved from its own store.

    }

    [HarnessFact]
    public async Task TheImapBackendConnectorReportsARejectedCredentialRatherThanSucceeding()
    {
        await using var backend = await DovecotServer.StartAsync();

        // The stored secret is deliberately wrong, so Dovecot must refuse and the connector must
        // surface that as a rejection rather than handing back an unauthenticated channel.
        var harness = new IntegrationHarness(
            new TcpBackendTransport(DovecotServer.Host, backend.ImapPort));

        await harness.EnrolAsync(DovecotServer.Login, "not-the-backend-password");

        var connector = harness.NewImapConnector();

        await Assert.ThrowsAsync<BackendAuthenticationRejectedException>(async () =>
            await connector.ConnectAsync(
                new BackendConnectionRequest
                {
                    AccountId = "acct-1",
                    Protocol = BackendProtocol.Imap,
                    TimeProvider = harness.Clock,
                },
                CancellationToken.None));
    }
}
