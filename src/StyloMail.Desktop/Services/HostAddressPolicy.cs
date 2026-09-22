namespace StyloMail.Desktop.Services;

/// <summary>
/// Which Host addresses this console will talk to.
/// </summary>
/// <remarks>
/// <b>Loopback is the only place plain http is defensible.</b> Everything the
/// console sends carries the operator's API key in a header, so an unencrypted
/// connection to anything that is not the same machine puts a credential that
/// can read every decision and release every quarantine onto the wire in clear
/// text.
///
/// <para>
/// A pure function of the address rather than a check buried in the connect
/// path, so it can be tested exhaustively and so the connection screen and the
/// client disagree about nothing.
/// </para>
/// </remarks>
public static class HostAddressPolicy
{
    /// <summary>Names that mean this machine.</summary>
    /// <remarks>
    /// <c>localhost</c> is included because it is what people type. A name that
    /// resolves to loopback in one environment and elsewhere in another is
    /// exactly the ambiguity this rule must not guess at, and a literal name is
    /// the narrower risk than allowing every host that might resolve to
    /// <c>127.0.0.1</c>.
    /// </remarks>
    private static readonly string[] LoopbackNames = ["localhost", "127.0.0.1", "::1", "[::1]"];

    /// <summary>Whether the console may point at this address.</summary>
    /// <param name="host">The candidate. Null or relative is refused.</param>
    /// <param name="refusal">Why not, when it is not acceptable. Null when it is.</param>
    public static bool IsAcceptable(Uri? host, out string? refusal)
    {
        if (host is null || !host.IsAbsoluteUri)
        {
            refusal = "That is not a complete address. It needs a scheme and a host.";
            return false;
        }

        if (host.Scheme is "https" or "wss")
        {
            refusal = null;
            return true;
        }

        if (host.Scheme is "http" or "ws")
        {
            if (IsLoopback(host))
            {
                refusal = null;
                return true;
            }

            refusal =
                "Refused: a plain http address sends the API key unencrypted. "
                + "Use https, or point at this machine.";
            return false;
        }

        refusal = $"Refused: '{host.Scheme}' is not a scheme this console talks over. Use https.";
        return false;
    }

    /// <summary>Whether the address names this machine.</summary>
    public static bool IsLoopback(Uri host)
    {
        if (host.IsLoopback) return true;

        var name = host.Host;

        return LoopbackNames.Any(candidate =>
            string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
    }
}
