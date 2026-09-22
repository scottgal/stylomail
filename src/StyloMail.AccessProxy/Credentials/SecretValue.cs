using System.Text;

namespace StyloMail.AccessProxy.Credentials;

/// <summary>
/// A piece of credential material that refuses to render itself.
/// </summary>
/// <remarks>
/// <b>Redaction is a type property, not a discipline.</b> "Never log a credential" is easy to state
/// and easy to break, because the leak is almost never a deliberate <c>Log(password)</c>, it is a
/// record's generated <c>ToString</c> inside an interpolated string, an object dumped into a
/// structured log, or an exception message built with <c>$"...{token}..."</c>. A <see cref="string"/>
/// offers no protection in any of those cases: it is printable, it satisfies every format parameter,
/// and nothing warns you.
///
/// <para>
/// This type exists so that the credential has no path into a log at all. It carries bytes, and
/// <see cref="ToString"/> returns a redaction marker rather than the value. There is deliberately
/// no implicit conversion to <see cref="string"/> and no <see cref="IFormattable"/> implementation,
/// so interpolating one produces the marker. Reaching the value requires calling
/// <see cref="Utf8"/> explicitly, which is a visible thing to do in review, and each call site is
/// therefore a place a reviewer can ask "why does this line need the plaintext?".
/// </para>
///
/// <para>
/// The bytes are zeroed on dispose where the caller honours <see cref="IDisposable"/>, which
/// narrows the window in which a crash dump or a swap file could hold the credential.
/// </para>
/// </remarks>
public sealed class SecretValue : IDisposable
{
    private byte[]? _bytes;

    private SecretValue(byte[] bytes) => _bytes = bytes;

    /// <summary>Wraps UTF-8 text as a secret. The source string is not held.</summary>
    public static SecretValue FromUtf8(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new SecretValue(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>Wraps raw bytes as a secret, taking ownership of the array.</summary>
    public static SecretValue FromBytes(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new SecretValue(value);
    }

    /// <summary>
    /// The credential bytes. This is the only member that exposes the value, and calling it is the
    /// reviewable act of deliberately handling a plaintext credential.
    /// </summary>
    public ReadOnlySpan<byte> Utf8
    {
        get
        {
            ObjectDisposedException.ThrowIf(_bytes is null, this);
            return _bytes;
        }
    }

    /// <summary>Number of bytes held. Safe to log, it is a length, not content.</summary>
    public int Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_bytes is null, this);
            return _bytes.Length;
        }
    }

    /// <summary>Always a redaction marker. See the type remarks for why this is load-bearing.</summary>
    public override string ToString() => "[redacted]";

    /// <summary>
    /// Whether this secret's text appears inside <paramref name="text"/>.
    /// </summary>
    /// <remarks>
    /// <b>This exists to catch a leak we do not control.</b> Every credential path in this project
    /// is written not to put the value in a message, but one of them hands an exception from
    /// somewhere else onward, the OAuth token endpoint, and that message is outside our authorship.
    /// A single careless <c>throw new HttpRequestException($"failed for {token}")</c> inside an
    /// implementation of that seam would put a refresh token into an exception chain, and exception
    /// chains are logged in full by every framework there is. Rather than trusting the seam's
    /// implementations to be careful, the caller checks: knowing the exact secret bytes means we can
    /// ask directly whether a message contains it.
    ///
    /// <para>
    /// The comparison decodes into a stack buffer rather than materialising a <see cref="string"/>,
    /// because a string here would reintroduce the very problem the type exists to avoid. If the
    /// value cannot be decoded it reports true, the caller drops diagnostic detail it might have
    /// kept, which is the right way round for this particular question.
    /// </para>
    /// </remarks>
    public bool AppearsIn(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var bytes = Utf8;

        // UTF-8 never needs more characters than bytes, so this buffer is always sufficient.
        Span<char> chars = stackalloc char[bytes.Length];
        if (!System.Text.Encoding.UTF8.TryGetChars(bytes, chars, out var written))
        {
            return true;
        }

        return text.AsSpan().Contains(chars[..written], StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether this secret appears anywhere in an exception's message chain.
    /// </summary>
    /// <remarks>
    /// Walks the whole chain, including aggregate exceptions, because that is what gets logged.
    /// </remarks>
    public bool AppearsIn(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (AppearsIn(current.Message))
            {
                return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (AppearsIn(inner))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_bytes is null)
        {
            return;
        }

        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_bytes);
        _bytes = null;
    }
}
