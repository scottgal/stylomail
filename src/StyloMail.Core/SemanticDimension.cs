namespace StyloMail.Core;

/// <summary>
/// One independently scored semantic property of a message.
/// </summary>
/// <remarks>
/// These are <b>Noul</b> questions in TypeSafe terms: each answers its own yes/no and
/// several may hold at once. They are deliberately not a <c>Choice</c> across labels —
/// a message can request credentials <em>and</em> redirect payment, and forcing one
/// mutually exclusive label would discard that.
///
/// <para>
/// The complete question lives in <see cref="Instructions"/> because a TypeSafe question
/// key is not sent to the model and is not used in inference. These are model assessments,
/// not facts, and a Noul near 0.5 means balanced yes/no rather than moderate intensity.
/// </para>
/// </remarks>
public sealed record SemanticDimension
{
    /// <summary>Stable question key. Used as the TypeSafe question id and as the evidence signal id.</summary>
    public required string Id { get; init; }

    public required string Instructions { get; init; }

    /// <summary>What a near-1 answer means.</summary>
    public required string CriteriaTrue { get; init; }

    /// <summary>What a near-0 answer means.</summary>
    public required string CriteriaFalse { get; init; }
}

/// <summary>
/// The bounded semantic question set. Adding or changing a dimension is a question-schema
/// version change and invalidates memoised semantic assessments.
/// </summary>
public static class SemanticDimensions
{
    /// <summary>Version of this question set. Part of the semantic cache key.</summary>
    public const string QuestionSchemaVersion = "semantic-dimensions/1";

    /// <summary>
    /// Whether <see cref="ConversationalContinuity"/> can be asked at all, given the supplied
    /// context. When no bounded conversation context is present the dimension is
    /// <see cref="EvidenceAvailability.NotApplicable"/> rather than scored low.
    /// </summary>
    public const string ConversationalContinuityId = "semantic.conversational_continuity";

    public static readonly IReadOnlyList<SemanticDimension> All =
    [
        new()
        {
            Id = "semantic.unsolicited_solicitation",
            Instructions = "Does this message make a promotional or commercial solicitation that the recipient has not solicited?",
            CriteriaTrue = "Offers, markets or promotes goods, services or content with no established consensual relationship.",
            CriteriaFalse = "No commercial solicitation: personal, transactional or informational correspondence only.",
        },
        new()
        {
            Id = "semantic.credential_request",
            Instructions = "Does this message ask the recipient to provide, confirm or re-enter credentials, authentication factors or access tokens?",
            CriteriaTrue = "Requests passwords, one-time codes, PINs, recovery phrases, API keys, session tokens or wallet access.",
            CriteriaFalse = "No request for credentials, authentication factors or access tokens.",
        },
        new()
        {
            Id = "semantic.payment_redirection",
            Instructions = "Does this message attempt to introduce or change payment destination details?",
            CriteriaTrue = "Supplies or changes bank account, routing, invoice or payment destination details.",
            CriteriaFalse = "No introduction or change of payment destination.",
        },
        new()
        {
            Id = "semantic.identity_authority_claim",
            Instructions = "Does this message claim to represent a person, institution or authority in a way that could be impersonation?",
            CriteriaTrue = "Asserts representation of a named person, organisation, government body or trusted service.",
            CriteriaFalse = "No claim of representation beyond the sender's own voice.",
        },
        new()
        {
            Id = "semantic.urgency_pressure",
            Instructions = "Does this message try to bypass deliberation through pressure, deadlines or time-sensitive consequences?",
            CriteriaTrue = "Imposes deadlines, threatens loss, or insists on immediate action.",
            CriteriaFalse = "No time pressure and no pressure to act without deliberation.",
        },
        new()
        {
            Id = "semantic.secrecy_bypass",
            Instructions = "Does this message ask the recipient to avoid normal verification, approval or oversight processes?",
            CriteriaTrue = "Requests secrecy from colleagues or asks the recipient to skip established checks, approvals or verification.",
            CriteriaFalse = "No request to bypass process and no request for secrecy.",
        },
        new()
        {
            Id = "semantic.sensitive_data_request",
            Instructions = "Does this message ask for confidential business or personal information?",
            CriteriaTrue = "Requests financial, identity, health, payroll or otherwise confidential business information.",
            CriteriaFalse = "No request for confidential business or personal information.",
        },
        new()
        {
            Id = "semantic.link_lure",
            Instructions = "Does this message encourage the recipient to navigate to a link for verification, reward, delivery or account recovery?",
            CriteriaTrue = "Directs the recipient to click through for account verification, parcel delivery, refunds, prizes or account recovery.",
            CriteriaFalse = "No click-through invitation of that kind.",
        },
        new()
        {
            Id = "semantic.attachment_lure",
            Instructions = "Does this message encourage the recipient to open or execute an attached file?",
            CriteriaTrue = "Prompts opening or running an attachment, particularly where its importance or urgency is asserted.",
            CriteriaFalse = "No prompt to open or execute an attachment.",
        },
        new()
        {
            Id = "semantic.threat_reward_inducement",
            Instructions = "Does this message use coercive consequences or implausible incentives to influence the recipient?",
            CriteriaTrue = "Threatens penalty, exposure or account loss, or offers rewards that are implausibly generous.",
            CriteriaFalse = "No coercive consequences and no implausible incentives.",
        },
        new()
        {
            Id = "semantic.transactional_character",
            Instructions = "Is this message an order, booking, receipt, notification or service message?",
            CriteriaTrue = "A record, acknowledgement or notification of an existing transaction or service event.",
            CriteriaFalse = "Not a transactional, acknowledgement or service message.",
        },
        new()
        {
            Id = ConversationalContinuityId,
            Instructions = "Does this message fit the bounded conversation context supplied in the state?",
            CriteriaTrue = "Consistent with the prior exchange supplied as conversation context.",
            CriteriaFalse = "Inconsistent with, or unrelated to, the supplied conversation context.",
        },
    ];
}
