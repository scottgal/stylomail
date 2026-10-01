using System.Security.Cryptography;
using System.Text;
using StyloMail.Core;
using StyloMail.Mime;

namespace StyloMail.Conversation.Measure;

/// <summary>
/// The paired fixtures this lane builds for itself, with a planted temporal change and a quiet
/// control.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are mine, not the corpus generator's, and the report says so.</b> <c>corpus-</c> owns
/// paired fixtures with planted temporal changes and owes them; until they exist this pair is what
/// the agreement measurement runs on, and a reader must be able to tell the two apart.
/// </para>
/// <para>
/// <b>The pairing is the experiment.</b> Every member of a pair shares the same prior turns, the
/// same subject, the same threading headers and the same participant set. The only thing that
/// differs is whether the latest turn introduces the change. So a difference in the answer is
/// attributable to the planted change rather than to the message being longer, differently
/// threaded or from a different sender.
/// </para>
/// </remarks>
internal static class Threads
{
    /// <summary>One scenario: the prior turns, and the two possible latest turns.</summary>
    internal sealed record Pair(
        string Name,
        string Subject,
        IReadOnlyList<string> PriorTurns,
        string Control,
        string Planted,
        string PlantedKind);

    private const string Supplier = "Northwind Supplies <orders@northwind.example>";
    private const string Buyer = "alice@example.example";

    /// <summary>
    /// The three pairs. Two plant a change of different kinds, and one plants nothing at all.
    /// </summary>
    /// <remarks>
    /// The third pair is the quiet control that answers the other half of the question: a change
    /// question that reports a change where none was planted is as useless as one that misses a
    /// planted change, and only the pair with <c>Planted == Control</c> measures that.
    /// </remarks>
    internal static IReadOnlyList<Pair> All =>
    [
        new(
            Name: "order-thread-payment",
            Subject: "Re: Your order NW-4482 has shipped",
            PriorTurns:
            [
                "From: " + Supplier + "\nSubject: Your order NW-4482 has shipped\n\n"
                    + "Order NW-4482 was dispatched today and should arrive within two working days.",
                "From: " + Buyer + "\nSubject: Re: Your order NW-4482 has shipped\n\n"
                    + "Thanks for the update. Two working days is fine.",
                "From: " + Supplier + "\nSubject: Re: Your order NW-4482 has shipped\n\n"
                    + "Noted, thank you. The tracking reference will follow once the carrier scans it.",
            ],
            Control:
                "Thanks, the tracking came through and the parcel arrived this morning. "
                + "Everything is in order, so nothing further is needed from your side.",
            Planted:
                "Thanks, the parcel arrived. One thing before you close the order: our finance "
                + "system changed and the account on file is no longer active. Please update our "
                + "records to the new bank account below and use it for the invoice, "
                + "GB29 NWBK 6016 1331 9268 19.",
            PlantedKind: "payment destination introduced where none existed"),

        new(
            Name: "account-thread-credential",
            Subject: "Re: Your account statement is ready",
            PriorTurns:
            [
                "From: " + Supplier + "\nSubject: Your account statement is ready\n\n"
                    + "Your monthly statement is ready and can be reviewed in the usual place.",
                "From: " + Buyer + "\nSubject: Re: Your account statement is ready\n\n"
                    + "Thanks, I will look at it over the weekend.",
                "From: " + Supplier + "\nSubject: Re: Your account statement is ready\n\n"
                    + "Of course. Let us know if anything on it looks wrong.",
            ],
            Control:
                "I looked at the statement and the figures match what I expected. "
                + "No questions, and nothing needs changing.",
            Planted:
                "I looked at the statement, but I cannot get in. Please confirm your password "
                + "policy, and re-send the one-time code that was supposed to come through, "
                + "so I can get back into the account today.",
            PlantedKind: "credential request introduced into a service thread"),

        // Quiet control: the latest turn continues the thread and changes nothing. A change
        // question that fires here is measuring the question, not the thread.
        new(
            Name: "order-thread-quiet",
            Subject: "Re: Your order NW-4482 has shipped",
            PriorTurns:
            [
                "From: " + Supplier + "\nSubject: Your order NW-4482 has shipped\n\n"
                    + "Order NW-4482 was dispatched today and should arrive within two working days.",
                "From: " + Buyer + "\nSubject: Re: Your order NW-4482 has shipped\n\n"
                    + "Thanks for the update. Two working days is fine.",
                "From: " + Supplier + "\nSubject: Re: Your order NW-4482 has shipped\n\n"
                    + "Noted, thank you. The tracking reference will follow once the carrier scans it.",
            ],
            Control:
                "Thanks, the tracking came through and the parcel arrived this morning. "
                + "Everything is in order, so nothing further is needed from your side.",
            Planted:
                "Thanks, the tracking came through and the parcel arrived this morning. "
                + "Everything is in order, so nothing further is needed from your side.",
            PlantedKind: "none (quiet control: the two arms are identical)"),
    ];

