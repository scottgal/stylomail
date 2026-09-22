using StyloMail.AccessProxy.Backends;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.Integration.Tests;

/// <summary>
/// Wraps a backend transport so both directions of the backend dialogue can be read back.
/// </summary>
/// <remarks>
/// The client side was instrumented first and turned out to be innocent. The same visibility is
/// needed on the backend leg, where a hand-driven session and our connector disagree about the same
/// exchange and only the bytes can say which of them is seeing something different.
/// </remarks>
internal sealed class RecordingTransport : IBackendTransport
{
    private readonly IBackendTransport _inner;

    internal RecordingTransport(IBackendTransport inner) => _inner = inner;

    internal RecordingDuplex? LastChannel { get; private set; }

    public async ValueTask<IDuplexChannel> OpenAsync(
        BackendConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var channel = await _inner.OpenAsync(request, cancellationToken).ConfigureAwait(false);
        LastChannel = new RecordingDuplex(channel);
        return LastChannel;
    }
}
