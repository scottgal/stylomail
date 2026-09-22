using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace StyloMail.Integration.Tests;

/// <summary>
/// A second backend, chosen because it advertises <c>AUTH=PLAIN</c> and GreenMail does not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> The proxy's app-password provider authenticates IMAP with SASL
/// PLAIN. GreenMail offers only <c>AUTH=XOAUTH2</c> and the <c>LOGIN</c> command, so the IMAP leg
/// cannot authenticate against it. Dovecot advertises <c>AUTH=PLAIN</c>, which makes the path the
/// operator actually ships testable, and it starts the cross-implementation coverage the plan defers.
/// </para>
/// <para>
/// <b>Pinned to <c>latest</c>, which is 2.4.5, and that is deliberate.</b> The 2.3 line has the
/// better-documented configuration, but <c>dovecot/dovecot:2.3.21</c> is amd64 only and this project
/// runs on an arm64 host, where it dies under Rosetta with <c>unable to mmap ExecutableHeap</c>.
/// 2.4.5 is <c>arm64/linux</c> and native. A tag is not pinned to a digest here, so a future 2.4.x
/// could in principle move; the drop-in config is small enough that a break would be obvious.
/// </para>
/// <para>
/// <b>The config is added to, never replaced.</b> See <c>Dovecot/99-harness.conf</c> for what
/// happens when the image's own config is overwritten.
/// </para>
/// <para>
/// <b>The password arrives by environment variable.</b> The image's shipped <c>auth.conf</c> defines
/// <c>passdb static { password = %{env:USER_PASSWORD} }</c>, so any username authenticates with that
/// one password, which is exactly the shape a test backend wants.
/// </para>
/// </remarks>
public sealed class DovecotServer : IAsyncDisposable
{
    /// <summary>The login the harness presents to the backend. Any value works; this is the realistic one.</summary>
    public const string Login = "alice@example.com";

    /// <summary>The backend's own password, which StyloMail must resolve and present.</summary>
    public const string AppPassword = "backend-app-password-4b7c";

    /// <summary>
    /// The port IMAP listens on inside the container.
    /// </summary>
    /// <remarks>
    /// 31143, not 143. The image runs rootless and its vendor config moves every listener into the
    /// unprivileged range, so 143 is not open at all.
    /// </remarks>
    private const int InternalImapPort = 31143;

    private readonly IContainer _container;

    private DovecotServer(IContainer container) => _container = container;

    /// <summary>A host that only ever means the container.</summary>
    public static string Host => "127.0.0.1";

    public ushort ImapPort => _container.GetMappedPublicPort(InternalImapPort);

    public static async Task<DovecotServer> StartAsync()
    {
        var dropIn = new FileInfo(
            Path.Combine(AppContext.BaseDirectory, "Dovecot", "99-harness.conf"));

        if (!dropIn.Exists)
        {
            throw new InvalidOperationException(
                $"{dropIn.FullName} is missing. It is copied to the output directory by the project "
                + "file; without it Dovecot refuses plaintext authentication and the test would fail "
                + "for a reason that looks like a proxy defect.");
        }

        var container = new ContainerBuilder("dovecot/dovecot:latest")
            .WithEnvironment("USER_PASSWORD", AppPassword)
            // A bind mount, not WithResourceMapping. The resource mapping was accepted without error
            // and the file never reached conf.d, so Dovecot kept its defaults, advertised
            // LOGINDISABLED with no AUTH=PLAIN, and refused cleartext with
            // "NO [PRIVACYREQUIRED] Cleartext authentication disallowed". That failure looks exactly
            // like a proxy defect from the test, and it cost a misdiagnosis: see the note in
            // ImapThroughProxyTests. The bind mount is the form verified against this image by hand.
            .WithBindMount(dropIn.FullName, "/etc/dovecot/conf.d/99-harness.conf")
            .WithPortBinding(InternalImapPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(InternalImapPort))
            .Build();

        await container.StartAsync();
        return new DovecotServer(container);
    }

    public ValueTask DisposeAsync() => new(_container.DisposeAsync().AsTask());
}
