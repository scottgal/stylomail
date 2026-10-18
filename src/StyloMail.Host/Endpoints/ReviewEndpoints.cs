using System.Security.Claims;
using StyloMail.Core;
using StyloMail.Host.Auth;
using StyloMail.Host.Contracts;
using StyloMail.Host.Decisions;
using StyloMail.Host.Storage;
using StyloMail.Queue;

namespace StyloMail.Host.Endpoints;

/// <summary>
/// The review surface: messages whose delivery is withheld for an authenticated human decision.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read on <c>Review</c>; release and quarantine on <c>Review</c>; reject on <c>Administer</c>.</b>
/// Reject is a permanent refusal, not a routine reviewer action, so it carries the higher
/// privilege. Every mutation takes a reason and derives the actor from the principal.
/// </para>
/// <para>
/// <b>Tenant comes from the principal and is never a parameter.</b> A review is addressed by queue
/// id, and the queue item plus the decision ledger are read under the same tenant.
/// </para>
/// <para>
/// <b>The decision record is the decision that opened the review.</b> A null record means no pack
/// was active; it is shown as such and never as a Permit.
/// </para>
/// </remarks>
internal static class ReviewEndpoints
{
    /// <summary><c>GET /v1/policy/reviews</c>, open reviews newest first.</summary>
    internal static async Task<IResult> ListAsync(
        string? state,
        int? limit,
        string? after,
        ClaimsPrincipal user,
        QueueStore queue,
        IDecisionLedger ledger,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var tenant = user.TenantId();
        if (string.IsNullOrWhiteSpace(tenant))
        {
            return EndpointResults.Forbidden("tenant_required", "The principal is not bound to a tenant.");
        }

        if (!string.IsNullOrWhiteSpace(state)
            && !string.Equals(state, "open", StringComparison.OrdinalIgnoreCase))
        {
            return EndpointResults.Invalid(
                "unsupported_review_state",
                "Only state=open is served. Resolved reviews are read by id, with their audit history.");
        }

        try
        {
            var page = await queue.ListAsync(new QueueListingQuery
            {
                TenantId = tenant,
                Filter = QueueListingFilter.Review,
                Limit = limit ?? QueueListingLimits.DefaultPageSize,
                After = after,
            }, cancellationToken).ConfigureAwait(false);

            var now = clock.GetUtcNow();
            var items = new List<ReviewItemResponse>(page.Items.Count);

            foreach (var item in page.Items)
            {
                var assessment = await ledger
                    .FindByMessageAsync(tenant, item.Envelope.InternalMessageId, cancellationToken)
                    .ConfigureAwait(false);

                items.Add(ToResponse(item, assessment?.Policy, assessment is null, now));
            }

            return Results.Ok(new ReviewListResponse
            {
                TenantId = tenant,
                Items = items,
                NextCursor = page.NextCursor,
            });
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable("The review queue could not be read, so no answer is being given.");
        }
    }

