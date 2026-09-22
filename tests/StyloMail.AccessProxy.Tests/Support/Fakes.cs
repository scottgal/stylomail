using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.AccessProxy.Tests.Support;

/// <summary>
/// A token service that never touches the network.
/// </summary>
/// <remarks>
/// The OAuth path is fully implemented and exercised, but the exchange itself is faked here — which
/// is the point of <see cref="IOAuthTokenEndpoint"/> being a seam of its own. It means the OAuth
/// credential kind can be tested end to end, including its failure modes, with no Google account
/// and no outbound request anywhere in the suite.
/// </remarks>
internal sealed class FakeOAuthTokenEndpoint : IOAuthTokenEndpoint
{
    private int _callCount;

    /// <summary>How many times an exchange was attempted. Used for the fail-closed assertions.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>When set, the exchange throws instead of succeeding.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>The refresh token the endpoint was handed. Asserted on for framing, never logged.</summary>
    public string? LastRefreshToken { get; private set; }

    public ValueTask<SecretValue> ExchangeRefreshTokenAsync(
        OAuthRefreshRequest request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        cancellationToken.ThrowIfCancellationRequested();

        LastRefreshToken = System.Text.Encoding.UTF8.GetString(request.RefreshToken.Utf8);

        if (FailWith is not null)
        {
            throw FailWith;
        }

        return ValueTask.FromResult(SecretValue.FromUtf8(ProxyHarness.AccessTokenSecret));
    }
}

/// <summary>Records what the relay let past, so the tap can be asserted on.</summary>
internal sealed class RecordingRetrievalObserver : IRetrievalObserver
{
    private readonly List<byte> _bytes = [];

    public int CallCount { get; private set; }

    public byte[] Observed
    {
        get
        {
            lock (_bytes)
            {
                return [.. _bytes];
            }
        }
    }

    public void Observe(ReadOnlySpan<byte> bytes)
    {
        CallCount++;
        lock (_bytes)
        {
            _bytes.AddRange(bytes);
        }
    }
}

/// <summary>
/// An observer that misbehaves, to prove the tap cannot take the session down with it.
/// </summary>
/// <remarks>
/// The relay documents that an observer must not throw. A contract that is only documented is a
/// contract that will eventually be broken by someone wiring up a real assessment hook — so the
/// behaviour when it happens is worth pinning down rather than leaving to chance.
/// </remarks>
internal sealed class ThrowingRetrievalObserver : IRetrievalObserver
{
    public int CallCount { get; private set; }

    public void Observe(ReadOnlySpan<byte> bytes)
    {
        CallCount++;
        throw new InvalidOperationException("Observer failed.");
    }
}
