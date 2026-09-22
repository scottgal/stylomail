using System.Security.Cryptography;
using System.Text;

namespace StyloMail.Chat.Slack;

/// <summary>What a request claiming to come from Slack turned out to be.</summary>
/// <remarks>
/// <b>A verdict rather than a bool.</b> "Not from Slack" and "from Slack, replayed" are different
/// facts, and an operator reading a log needs to tell them apart. A rejected request that only says
/// "no" is the kind of diagnostic this project keeps having to add afterwards.
/// </remarks>
public enum SlackSignatureVerdict
{
    Valid,
    MissingSignature,
    MalformedSignature,
    StaleRequest,
    Mismatch,
}

/// <summary>
/// Proves a request came from Slack and was not tampered with or replayed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Slack's scheme, in full.</b> The signed string is <c>v0:{timestamp}:{rawBody}</c>, signed with
/// HMAC-SHA256 under the app's signing secret, sent as <c>v0=&lt;hex&gt;</c>. The timestamp is part
/// of the signed string and is also checked against the clock, because a signature alone never
/// expires: without the window, one captured request replays forever.
/// </para>
/// <para>
/// <b>The body must be the raw bytes as received.</b> Any normalisation before verification, a
/// reserialisation or a whitespace trim, changes the signed string and either fails every request or
/// forces the caller to compare against something other than what will be parsed.
/// </para>
/// </remarks>
public static class SlackSignatureVerifier
{
    /// <summary>Slack's own guidance, and the window this replicates.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public static SlackSignatureVerdict Verify(
        string signingSecret,
        string? timestamp,
        string? signature,
        string rawBody,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return SlackSignatureVerdict.MissingSignature;
        }

        if (!signature.StartsWith("v0=", StringComparison.Ordinal))
        {
            return SlackSignatureVerdict.MalformedSignature;
        }

        if (!long.TryParse(timestamp, out var sentAtUnix))
        {
            return SlackSignatureVerdict.MalformedSignature;
        }

        byte[] presented;
        try
        {
            presented = Convert.FromHexString(signature[3..]);
        }
        catch (FormatException)
        {
            return SlackSignatureVerdict.MalformedSignature;
        }

        var sentAt = DateTimeOffset.FromUnixTimeSeconds(sentAtUnix);
        var skew = (now - sentAt).Duration();
        if (skew > Tolerance)
        {
            // Checked after the shape and before the comparison, so a replay is named as a replay
            // rather than reported as a mismatch against a secret that was never the problem.
            return SlackSignatureVerdict.StaleRequest;
        }

        var basestring = $"v0:{timestamp}:{rawBody}";
        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(signingSecret),
            Encoding.UTF8.GetBytes(basestring));

        return CryptographicOperations.FixedTimeEquals(expected, presented)
            ? SlackSignatureVerdict.Valid
            : SlackSignatureVerdict.Mismatch;
    }
}
