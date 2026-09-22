using System.Security.Cryptography;
using System.Text;
using StyloMail.Assessment.Semantic;
using StyloMail.Core;

namespace StyloMail.Assessment.Triage;

/// <summary>
/// The security-bearing fingerprint of a chat message: its normalised text and its destinations.
/// </summary>
/// <remarks>
/// <para>
/// <b>The basis is the text, because chat's repetition is textual.</b> Cutting channel noise is one
/// of the three jobs, and the shape of that noise is the same words broadcast again rather than the
/// same links. A link-based basis would make the duplicate check fire almost never on a channel where
/// most messages carry no link at all, which is not a conservative tuning of the check, it is a check
/// that does not run.
/// </para>
/// <para>
/// <b>And the destinations are in it, which is the half that matters.</b> Two messages with identical
/// words and different destinations are not duplicates and must escalate. Putting both into one
/// fingerprint makes that true by construction rather than by a threshold, which is what the
/// fifty-first message deserves: the one that repeats the campaign and changes precisely the thing
/// that makes it an attack.
/// </para>
/// <para>
/// <b>An empty text and no destinations is not agreement with anything.</b> The same rule a
/// fingerprint over nothing has always had: a message with no caption is not a duplicate of every
/// other message with no caption.
/// </para>
/// </remarks>
public static class ChatFingerprint
{
    /// <summary>
    /// The fingerprint for one chat message, or one over nothing when there is nothing to digest.
    /// </summary>
    /// <remarks>
    /// Components are length-prefixed rather than joined by a separator, so two messages cannot
    /// produce the same digest by splitting their content differently across the boundary. A
    /// separator would need a character that cannot occur in either part, and there is no such
    /// character in free text.
    /// </remarks>
    public static SecurityBearingFingerprint Of(ChatAnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var components = new List<string>(input.Links.Count + 1);

        var normalised = Normalise(input.BodyText);
        if (normalised.Length > 0)
        {
            components.Add(normalised);
        }

        // Order preserved and every occurrence kept, following the mail path: a message listing two
        // destinations and one listing the same destination twice are different messages.
        components.AddRange(input.Links.Select(link => link.ActualTarget));

        if (components.Count == 0)
        {
            return new SecurityBearingFingerprint { Digest = string.Empty, ComponentCount = 0 };
        }

        var builder = new StringBuilder();
        foreach (var component in components)
        {
            builder.Append(component.Length).Append(':').Append(component);
        }

        return new SecurityBearingFingerprint
        {
            Digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant(),
            ComponentCount = components.Count,
        };
    }

    /// <summary>
    /// The text as it will be compared: trimmed, with runs of whitespace collapsed to one space.
    /// </summary>
    /// <remarks>
    /// <b>Case is deliberately preserved.</b> Folding it would make "Invoice attached" and "invoice
    /// attached" duplicates, and the difference between them is exactly the kind of thing a
    /// duplicate check should not be deciding on its own. Whitespace is collapsed because that is
    /// presentation rather than content, and no amount of it changes what the message says.
    /// </remarks>
    private static string Normalise(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var character in text.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
