using MailKit.Net.Imap;
using MailKit.Security;

namespace StyloMail.Integration.Tests;

/// <summary>
/// The harness proving itself before it is used to prove anything else.
/// </summary>
/// <remarks>
/// If this fails, every other test in the project fails for a reason that has nothing to do with
/// StyloMail, so it is worth having one test whose only job is that the container is a mail server.
/// </remarks>
public sealed class GreenMailTests
{
    [HarnessFact]
    public async Task A_real_imap_client_logs_into_the_harness_backend()
    {
        await using var backend = await GreenMailServer.StartAsync();

        using var client = new ImapClient();
        await client.ConnectAsync(GreenMailServer.Host, backend.ImapPort, SecureSocketOptions.None);

        await client.AuthenticateAsync(GreenMailServer.Login, GreenMailServer.AppPassword);

        Assert.True(client.IsAuthenticated);

        var inbox = await client.GetFolderAsync("INBOX");
        Assert.NotNull(inbox);

        await client.DisconnectAsync(true);
    }
}
