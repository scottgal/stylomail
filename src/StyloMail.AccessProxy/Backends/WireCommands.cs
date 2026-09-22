using System.Buffers;
using System.Text;
using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.AccessProxy.Backends;

/// <summary>
/// Builds protocol command lines that contain credential bytes.
/// </summary>
/// <remarks>
/// Every method here exists to keep a secret out of a <see cref="string"/>. The obvious spelling of
/// these commands, <c>$"A1 LOGIN \"{user}\" \"{password}\""</c>, is wrong for a reason that is
/// easy to miss: <see cref="string"/> is immutable, so that copy of the password cannot be zeroed
/// and survives in managed memory until the garbage collector happens to run. On a mail session
/// that stays open for hours, "until the GC runs" is not a bound anyone can state.
///
/// <para>
/// Building on <see cref="ArrayBufferWriter{T}"/> keeps the secret in a buffer the caller owns, so
/// the caller can clear it as soon as the line is on the wire.
/// </para>
/// </remarks>
internal static class WireCommands
{
    /// <summary>
    /// Builds an IMAP <c>LOGIN</c> command with quoted-string arguments.
    /// </summary>
    /// <remarks>
    /// <b>CR and LF in a credential are rejected, not escaped.</b> Neither can appear in a
    /// legitimate username or password, and permitting them would let a credential inject an
    /// arbitrary second command into the backend dialogue, the classic header-injection shape,
    /// pointed at a protocol instead of a message. Escaping would be the wrong fix: the protocol
    /// has no representation for them, so the honest response is to refuse.
    /// </remarks>
    internal static byte[] ImapLogin(string tag, ReadOnlySpan<byte> user, ReadOnlySpan<byte> password)
    {
        var writer = new ArrayBufferWriter<byte>(user.Length + password.Length + 32);

        WriteAscii(writer, tag);
        WriteAscii(writer, " LOGIN "u8);
        WriteImapQuoted(writer, user, "username");
        writer.Write(" "u8);
        WriteImapQuoted(writer, password, "password");

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>Builds a command whose argument is a single raw token, e.g. POP3 <c>USER</c>/<c>PASS</c>.</summary>
    internal static byte[] VerbWithToken(string verb, SecretValue token)
    {
        var tokenBytes = token.Utf8;
        RejectControlCharacters(tokenBytes, "credential token");

        var writer = new ArrayBufferWriter<byte>(tokenBytes.Length + verb.Length + 2);
        WriteAscii(writer, verb);
        WriteAscii(writer, " "u8);
        writer.Write(tokenBytes);

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>Base64-encodes a secret for a SASL exchange, as ASCII.</summary>
    internal static byte[] Base64(SecretValue value)
        => Encoding.ASCII.GetBytes(Convert.ToBase64String(value.Utf8));

    /// <summary>Joins an ASCII command prefix to raw bytes, without an intermediate string.</summary>
    internal static byte[] Concatenate(string prefix, ReadOnlySpan<byte> tail)
    {
        var head = Encoding.ASCII.GetBytes(prefix);
        var combined = new byte[head.Length + tail.Length];
        head.CopyTo(combined, 0);
        tail.CopyTo(combined.AsSpan(head.Length));
        return combined;
    }

    /// <summary>
    /// Decodes a base64 SASL challenge.
    /// </summary>
    /// <remarks>
    /// A malformed challenge is a protocol error rather than a credential failure: the backend broke
    /// its own framing, which says nothing about whether the credential is good, and reporting it as
    /// a rejection would send an operator looking at the wrong thing.
    /// </remarks>
    internal static byte[] DecodeBase64(string text, string description)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var buffer = new byte[(text.Length / 4 + 1) * 3];
        if (!Convert.TryFromBase64String(text, buffer, out var written))
        {
            throw new AccessProxyProtocolException($"The backend sent a malformed {description}.");
        }

        return buffer.AsSpan(0, written).ToArray();
    }

    private static void WriteImapQuoted(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> value, string field)
    {
        RejectControlCharacters(value, field);

        writer.Write("\""u8);
        foreach (var b in value)
        {
            if (b is (byte)'"' or (byte)'\\')
            {
                writer.Write("\\"u8);
            }

            writer.Write([b]);
        }

        writer.Write("\""u8);
    }

    private static void RejectControlCharacters(ReadOnlySpan<byte> value, string field)
    {
        foreach (var b in value)
        {
            if (b is (byte)'\r' or (byte)'\n' or 0)
            {
                throw new AccessProxyProtocolException(
                    $"The {field} contains a character that cannot appear in a protocol line.");
            }
        }
    }

    private static void WriteAscii(ArrayBufferWriter<byte> writer, string value)
        => WriteAscii(writer, Encoding.ASCII.GetBytes(value));

    private static void WriteAscii(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> value)
        => writer.Write(value);
}