    /// <summary><c>GET /v1/policy/reviews/{queueId}</c>, one review with its audit history.</summary>
    internal static async Task<IResult> GetAsync(
        string queueId,
        ClaimsPrincipal user,
        QueueStore queue,
        IDecisionLedger ledger,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var tenant = user.TenantId();
        if (string.IsNullOrWhiteSpace(tenant))
        {
            return EndpointResults.Forbidden("tenant_required", "The principal is not bound to a tenant.");
        }

        try
        {
            return await DetailAsync(queueId, tenant, queue, ledger, clock, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable("The review could not be read, so no answer is being given.");
        }
    }

    /// <summary><c>POST /v1/policy/reviews/{queueId}/release</c>.</summary>
    internal static Task<IResult> ReleaseAsync(
        string queueId,
        ReviewResolutionRequest? request,
        ClaimsPrincipal user,
        QueueStore queue,
        IDecisionLedger ledger,
        TimeProvider clock,
        CancellationToken cancellationToken) =>
        ResolveAsync(queueId, ReviewResolution.Release, request, user, queue, ledger, clock, cancellationToken);

    /// <summary><c>POST /v1/policy/reviews/{queueId}/quarantine</c>.</summary>
    internal static Task<IResult> QuarantineAsync(
        string queueId,
        ReviewResolutionRequest? request,
        ClaimsPrincipal user,
        QueueStore queue,
        IDecisionLedger ledger,
        TimeProvider clock,
        CancellationToken cancellationToken) =>
        ResolveAsync(queueId, ReviewResolution.Quarantine, request, user, queue, ledger, clock, cancellationToken);

    /// <summary><c>POST /v1/policy/reviews/{queueId}/reject</c>, on Administer.</summary>
    internal static Task<IResult> RejectAsync(
        string queueId,
        ReviewResolutionRequest? request,
        ClaimsPrincipal user,
        QueueStore queue,
        IDecisionLedger ledger,
        TimeProvider clock,
        CancellationToken cancellationToken) =>
        ResolveAsync(queueId, ReviewResolution.Reject, request, user, queue, ledger, clock, cancellationToken);

    private static async Task<IResult> ResolveAsync(
        string queueId,
        ReviewResolution resolution,
        ReviewResolutionRequest? request,
        ClaimsPrincipal user,
        QueueStore queue,
        IDecisionLedger ledger,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var tenant = user.TenantId();
        var actor = user.PrincipalId();
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(actor))
        {
            return EndpointResults.Forbidden("tenant_required", "The principal is not bound to a tenant.");
        }

        var reason = request?.Reason;

        if (string.IsNullOrWhiteSpace(reason))
        {
            return EndpointResults.Invalid(
                "reason_required",
                "A review decision must state its reason. The actor and reason are the audit record.");
        }

        try
        {
            var item = await queue
                .GetItemForTenantAsync(queueId, tenant, cancellationToken)
                .ConfigureAwait(false);

            if (item is null)
            {
                return EndpointResults.NotFound("review_not_found", "No such review for this tenant.");
            }

            var assessment = await ledger
                .FindByMessageAsync(tenant, item.Envelope.InternalMessageId, cancellationToken)
                .ConfigureAwait(false);

            var applied = await queue.ResolveReviewAsync(
                queueId,
                resolution,
                actor,
                reason,
                assessment?.AssessmentId,
                PolicyReference(assessment?.Policy),
                tenant,
                cancellationToken).ConfigureAwait(false);

            if (!applied)
            {
                return EndpointResults.NotFound(
                    "review_not_open",
                    "That message is not awaiting review, so no decision was applied.");
            }

            return await DetailAsync(queueId, tenant, queue, ledger, clock, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable("The review decision could not be durably recorded, so it was not applied.");
        }
    }

    private static async Task<IResult> DetailAsync(
        string queueId,
        string tenant,
        QueueStore queue,
        IDecisionLedger ledger,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var item = await queue
            .GetItemForTenantAsync(queueId, tenant, cancellationToken)
            .ConfigureAwait(false);

        if (item is null)
        {
            return EndpointResults.NotFound("review_not_found", "No such review for this tenant.");
        }

        var events = await queue
            .ListReviewEventsAsync(tenant, queueId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!item.Recipients.Any(recipient => recipient.State == DeliveryState.Review)
            && events.Count == 0)
        {
            return EndpointResults.NotFound("review_not_found", "No such review for this tenant.");
        }

        var assessment = await ledger
            .FindByMessageAsync(tenant, item.Envelope.InternalMessageId, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new ReviewDetailResponse
        {
            Item = ToResponse(item, assessment?.Policy, assessment is null, clock.GetUtcNow()),
            Events = [.. events.Select(ReviewEventResponse.From)],
        });
    }

    private static ReviewItemResponse ToResponse(
        QueueItem item,
        StyloMail.Core.PolicyDecisionRecord? policy,
        bool policyRecordMissing,
        DateTimeOffset now) => new()
        {
            QueueId = item.QueueId,
            TenantId = item.TenantId,
            InternalMessageId = item.Envelope.InternalMessageId,
            Recipients = [.. item.Recipients.Select(recipient => ReviewRecipientResponse.From(recipient, now))],
            Policy = policy,
            PolicyRecordMissing = policyRecordMissing,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
        };

    private static string? PolicyReference(StyloMail.Core.PolicyDecisionRecord? policy) =>
        policy is null
            ? null
            : $"{policy.PackId}@{policy.PackVersion}/{policy.RuleSetId}@{policy.RuleSetVersion}";
}
