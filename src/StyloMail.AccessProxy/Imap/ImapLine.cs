namespace StyloMail.AccessProxy.Imap;

/// <summary>
/// A minimal reader for the client commands the proxy has to understand.
/// </summary>
/// <remarks>
/// <b>Deliberately not an IMAP parser.</b> The proxy understands exactly three commands —
/// <c>LOGIN</c>, <c>AUTHENTICATE</c> and <c>LOGOUT</c>, plus <c>CAPABILITY</c> and <c>NOOP</c> so a
/// client's pre-login probes get an answer — because those are the only ones that carry or concern
/// a credential. Everything after authentication is bytes, relayed without inspection.
///
/// <para>
/// This is a smaller surface than it looks. A general IMAP parser would have to understand literals,
/// resp-text and the full grammar of FETCH responses, and every one of those is a place where a
/// message could be re-framed in passing. Keeping the parser in front of the relay, and unable to
/// see past authentication, is what makes "we did not rewrite your mail" a property of the code
/// rather than a promise about it.
/// </para>
///
/// <para>
/// Arguments support atoms and quoted strings. Literals (<c>{n}</c>) are <em>not</em> supported and
/// are rejected with <c>BAD</c>: a literal credential is vanishingly rare, and accepting one means
/// reading a length-prefixed byte sequence inside the authentication dialogue, which is complexity
/// this path does not need to carry. A client that uses one gets a clear refusal rather than
/// silent misbehaviour.
/// </para>
/// </remarks>
internal static class ImapLine
{
    /// <summary>Splits a command line into its tag, verb and remaining argument text.</summary>
    internal static bool TrySplit(string line, out string tag, out string verb, out string arguments)
    {
        tag = string.Empty;
        verb = string.Empty;
        arguments = string.Empty;

        var pos = 0;
        if (!TryReadAtom(line, ref pos, out tag))
        {
            return false;
        }

        SkipSpaces(line, ref pos);
        if (!TryReadAtom(line, ref pos, out verb))
        {
            return false;
        }

        SkipSpaces(line, ref pos);
        arguments = line[pos..];
        return true;
    }

    /// <summary>
    /// Reads one astring argument — an atom, or a quoted string with <c>\</c> escapes.
    /// </summary>
    /// <remarks>
    /// Returns false for a literal, and for anything malformed. The caller turns that into a
    /// <c>BAD</c> reply rather than guessing at the intent.
    /// </remarks>
    internal static bool TryReadArgument(string arguments, ref int pos, out string value)
    {
        value = string.Empty;
        SkipSpaces(arguments, ref pos);

        if (pos >= arguments.Length)
        {
            return false;
        }

        if (arguments[pos] == '"')
        {
            pos++;
            var builder = new System.Text.StringBuilder();

            while (pos < arguments.Length)
            {
                var c = arguments[pos];

                if (c == '\\')
                {
                    pos++;
                    if (pos >= arguments.Length)
                    {
                        return false;
                    }

                    builder.Append(arguments[pos]);
                    pos++;
                    continue;
                }

                if (c == '"')
                {
                    pos++;
                    value = builder.ToString();
                    return true;
                }

                builder.Append(c);
                pos++;
            }

            // Unterminated quoted string.
            return false;
        }

        if (arguments[pos] == '{')
        {
            // A literal. See the type remarks for why this is refused rather than implemented.
            return false;
        }

        return TryReadAtom(arguments, ref pos, out value);
    }

    /// <summary>Validates a client-supplied tag before it is echoed back.</summary>
    /// <remarks>
    /// <b>The only client-supplied text the proxy echoes.</b> An IMAP tag is an atom: printable
    /// ASCII, no spaces, no control characters, and not the special <c>+</c> or <c>*</c>. Echoing an
    /// unvalidated tag would let a client inject a response line of its own into the stream the
    /// proxy writes back — so the check is a security boundary, not a formality.
    /// </remarks>
    internal static bool IsValidTag(string tag)
    {
        if (tag.Length is 0 or > 64)
        {
            return false;
        }

        if (tag is "+" or "*")
        {
            return false;
        }

        foreach (var c in tag)
        {
            // Atom-specials per RFC 3501, plus anything outside printable ASCII.
            if (c < 0x21 || c > 0x7e || c is '(' or ')' or '{' or ' ' or '%' or '*' or '"' or '\\' or ']')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadAtom(string text, ref int pos, out string value)
    {
        var start = pos;
        while (pos < text.Length && text[pos] != ' ')
        {
            pos++;
        }

        value = text[start..pos];
        return value.Length > 0;
    }

    private static void SkipSpaces(string text, ref int pos)
    {
        while (pos < text.Length && text[pos] == ' ')
        {
            pos++;
        }
    }
}
