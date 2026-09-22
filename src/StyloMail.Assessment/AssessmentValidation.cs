using StyloMail.Core;

namespace StyloMail.Assessment;

/// <summary>Rule identifiers for the mandatory limits the pipeline applies before anything else.</summary>
/// <remarks>
/// Stable, machine-readable, and deliberately not prose. These end up in
/// <see cref="PolicyContext.VerifiedSecurityRuleViolations"/>, which policy treats as established
/// facts rather than model judgements — so the identifier is the contract, and a message cannot
/// influence it because none of these are derived from message content.
/// </remarks>
public static class AssessmentRules
{
    public const string NoRecipients = "envelope.no_recipients";
    public const string MissingTrustedPrincipal = "envelope.missing_trusted_principal";
    public const string UnapprovedSenderIdentity = "envelope.unapproved_sender_identity";

    /// <summary>
    /// A null sender (<c>&lt;&gt;</c>) on the submission path. A null sender means "this is a DSN", and
    /// we do not originate bounces — DSN policy stays with the upstream MTA.
    /// </summary>
    /// <remarks>
    /// <b>Unconditional, and that is the point.</b> A null sender previously reached the queue's
    /// argument guard, which *throws* rather than returning a refusal, so whether a message crashed
    /// out of <c>AssessAsync</c> or was cleanly declined depended on whether an unrelated approved-
    /// sender list happened to be configured. A rule that only sometimes fires is not a rule.
    /// </remarks>
    public const string NullSenderNotPermitted = "envelope.null_sender_not_permitted";
    public const string RecipientCountExceeded = "limits.recipient_count";
    public const string LinkCountExceeded = "limits.link_count";
    public const string AttachmentCountExceeded = "limits.attachment_count";
    public const string BodySizeExceeded = "limits.body_size";
    public const string ParserLimitExceeded = "limits.parser_limit_exceeded";
    public const string OversizeRejected = "limits.oversize_rejected";
    public const string PayloadNotDurable = "envelope.payload_not_durable";

    /// <summary>Prefix for a parser rejection, followed by the parser's own reason token.</summary>
    public const string ParseRejectedPrefix = "parse.";
}

/// <summary>
/// Step one: validate the envelope, the authorisation and the limits.
/// </summary>
/// <remarks>
/// <b>Nothing here is inferred from message content.</b> Identity comes from the envelope and from
/// trusted provenance; a client-supplied <c>From</c> header cannot establish who sent a message, and
/// a message that names its own limits cannot lift them. The output is a list of rule identifiers
/// rather than an action, because choosing an action is policy's job and there is exactly one place
/// in this system where that happens.
///
/// <para>
/// The rules are <em>mandatory</em> in the sense that they are applied regardless of anything the
/// semantic layer says afterwards: a message that blows the recipient ceiling is refused whether or
/// not the model found it benign.
/// </para>
/// </remarks>
public static class AssessmentValidation
{
    /// <summary>
    /// Checks the envelope and the declared limits, returning the rule identifiers that were
    /// breached. An empty list means the message may proceed.
    /// </summary>
    public static List<string> Validate(
        MailAnalysisInput input,
        AssessmentContext context,
        MailAssessorOptions options)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        var violations = new List<string>();
        var envelope = input.Envelope;

        if (string.IsNullOrWhiteSpace(envelope.TrustedPrincipalId))
        {
            // The principal is what the transport boundary established. Without one there is no
            // identity to attribute the message to, and inventing one from the From header would be
            // accepting the sender's own claim about who they are.
            violations.Add(AssessmentRules.MissingTrustedPrincipal);
        }

        if (envelope.RcptTo.Count == 0)
        {
            violations.Add(AssessmentRules.NoRecipients);
        }

        if (envelope.RcptTo.Count > options.MaxRecipients)
        {
            violations.Add(AssessmentRules.RecipientCountExceeded);
        }

        if (input.Links.Count > options.MaxLinks)
        {
            violations.Add(AssessmentRules.LinkCountExceeded);
        }

        if (input.Attachments.Count > options.MaxAttachments)
        {
            violations.Add(AssessmentRules.AttachmentCountExceeded);
        }

        if (input.BodyText.Length > options.MaxBodyCharacters)
        {
            violations.Add(AssessmentRules.BodySizeExceeded);
        }

        if (input.Coverage.ParserLimitExceeded)
        {
            violations.Add(AssessmentRules.ParserLimitExceeded);
        }

        if (input.Coverage.OversizeRejected)
        {
            violations.Add(AssessmentRules.OversizeRejected);
        }

        // An approved-sender list is a statement about which identities this authenticated principal
        // may speak as. Only the list's own emptiness disables it — an empty list means "no
        // restriction configured", not "nothing is approved", because reading it the other way would
        // refuse all mail in every deployment that had not configured one.
        // A null sender is refused before anything else looks at identity, and regardless of the
        // approved list — see the rule's remarks. Outbound only: an inbound DSN delivered to a
        // mailbox is ordinary mail, and the ruling leaves the inbound path unaffected.
        if (envelope.Direction == MailDirection.Outbound && SenderAddresses.IsNullSender(envelope.MailFrom))
        {
            violations.Add(AssessmentRules.NullSenderNotPermitted);
        }

        var approved = input.Authentication.ApprovedSenderIdentities;
        if (approved.Count > 0)
        {
            var from = Normalize(envelope.MailFrom);

            if (!approved.Any(identity => Normalize(identity) == from))
            {
                violations.Add(AssessmentRules.UnapprovedSenderIdentity);
            }
        }

        return violations;
    }

    private static string Normalize(string? address) =>
        string.IsNullOrWhiteSpace(address) ? string.Empty : address.Trim().ToLowerInvariant();

}