    /// <summary>
    /// Builds the classifier input for one arm of a pair, parsing it as a real reply in the thread.
    /// </summary>
    /// <remarks>
    /// The latest turn is parsed by the shipping analyser rather than hand-built, and it carries
    /// <c>In-Reply-To</c> and <c>References</c> against the first prior turn, so it is a genuine
    /// reply rather than a message that merely resembles one. The threading headers matter: the
    /// deterministic leg of a thread reads them, and a pair whose arms differed in their headers
    /// would confound the semantic change with a deterministic one.
    /// </remarks>
    /// <remarks>
    /// <b>Why provenance is a parameter here.</b> This constructor used to build every arm without an
    /// authentication block, which made it this lane's own shape rather than the Host's. Measured, that
    /// difference is not cosmetic: with the same message, the same window, the same absent profile and
    /// the same tenant, a trusted authentication block answers <c>B</c> where its absence answers
    /// <c>A</c>. An axis measured on one of those shapes is an axis measured on that shape, and a
    /// comparison between two arms has to hold it fixed or the contrast is between provenances rather
    /// than between bodies.
    /// </remarks>
    internal static SemanticMailInput Input(
        Pair pair,
        IReadOnlyList<string> priorTurns,
        string latestBody,
        bool authenticated = false,
        string verifierId = "measure-boundary",
        string tenantId = "measure")
    {
        var messageId = $"<conversation-measure-{pair.Name}@example.test>";
        var raw = Encoding.UTF8.GetBytes(
            "From: " + Buyer + "\r\n"
            + "To: " + Supplier + "\r\n"
            + $"Subject: {pair.Subject}\r\n"
            + "Date: Mon, 22 Sep 2026 15:00:00 +0000\r\n"
            + $"Message-ID: <latest-{pair.Name}@example.test>\r\n"
            + $"In-Reply-To: {messageId}\r\n"
            + $"References: {messageId}\r\n"
            + "MIME-Version: 1.0\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n"
            + "\r\n"
            + latestBody.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n");

        var envelope = new MailEnvelope
        {
            InternalMessageId = $"conversation-measure-{pair.Name}-latest",
            TenantId = tenantId,
            Direction = MailDirection.Inbound,
            TrustedPrincipalId = "conversation-measure",
            MailFrom = "alice@example.example",
            RcptTo = ["orders@northwind.example"],
            ReceivedAt = DateTimeOffset.UnixEpoch,
            MimeDigest = Convert.ToHexStringLower(SHA256.HashData(raw)),
            PayloadReference = PayloadReferences.Ephemeral,
        };

        var result = new BoundedMimeMessageAnalyzer().Analyze(new MimeAnalysisRequest
        {
            Envelope = envelope,
            RawMessage = raw,
            ConversationContext = priorTurns,
            Authentication = authenticated ? Corpus.TrustedAuthentication(verifierId) : null,
        });

        if (!result.IsAnalysable)
        {
            throw new InvalidOperationException(
                $"The '{pair.Name}' latest turn did not parse ({result.Disposition}), so it cannot be "
                + "measured as a reply in a thread.");
        }

        return new SemanticMailInput
        {
            Message = result.Message!,
            Dimensions = SemanticDimensions.All,
        };
    }
}
