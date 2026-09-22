using System.Globalization;
using System.Text;

namespace StyloMail.Transport.Smtp;

/// <summary>
/// A bounded, credential-redacting record of one SMTP conversation.
/// </summary>
/// <remarks>
/// Kept because "why did this message not go out?" is otherwise unanswerable: the reply code the
/// upstream gave is the only evidence there is, and once the socket is gone it is gone. Two
/// properties make it safe to keep and safe to log:
///
/// <list type="number">
/// <item><b>It redacts.</b> The bytes of an <c>AUTH</c> exchange are base64 of the username and
/// password. A transcript that recorded them verbatim would put a live credential into whatever
/// stores transcripts, a log file, a decision ledger, a crash dump. The payload is replaced at the
/// point of recording, so there is no path on which the secret is held and merely "not printed".</item>
/// <item><b>It is bounded.</b> A peer controls how much it says. An unbounded transcript is an
/// unbounded allocation driven by the remote end, which is the same denial-of-service the reply
/// reader bounds exist to stop. Once full it stops recording and says so, rather than dropping the
/// beginning and leaving a transcript whose first line is mid-conversation.</item>
/// </list>
/// </remarks>
public sealed class SmtpTranscript
{
    /// <summary>Stand-in recorded wherever a credential would otherwise appear.</summary>
    public const string Redacted = "[redacted]";

    private readonly List<string> _entries = [];
    private readonly int _maxEntries;
    private readonly int _maxEntryChars;
    private bool _truncated;

    public SmtpTranscript(int maxEntries = 200, int maxEntryChars = 512)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntryChars, 16);

        _maxEntries = maxEntries;
        _maxEntryChars = maxEntryChars;
    }

    /// <summary>The conversation so far, oldest first. Empty until something has been said.</summary>
    public IReadOnlyList<string> Entries => _entries;

    /// <summary>True when a bound stopped recording, so the tail is missing.</summary>
    public bool IsTruncated => _truncated;

    /// <summary>Records a command we sent.</summary>
    /// <param name="command">The command, without its line terminator.</param>
    /// <param name="isCredentialBearing">
    /// True when the command carries authentication material. Callers must set this for the
    /// continuation lines of a SASL exchange as well as for the <c>AUTH</c> verb itself, the base64
    /// is on the follow-up line for some mechanisms, not on the <c>AUTH</c> line.
    /// </param>
    internal void RecordClient(string command, bool isCredentialBearing = false)
    {
        Append("C: " + (isCredentialBearing ? Redact(command) : command));
    }

    /// <summary>Records a reply we received.</summary>
    internal void RecordServer(SmtpReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        Append("S: " + reply.ToString());
    }

    /// <summary>Records that the connection failed, and at what stage.</summary>
    internal void RecordFailure(SmtpDeliveryStage stage, string reason)
    {
        Append($"! {stage}: {reason}");
    }

    /// <summary>
    /// Renders the transcript for storage or display.
    /// </summary>
    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach (var entry in _entries)
        {
            builder.Append(entry).Append('\n');
        }

        if (_truncated)
        {
            builder.Append("[transcript truncated at ").Append(_maxEntries.ToString(CultureInfo.InvariantCulture))
                .Append(" entries]\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Blanks out everything after the mechanism name.
    /// </summary>
    /// <remarks>
    /// Covers both shapes the protocol uses: <c>AUTH PLAIN &lt;base64&gt;</c> on one line, and a bare
    /// <c>AUTH LOGIN</c> followed by base64 prompts. The latter is handled by the caller flagging the
    /// continuation line, not by this method.
    /// </remarks>
    private static string Redact(string command)
    {
        var firstSpace = command.IndexOf(' ', StringComparison.Ordinal);
        if (firstSpace < 0)
        {
            // A bare "AUTH" with nothing after it carries nothing, but a bare base64 continuation
            // line is exactly the credential, so anything reaching here unmarked is blanked whole.
            return command.Equals("AUTH", StringComparison.OrdinalIgnoreCase)
                ? command
                : Redacted;
        }

        var mechanismEnd = command.IndexOf(' ', firstSpace + 1);
        return mechanismEnd < 0
            ? command
            : string.Concat(command.AsSpan(0, mechanismEnd), " ", Redacted);
    }

    private void Append(string entry)
    {
        if (_entries.Count >= _maxEntries)
        {
            _truncated = true;
            return;
        }

        _entries.Add(entry.Length <= _maxEntryChars ? entry : string.Concat(entry.AsSpan(0, _maxEntryChars), "…"));
    }
}
