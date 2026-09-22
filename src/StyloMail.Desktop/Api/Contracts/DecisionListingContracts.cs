namespace StyloMail.Desktop.Api.Contracts;

/// <summary>
/// <c>GET /v1/decisions</c>: a page of the explainable ledger, newest first.
/// </summary>
/// <remarks>
/// <b>Rows are summaries, not whole decisions.</b> A <see cref="DecisionResponse"/>
/// carries the full evidence list, risk dimensions, recipients and coverage, so
/// a page of them is potentially megabytes and the evidence volume is
/// per-message, which a listing cannot bound. The full explanation is one
/// <see cref="StyloMailApiClient.GetDecisionAsync"/> away, and that route already
/// returns exactly the shape the detail pane renders.
///
/// <para>
/// Two hops rather than one, then: a message row carries
/// <c>internalMessageId</c>, the ledger can be filtered by it, and the row's
/// <c>assessmentId</c> fetches the explanation. The console takes the newest and
/// says so when there is more than one, because a message can legitimately be
/// assessed more than once and showing only the newest without saying so would
/// hide a re-assessment.
/// </para>
/// </remarks>
public sealed record DecisionListingResponse
{
    public required string TenantId { get; init; }

    /// <summary>Which action was asked for, echoed so pages can be told apart. Null means all.</summary>
    public string? Action { get; init; }

    public required IReadOnlyList<DecisionSummaryResponse> Decisions { get; init; }

    /// <summary>Echo back unchanged for the next page; null when this is the last one.</summary>
    public string? NextCursor { get; init; }

    public required bool HasMore { get; init; }
}

/// <summary>One ledger row, without the evidence payload.</summary>
public sealed record DecisionSummaryResponse
{
    public required string AssessmentId { get; init; }

    /// <summary>The message this decision is about. The join key from a message row.</summary>
    public required string InternalMessageId { get; init; }

    public required MailAction Action { get; init; }

    /// <summary>In shadow mode, the action policy would have taken. Forwarding still occurred.</summary>
    public MailAction? ProposedActionInShadow { get; init; }

    /// <summary>
    /// The aggregate risk index, with the same caveat as everywhere else: a
    /// documented index and <b>not</b> a calibrated probability.
    /// </summary>
    public required double RiskIndex { get; init; }

    /// <summary>
    /// The reasons, most significant first, with their sentences.
    /// </summary>
    /// <remarks>
    /// Codes and messages rather than codes alone. A list showing only codes
    /// would make a reviewer open every row to find out whether it was
    /// interesting, which is the work the list exists to save.
    /// </remarks>
    public required IReadOnlyList<ReasonResponse> Reasons { get; init; }

    public required VersionsResponse Versions { get; init; }

    /// <summary>
    /// How much of the message was analysable.
    /// </summary>
    /// <remarks>
    /// On the row rather than only in the detail, because a decision taken over
    /// reduced coverage is a weaker one and a reviewer scanning the list should
    /// be able to see which rows those are without opening each one.
    /// </remarks>
    public required CoverageResponse Coverage { get; init; }

    public required DateTimeOffset AssessedAt { get; init; }
}
