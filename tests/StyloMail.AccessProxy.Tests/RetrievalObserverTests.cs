using StyloMail.AccessProxy.Sessions;
using StyloMail.AccessProxy.Tests.Support;

namespace StyloMail.AccessProxy.Tests;

/// <summary>
/// Constraint 5: assessment of retrieved messages is optional and non-blocking.
/// </summary>
/// <remarks>
/// The property that matters is not "assessment works", it is that <em>a client fetching mail is
/// never held hostage by the semantic provider</em>. These tests are about the absence of coupling,
/// which is why several of them assert that something did not happen.
/// </remarks>
public sealed class RetrievalObserverTests
{
    [Fact]
    public async Task WithNoObserverConfigured_TheSessionRelaysNormally()
    {
        // The default deployment. Assessment is off, and the retrieval path is unaffected by that.
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var client = new PipeDuplex("client");
        var run = harness.NewImapSession(observer: null).RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
        Assert.StartsWith("a1 OK", await client.ReadLineAsync(), StringComparison.Ordinal);

        byte[] message = [(byte)'*', (byte)' ', (byte)'1', (byte)' ', (byte)'O', (byte)'K', (byte)'\r', (byte)'\n'];
        await harness.Transport.Channel!.SendRawAsync(message);

        var received = new byte[message.Length];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.PeerInput.ReadExactlyAsync(received, cts.Token);

        Assert.Equal(message, received);

        await client.ClosePeerWriteAsync();
        await run;
    }

    [Fact]
    public async Task TheNullObserver_ObservesNothing()
    {
        // Named rather than null so a deployment can tell "assessment is off" from "never wired".
        var observer = NullRetrievalObserver.Instance;

        observer.Observe([1, 2, 3, 4]);

        await Task.CompletedTask;
        Assert.Same(NullRetrievalObserver.Instance, observer);
    }

    [Fact]
    public async Task ConfiguredObserver_SeesExactlyTheBytesTheClientReceives()
    {
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var observer = new RecordingRetrievalObserver();
        var client = new PipeDuplex("client");
        var run = harness.NewImapSession(observer).RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
        await client.ReadLineAsync();

        byte[] message = [(byte)'*', (byte)' ', (byte)'9', (byte)' ', (byte)'F', (byte)'E', (byte)'T', (byte)'C', (byte)'H', (byte)'\r', (byte)'\n'];
        await harness.Transport.Channel!.SendRawAsync(message);

        var received = new byte[message.Length];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.PeerInput.ReadExactlyAsync(received, cts.Token);

        // Give the pump a moment to hand the chunk to the observer, which happens before the write.
        var observed = await WaitForObservationAsync(observer, message.Length);

        Assert.Equal(message, observed);

        await client.ClosePeerWriteAsync();
        await run;
    }

    [Fact]
    public async Task ObserverDoesNotSeeClientToBackendBytes()
    {
        // The tap is on retrieval. A client's own commands are not "retrieved messages", and a tap
        // that saw both directions would be handing a future assessment hook the user's credentials
        // along with their mail.
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var observer = new RecordingRetrievalObserver();
        var client = new PipeDuplex("client");
        var run = harness.NewImapSession(observer).RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
        await client.ReadLineAsync();

        // A command the client sends after authentication, which the relay forwards upstream.
        await client.SendAsync("a2 SELECT INBOX\r\n");

        await Task.Delay(200);

        Assert.DoesNotContain(
            ProxyHarness.ClientPassword,
            System.Text.Encoding.UTF8.GetString(observer.Observed),
            StringComparison.Ordinal);

        await client.ClosePeerWriteAsync();
        await run;
    }

    [Fact]
    public async Task AnObserverThatThrows_DoesNotTakeTheSessionDown()
    {
        // The documented contract says an observer must not throw. A documented contract is not an
        // enforced one, and the failure mode if it were merely documented is a user's mailbox dying
        // because a semantic classifier had a bad day.
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var observer = new ThrowingRetrievalObserver();
        var client = new PipeDuplex("client");
        var run = harness.NewImapSession(observer).RunAsync(client, CancellationToken.None);

        await client.ReadLineAsync();
        await client.SendAsync($"a1 LOGIN {ProxyHarness.Login} {ProxyHarness.ClientPassword}\r\n");
        await client.ReadLineAsync();

        byte[] message = [(byte)'*', (byte)' ', (byte)'1', (byte)' ', (byte)'O', (byte)'K', (byte)'\r', (byte)'\n'];
        await harness.Transport.Channel!.SendRawAsync(message);

        // The bytes still arrive, despite the observer failing on every chunk.
        var received = new byte[message.Length];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.PeerInput.ReadExactlyAsync(received, cts.Token);

        Assert.Equal(message, received);
        Assert.True(observer.CallCount > 0, "The observer was never offered anything, so this proved nothing.");

        await client.ClosePeerWriteAsync();
        Assert.Equal(SessionOutcome.Relayed, await run);
    }

    [Fact]
    public void TheObserverContractIsSynchronous_SoTheRelayStructurallyCannotAwaitIt()
    {
        // "Non-blocking" is enforced by the signature rather than by good intentions: Observe returns
        // void, so there is no Task for the relay to await and no way for it to wait on a provider.
        // If this ever becomes Task-returning, the relay gains the ability to block a user's mailbox
        // on a semantic provider, which is the exact coupling the brief forbids. The assertion is a
        // tripwire for that change, not a style preference.
        var method = typeof(IRetrievalObserver).GetMethod(nameof(IRetrievalObserver.Observe));

        Assert.NotNull(method);
        Assert.Equal(typeof(void), method!.ReturnType);
    }

    private static async Task<byte[]> WaitForObservationAsync(RecordingRetrievalObserver observer, int expectedLength)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var seen = observer.Observed;
            if (seen.Length >= expectedLength)
            {
                return seen;
            }

            await Task.Delay(20);
        }

        return observer.Observed;
    }
}
