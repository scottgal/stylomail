using StyloMail.Desktop.Api;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// An API key holder that is not the platform keychain.
/// </summary>
/// <remarks>
/// The real implementation reads the macOS keychain, which a test process must
/// never touch: it would depend on a developer's login state, could read a real
/// operator key, and on CI would simply fail. Key retrieval is a seam precisely
/// so that the network-facing behaviour can be tested without one.
/// </remarks>
internal sealed class TestApiKeyProvider : IApiKeyProvider
{
    /// <summary>A key that is recognisable in a failure message without being real.</summary>
    public const string SampleKey = "test-key-0123456789abcdef";

    private readonly string? _key;

    public TestApiKeyProvider(string? key = SampleKey) => _key = key;

    public ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_key);
}
