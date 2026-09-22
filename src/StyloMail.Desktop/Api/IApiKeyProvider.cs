namespace StyloMail.Desktop.Api;

/// <summary>
/// Where the console's API key comes from.
/// </summary>
/// <remarks>
/// A seam rather than a string field, for two reasons that are both about the
/// key never leaking:
///
/// <para>
/// The real implementation reads the platform keychain (spec 10.3 forbids a
/// config file, a log, and a view). A seam lets every other test run without a
/// keychain and without a real operator's credential, so the network behaviour
/// can be tested on its own.
/// </para>
///
/// <para>
/// It returns <see cref="string"/> rather than handing back some wrapper the
/// caller could format. There is exactly one place that puts the value into a
/// header, and nothing that could accidentally include it in a message.
/// </para>
/// </remarks>
public interface IApiKeyProvider
{
    /// <summary>
    /// The key to present, or <see langword="null"/> when none is configured.
    /// </summary>
    /// <remarks>
    /// Null is a distinct answer from the empty string and from a wrong key,
    /// and the caller treats it as such: no request is sent at all. Sending an
    /// unauthenticated request to discover that we had nothing to authenticate
    /// with would be a request the Host is right to refuse and that tells an
    /// operator nothing they did not already know.
    /// </remarks>
    ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default);
}
