using StyloMail.Core;
using StyloMail.Host.Decisions;

namespace StyloMail.Host.Contracts;

/// <summary>
/// The answer to <c>GET /v1/decisions</c>: a page of the explainable ledger, newest first.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rows are summaries, not whole decisions.</b> A <see cref="DecisionResponse"/> carries the full
/// evidence list, risk dimensions, recipients and coverage for one message, so a page of them is
/// potentially megabytes and the evidence volume is per-message — the listing cannot bound it. The
/// full explanation is one <c>GET /v1/decisions/{id}</c> away, and that route already returns exactly
/// the shape the detail view renders.
/// </para>
/// <para>
/// What a row carries is what a reviewer needs to decide whether to open it: the action, the ordered
/// reason codes, the versions the decision was made under, and the coverage flags that say how much
/// of the message was actually analysable.
/// </para>
/// </remarks>
public sealed record DecisionListingResponse
{
    public required string TenantId { get; init; }

    /// <summary>Which action was asked for, echoed so a caller can tell pages apart. Null means all.</summary>
    public string? Action { get; init; }

    public required IReadOnlyList<DecisionSummaryResponse> Decisions { get; init; }

    /// <summary>Echo back to fetch the next page; null when this is the last one.</summary>
    public string? NextCursor { get; init; }

    public required bool HasMore { get; init; }

    public static DecisionListingResponse From(string tenantId, MailAction? action, DecisionListingPage page) => new()
    {
        TenantId = tenantId,
        Action = action?.ToString(),
        Decisions = [.. page.Items.Select(DecisionSummaryResponse.From)],
        NextCursor = page.NextCursor,
        HasMore = page.HasMore,
    };
}

/// <summary>One ledger row, without the evidence payload.</summary>
public sealed record DecisionSummaryResponse
{
    public required string AssessmentId { get; init; }

    public required string InternalMessageId { get; init; }

    public required MailAction Action { get; init; }

    /// <summary>In shadow mode, the action policy would have taken. Forwarding still occurred.</summary>
    public MailAction? ProposedActionInShadow { get; init; }

    /// <summary>
    /// The aggregate risk index — <b>a documented index, not a calibrated probability</b>.
    /// </summary>
    /// <remarks>
    /// Carried because a reviewer list will want to order or colour by it, and because it is already
    /// on the record. It is a weighted combination of correlated semantic dimensions and must not be
    /// rendered as a probability of anything — see <see cref="MailAssessment.RiskIndex"/>.
    /// </remarks>
    public required double RiskIndex { get; init; }

    /// <summary>
    /// The reasons, most significant first.
    /// </summary>
    /// <remarks>
    /// Codes <em>and</em> messages: a listing that showed only codes would make a reviewer open every
    /// row to find out whether it was interesting, which is the work the list exists to save. The
    /// messages here are ours and describe evidence, not message content.
    /// </remarks>
    public required IReadOnlyList<ReasonResponse> Reasons { get; init; }

    /// <summary>Versions the decision was made under, so a row can be read against the right policy.</summary>
    public required VersionsResponse Versions { get; init; }

    /// <summary>
    /// How much of the message was analysable.
    /// </summary>
    /// <remarks>
    /// On the row rather than only in the detail, because a decision taken over reduced coverage is
    /// a weaker one and a reviewer scanning the list should be able to see which rows those are.
    /// </remarks>
    public required CoverageResponse Coverage { get; init; }

    public required DateTimeOffset AssessedAt { get; init; }

    public static DecisionSummaryResponse From(MailAssessment assessment) => new()
    {
        AssessmentId = assessment.AssessmentId,
        InternalMessageId = assessment.InternalMessageId,
        Action = assessment.Action,
        ProposedActionInShadow = assessment.ProposedActionInShadow,
        RiskIndex = assessment.RiskIndex,
        Reasons = [.. assessment.Reasons.Select(ReasonResponse.From)],
        Versions = VersionsResponse.From(assessment.Versions),
        Coverage = CoverageResponse.From(assessment.Coverage),
        AssessedAt = assessment.AssessedAt,
    };
}
