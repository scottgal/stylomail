using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Sessions;
using StyloMail.AccessProxy.Tests.Support;

namespace StyloMail.AccessProxy.Tests;

public sealed class AdmissionOwnershipTests
{
    [Fact]
    public async Task Real_tls_imap_handoff_cannot_spend_the_same_admission_twice()
    {
        var bounds = new AccessProxyBounds
        {
            MaxConcurrentSessions = 1, MaxSessionsPerAccount = 1, AuthenticationTimeout = TimeSpan.FromSeconds(3),
        };
        var harness = new ProxyHarness(bounds);
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        var tlsOptions = new TlsServerOptions { ServerCertificate = certificate };
        var greetingReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<(bool SecondWasPending, bool Cancelled, int Active)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<SessionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listener = new TcpAccessProxyListener(new IPEndPoint(IPAddress.Loopback, 0), ProxyHarness.TenantId,
            (tenant, raw, token) => ProxySessionRunnerFactory.RunWithTlsAsync(ProxyListenerProtocol.Imap,
                tlsOptions, bounds, TimeProvider.System, async (_, secure, sessionToken) =>
                {
                    var firstSession = harness.NewImapSession().RunAsync(secure, sessionToken);
                    // The client confirms receipt of the real IMAP greeting, proving that the real
                    // session has already consumed the admission through the secure channel view.
                    await greetingReceived.Task.WaitAsync(sessionToken);
                    using var cancellation = new CancellationTokenSource();
                    var second = harness.Limiter.AcquireSessionAsync(secure, cancellation.Token);
                    var secondWasPending = !second.IsCompleted;
                    cancellation.Cancel();
                    var cancelled = false;
                    try { using var duplicate = await second; }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
                    observed.TrySetResult((secondWasPending, cancelled, harness.Limiter.ActiveSessions));
                    var outcome = await firstSession;
                    finished.TrySetResult(outcome);
                    return outcome;
                }, tenant, raw, token), harness.Limiter);
        await listener.StartAsync(CancellationToken.None);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);
        await using var tls = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, deadline.Token);
        using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
        using var writer = new StreamWriter(tls, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        Assert.StartsWith("* OK", await reader.ReadLineAsync(deadline.Token));
        greetingReceived.SetResult();
        var observation = await observed.Task.WaitAsync(deadline.Token);
        // Assert outside the listener callback: its exception isolation must not swallow a test failure.
        Assert.True(observation.SecondWasPending, "A second borrow spent an already-consumed connection admission.");
        Assert.True(observation.Cancelled);
        Assert.Equal(1, observation.Active);
        await writer.WriteLineAsync("a1 LOGOUT");
        Assert.StartsWith("* BYE", await reader.ReadLineAsync(deadline.Token));
        Assert.StartsWith("a1 OK", await reader.ReadLineAsync(deadline.Token));
        Assert.Equal(SessionOutcome.ClientDisconnected, await finished.Task.WaitAsync(deadline.Token));
        while (harness.Limiter.ActiveSessions != 0) await Task.Delay(10, deadline.Token);
    }
}
