namespace StyloMail.Mime;

/// <summary>
/// Stable identifiers for the deterministic signals this adapter produces.
/// </summary>
/// <remarks>
/// These are the <see cref="StyloMail.Core.Evidence.SignalId"/> values. They are part of the
/// decision ledger's contract and are bumped through <see cref="SourceVersion"/> rather than
/// renamed in place, because a rule change that silently reuses an id makes historic decisions
/// unreproducible.
///
/// <para>
/// <b>Scope honesty.</b> Several features the source specification lists are <em>novelty</em>
/// judgements, "URL host novelty", "new correspondence relationships", "Reply-To novelty". A
/// novelty judgement is a comparison against a baseline, so it cannot be computed from one
/// message and is not fabricated here. This adapter emits the local, reproducible half (the
/// observable fact plus a stable grouping key) and leaves the baseline comparison to the
/// adaptive engine, which owns the observed-state store. The signals below say which half they
/// are in their names and attributes.
/// </para>
/// </remarks>
public static class MimeSignals
{
    /// <summary>
    /// Version of this adapter's rules and of the shape of the signals below. Recorded on
    /// every <see cref="StyloMail.Core.Evidence"/> as <c>SourceVersion</c>.
    /// </summary>
    public const string SourceVersion = "stylomail-mime/1";

    /// <summary>Envelope identity versus header identity. Value: count of disagreeing header identities.</summary>
    public const string EnvelopeHeaderIdentity = "deterministic.envelope_header_identity";

    /// <summary>A display name that is itself an address, differing from the address it is attached to.</summary>
    public const string DisplayNameAddressMismatch = "deterministic.display_name_address_mismatch";

    /// <summary>
    /// Local half of Reply-To novelty: Reply-To exists and its domain differs from the From domain.
    /// Value 1.0 = diverging, 0.0 = same domain. The baseline comparison is not done here.
    /// </summary>
    public const string ReplyToDivergence = "deterministic.reply_to_divergence";

    /// <summary>Count of failing authentication mechanisms that came from a trusted verifier.</summary>
    public const string TrustedAuthenticationFailure = "deterministic.trusted_authentication_failure";

    /// <summary>
    /// Whether usable authentication provenance existed at all. Value 1.0 = incomplete or absent.
    /// A supply of headers asserting their own authentication does not make this 0.0.
    /// </summary>
    public const string AuthenticationProvenance = "deterministic.authentication_provenance";

    /// <summary>Link label versus link destination. Value: ratio of links whose label disagrees with the target host.</summary>
    public const string LinkDisplayMismatch = "deterministic.link_display_mismatch";

    /// <summary>Links whose host is an internationalised domain. Value: count.</summary>
    public const string LinkIdn = "deterministic.link_idn";

    /// <summary>Internationalised hosts that look like a deliberate confusable. Value: count.</summary>
    public const string LinkIdnHomograph = "deterministic.link_idn_homograph";

    /// <summary>
    /// The observed URL-host fact plus a stable digest of the distinct host set, so the adaptive
    /// engine can compare this message's host set against the recipient's baseline. Novelty is
    /// <em>not</em> decided here.
    /// </summary>
    public const string LinkHostProfile = "deterministic.link_host_profile";

    /// <summary>Declared attachment type versus the type implied by its file name. Value: count of mismatches.</summary>
    public const string AttachmentTypeMismatch = "deterministic.attachment_type_mismatch";

    /// <summary>
    /// One per attachment: its digest, so a campaign can be grouped by attachment hash. The
    /// evidence value is the number of bytes hashed, which for an attachment over the hashing
    /// budget is a prefix rather than the file's size, <c>hashPartial</c> says which.
    /// </summary>
    public const string AttachmentHash = "deterministic.attachment_hash";

    /// <summary>Attachments whose content could not be read. Value: count.</summary>
    public const string AttachmentUnavailable = "deterministic.attachment_unavailable";

    /// <summary>Recipient fan-out on this transaction. Value: recipient count.</summary>
    public const string RecipientCount = "deterministic.recipient_count";

    /// <summary>Structural facts: part count, nesting depth, body and attachment counts.</summary>
    public const string MessageStructure = "deterministic.message_structure";

    /// <summary>
    /// A stable skeleton digest and similarity hash over the message's new text, for template
    /// similarity and bounded near-duplicate campaign grouping downstream.
    /// </summary>
    public const string TemplateFingerprint = "deterministic.template_fingerprint";

    /// <summary>Raw size and its breakdown. Value: total bytes.</summary>
    public const string MessageSize = "deterministic.message_size";

    /// <summary>HTML representation versus plain-text representation. Value: 1 - token containment.</summary>
    public const string HtmlTextDisagreement = "deterministic.html_text_disagreement";

    /// <summary>Padding, hidden text and obfuscation indicators. Value: count of indicators.</summary>
    public const string PaddingObfuscation = "deterministic.padding_obfuscation";

    /// <summary>Internal consistency of In-Reply-To, References and subject threading. Value: count of inconsistencies.</summary>
    public const string ThreadHeaderConsistency = "deterministic.thread_header_consistency";

    /// <summary>Quoted history versus new text. Value: proportion of text that is quoted history.</summary>
    public const string QuotedHistory = "deterministic.quoted_history";

    /// <summary>Encrypted or password-protected content that reduced the analysable portion. Value: count.</summary>
    public const string ContentEncrypted = "deterministic.content_encrypted";

    /// <summary>
    /// Explicit coverage summary. Value 1.0 when any reduced-coverage condition applies.
    /// Reduced coverage is always its own signal so it cannot be lost behind an absence.
    /// </summary>
    public const string AnalysisCoverage = "deterministic.analysis_coverage";

    /// <summary>
    /// Markup that collects or contacts something: forms, password inputs, remote images and
    /// tracking pixels. Reported even when the message has no form, because a remote image is
    /// itself an observation, the reader's client will contact a host the sender chose.
    /// </summary>
    public const string HtmlMarkupObservation = "deterministic.html_markup_observation";
}
