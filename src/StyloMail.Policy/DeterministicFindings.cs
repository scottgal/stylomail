namespace StyloMail.Policy;

/// <summary>
/// The declared unit of a weighted signal, so its value can be normalised into 0..1 without the
/// caller guessing from the number.
/// </summary>
/// <remarks>
/// A signal id's unit is a property of the signal, not a tunable: the producer decides whether it
/// publishes a ratio, a flag or a count, and the same id always publishes the same shape. This is
/// what lets a count sit in the same weighted table as a ratio without the count's magnitude being
/// read as a probability.
/// </remarks>
public enum SignalUnit
{
    /// <summary>Already a 0..1 ratio. Used as it arrives.</summary>
    Ratio,

    /// <summary>0 or 1. Used as it arrives.</summary>
    Boolean,

    /// <summary>An unbounded count, normalised to a bounded step before it enters the index.</summary>
    Count,
}

/// <summary>
/// The deterministic MIME findings that carry risk rather than merely describing the message, with
/// the unit each one declares.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these exist.</b> Until this landed, every entry in
/// <see cref="PolicyOptions.DimensionWeights"/> was a semantic id, so a checkable MIME fact could not
/// move the index no matter how decisive it was: a message whose displayed links disagreed with their
/// hosts could be allowed on the model's word alone. Filed high as
/// <c>unweighted-deterministic-findings-cannot-block-a</c>.
/// </para>
/// <para>
/// <b>Which signals qualify.</b> A signal is risk-shaped iff a high value is a <em>deviation from a
/// well-formed message</em>, rather than a property the message may legitimately have. That criterion
/// is the standard for any future signal. It is why <c>link_idn</c> (an internationalised host is a
/// property; the homograph signal already carries the anomaly), the two byte totals, the fan-out and
/// the two digests stay out, and why <c>html_markup_observation</c> stays out as emitted: it reports
/// an observation (a remote image is one) and does not sub-type the form or pixel case, so weighting
/// it would charge every newsletter.
/// </para>
/// <para>
/// <b>No byte total and no digest is ever weighted</b>, which is what makes the misfire the issue was
/// written about impossible by construction rather than by a guard: a stored attachment or a large
/// body cannot move the index by its size.
/// </para>
/// <para>
/// The ids are literals here rather than a reference to <c>StyloMail.Mime.MimeSignals</c>, because
/// Policy sits below Mime and must not depend on it. <c>MimeSignals</c> is the producer and its
/// comments carry the value shape each id publishes.
/// </para>
/// </remarks>
public static class DeterministicFindings
{
    public const string TrustedAuthenticationFailure = "deterministic.trusted_authentication_failure";
    public const string LinkDisplayMismatch = "deterministic.link_display_mismatch";
    public const string LinkIdnHomograph = "deterministic.link_idn_homograph";
    public const string DisplayNameAddressMismatch = "deterministic.display_name_address_mismatch";
    public const string AttachmentTypeMismatch = "deterministic.attachment_type_mismatch";
    public const string HtmlTextDisagreement = "deterministic.html_text_disagreement";
    public const string PaddingObfuscation = "deterministic.padding_obfuscation";
    public const string ReplyToDivergence = "deterministic.reply_to_divergence";
    public const string EnvelopeHeaderIdentity = "deterministic.envelope_header_identity";
    public const string ThreadHeaderConsistency = "deterministic.thread_header_consistency";

    /// <summary>Unit per weighted deterministic id. An id absent from this map is not deterministic.</summary>
    public static readonly IReadOnlyDictionary<string, SignalUnit> Units =
        new Dictionary<string, SignalUnit>(StringComparer.Ordinal)
        {
            [TrustedAuthenticationFailure] = SignalUnit.Count,
            [LinkDisplayMismatch] = SignalUnit.Ratio,
            [LinkIdnHomograph] = SignalUnit.Count,
            [DisplayNameAddressMismatch] = SignalUnit.Boolean,
            [AttachmentTypeMismatch] = SignalUnit.Count,
            [HtmlTextDisagreement] = SignalUnit.Ratio,
            [PaddingObfuscation] = SignalUnit.Count,
            [ReplyToDivergence] = SignalUnit.Boolean,
            [EnvelopeHeaderIdentity] = SignalUnit.Count,
            [ThreadHeaderConsistency] = SignalUnit.Count,
        };

    /// <summary>
    /// The checkable facts that must refuse a delivery, but as a <b>Hold rather than a Reject</b>:
    /// each is established rather than inferred, like a verified violation, yet each has a
    /// legitimate-traffic population that a permanent verdict would destroy and a review can release.
    /// Membership lives here, in the file that already answers which findings are deterministic, and
    /// not with the caller, because which facts can refuse an action is itself an action-shaped
    /// judgement and policy is the only place an action is chosen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>trusted_authentication_failure</c> holds mailing-list and forwarder traffic that breaks
    /// alignment on relay, which is legitimate and common. <c>link_idn_homograph</c> holds genuine
    /// internationalised domains and brands with non-ASCII names. <c>attachment_type_mismatch</c>
    /// holds genuine double-extension archives that are safe.
    /// </para>
    /// <para>
    /// The other seven stay index-only, and the reason is uniform: their false positives are
    /// structural rather than rare (display names, reply-to divergence on lists and ticketing,
    /// shorteners and tracking redirects, invisible preheader text, marketing footers, cross-posting),
    /// so a refusal rule would hold a large share of ordinary mail.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<string> Refusing =
        new HashSet<string>(StringComparer.Ordinal)
        {
            TrustedAuthenticationFailure,
            LinkIdnHomograph,
            AttachmentTypeMismatch,
        };

    /// <summary>True when this id is one of the weighted deterministic findings.</summary>
    public static bool IsDeterministic(string signalId) => Units.ContainsKey(signalId);

    /// <summary>True when this id names a checkable fact that must refuse a delivery.</summary>
    public static bool IsRefusing(string signalId) => Refusing.Contains(signalId);

    /// <summary>
    /// True when a measured value for <paramref name="signalId"/> establishes its finding, that is,
    /// when the value normalises to <c>1.0</c> by the id's declared unit. The caller checks
    /// availability, because only the caller can see whether the row was answered: this answers only
    /// whether the value means "present", never whether the question was put.
    /// </summary>
    public static bool Establishes(string signalId, double value)
        => IsRefusing(signalId)
            && Units.TryGetValue(signalId, out var unit)
            && Normalise(value, unit) >= 1.0;

    /// <summary>
    /// Normalises a value into 0..1 by its declared unit. A count becomes a <b>bounded step</b>,
    /// <c>1.0</c> at one or more and <c>0.0</c> at zero, so severity is carried by the weight and not
    /// by the magnitude. The step is chosen over a ramp because it is exact in doubles, so a ledger
    /// that exists to reproduce a historic decision reproduces it bit for bit, and because a ramp
    /// invents a knee per signal with no measurement behind it. Five homographs weigh what one does;
    /// that is reversible the day a measurement justifies a knee.
    /// </summary>
    public static double Normalise(double value, SignalUnit unit) => unit switch
    {
        SignalUnit.Count => value >= 1.0 ? 1.0 : 0.0,
        _ => Math.Clamp(value, 0.0, 1.0),
    };
}
