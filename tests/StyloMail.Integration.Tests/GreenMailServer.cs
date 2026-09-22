using MailKit.Net.Imap;
using MailKit.Security;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace StyloMail.Integration.Tests;

/// <summary>
/// A throwaway mail server, real enough to speak IMAP, POP3 and SMTP over a socket.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authentication is deliberately left enabled.</b> The standalone image disables it by default
/// through <c>GREENMAIL_OPTS</c>, and a backend that accepts any password cannot tell the harness
/// whether StyloMail presented the credential it resolved, which is the entire point of the
/// credential seam. So the options string is replaced rather than appended to.
/// </para>
/// <para>
/// <b>The ports are random and mapped.</b> A fixed port makes two runs collide, and a container that
/// fails because something else holds 3025 is a test failure that describes the machine rather than
/// the code.
/// </para>
/// </remarks>
public sealed class GreenMailServer : IAsyncDisposable
{
    /// <summary>The login the harness backend knows, as an address at the example domain.</summary>
    public const string Login = "alice@example.com";

    /// <summary>The backend's own password, which StyloMail must resolve and present.</summary>
    public const string AppPassword = "backend-app-password-4b7c";

    private readonly IContainer _container;

    private GreenMailServer(IContainer container) => _container = container;

    /// <summary>
    /// A host that only ever means the container, so no test has to think about it.
    /// </summary>
    /// <remarks>
    /// Static because it is a constant: the container is always reached over the loopback address of
    /// the machine running the tests, whatever port it was mapped to. The plan wrote this as an
    /// instance member, which CA1822 rejects here, and analyzers are errors in this repository.
    /// </remarks>
    public static string Host => "127.0.0.1";

    public ushort ImapPort => _container.GetMappedPublicPort(3143);

    public ushort Pop3Port => _container.GetMappedPublicPort(3110);

    public ushort SmtpPort => _container.GetMappedPublicPort(3025);

    public static async Task<GreenMailServer> StartAsync()
    {
        // -Dgreenmail.users=<login>:<password> creates that account, which is how a backend acquires
        // a user without a test signing up over the wire first.
        //
        // The login is the FULL ADDRESS, and that is not cosmetic. GreenMail's other form,
        // "-Dgreenmail.users=alice:pwd@example.com", makes the login id the local part only: it
        // accepts "alice" and rejects "alice@example.com". Measured against 2.1.14 (the address form
        // returns "a1 OK LOGIN completed", the local-part form returns "a1 NO LOGIN failed").
        //
        // It matters here because the proxy resolves the backend credential with the account's
        // backend username, which for this harness is the full address, and presents exactly that.
        // Configuring the local-part form would make every proxy test fail at authentication for a
        // reason that looks like a credential seam defect but is a harness configuration mistake.
        //
        // Built from the constants rather than repeating the literals, so the account the container
        // creates and the account the tests authenticate as cannot drift apart.
        const string Options =
            "-Dgreenmail.setup.test.all -Dgreenmail.hostname=0.0.0.0 "
            + "-Dgreenmail.users=" + Login + ":" + AppPassword;

        // The image goes to the constructor: the parameterless ContainerBuilder() is obsolete in
        // Testcontainers 4.15 and warns CS0618.
        var container = new ContainerBuilder("greenmail/standalone:2.1.14")
            .WithEnvironment("GREENMAIL_OPTS", Options)
            .WithPortBinding(3143, assignRandomHostPort: true)
            .WithPortBinding(3110, assignRandomHostPort: true)
            .WithPortBinding(3025, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(3143))
            .Build();

        await container.StartAsync();

        var server = new GreenMailServer(container);
        await server.WaitUntilTheAccountCanAuthenticateAsync();
        return server;
    }

    /// <summary>
    /// Blocks until the configured account can actually log in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The TCP wait strategy is not enough, and this was measured rather than guessed.</b>
    /// <c>UntilInternalTcpPortIsAvailable</c> returns as soon as something accepts a connection on
    /// 3143, which GreenMail does before it has finished creating the user from
    /// <c>-Dgreenmail.users</c>. A test that connects in that window gets
    /// <c>AuthenticationException: Invalid login/password for user id alice@example.com</c>, which
    /// reads like a credential defect and is a startup race.
    /// </para>
    /// <para>
    /// <b>It was intermittent, which is why it survived the first reports.</b> Measured over ten
    /// consecutive runs, nine failed, and the two GreenMail-backed tests failed in different
    /// combinations. A single green run proves nothing about this fixture.
    /// </para>
    /// <para>
    /// <b>Dovecot does not need the equivalent</b> because its shipped <c>auth.conf</c> uses a static
    /// passdb: there is no account to create, so there is no window.
    /// </para>
    /// </remarks>
    private async Task WaitUntilTheAccountCanAuthenticateAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new ImapClient();
                await client.ConnectAsync(Host, ImapPort, SecureSocketOptions.None);
                await client.AuthenticateAsync(Login, AppPassword);
                await client.DisconnectAsync(true);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
            }

            await Task.Delay(200);
        }

        throw new InvalidOperationException(
            $"GreenMail did not accept '{Login}' within 30 seconds of the container starting. "
            + $"Last error: {last?.GetType().Name}: {last?.Message}");
    }

    public ValueTask DisposeAsync() => new(_container.DisposeAsync().AsTask());
}
