using System.Security.Claims;
using StyloMail.Core;
using StyloMail.Host.Auth;
using StyloMail.Host.Contracts;
using StyloMail.Host.Controls;
using StyloMail.Host.Decisions;
using StyloMail.Host.Feedback;
using StyloMail.Host.Observability;
using StyloMail.Host.Storage;
using StyloMail.Host.Submissions;

namespace StyloMail.Host.Endpoints;

/// <summary>
/// <c>POST /v1/feedback</c>, an authorised label, with the scope it applies to.
/// </summary>
internal static class FeedbackEndpoints
{
    internal static async Task<IResult> SubmitAsync(
        FeedbackRequest request,
        ClaimsPrincipal user,
        IFeedbackStore feedback,
        IDecisionLedger ledger,
        HostMetrics metrics,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (request.Scope is not { } scope)
        {
            return EndpointResults.Invalid(
                "scope_required",
                "Feedback must state the scope it applies to. A label with no scope is an unbounded one.");
        }

        if (string.IsNullOrWhiteSpace(request.DecisionId))
        {
            return EndpointResults.Invalid("decision_required", "Feedback must reference a decision.");
        }

        // A label binds to a decision, so the decision has to exist and be this tenant's. This is
        // also what stops feedback being used to probe for another tenant's decision ids.
        var decision = await ledger.FindAsync(user.TenantId()!, request.DecisionId, cancellationToken)
            .ConfigureAwait(false);

        if (decision is null)
        {
            return EndpointResults.NotFound(
                "decision_not_found",
                "No decision with that identifier is recorded for this tenant.");
        }

        var entry = new FeedbackEntry
        {
            FeedbackId = $"fbk_{Guid.NewGuid():N}",
            TenantId = user.TenantId()!,
            DecisionId = request.DecisionId,
            Scope = scope,
            Label = request.Label,
            Recipient = request.Recipient,
            RecordedBy = user.PrincipalId() ?? "unknown",
            RecordedAt = clock.GetUtcNow(),
            Note = request.Note,
        };

        try
        {
            await feedback.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable(
                "The feedback could not be durably recorded, so it was not accepted.");
        }

        metrics.FeedbackRecorded();

        return Results.Ok(new FeedbackResponse
        {
            FeedbackId = entry.FeedbackId,
            DecisionId = entry.DecisionId,
            Scope = entry.Scope,
            Label = entry.Label,
            Recipient = entry.Recipient,
            RecordedAt = entry.RecordedAt,
        });
    }
}

/// <summary>
/// <c>POST /v1/quarantine/{id}/release</c>, audited, idempotent release.
/// </summary>
/// <remarks>
/// Requires the review privilege, which a sending principal does not hold. Releasing your own
/// quarantine is precisely the action the privilege split exists to prevent, so this route is the
/// clearest expression of it in the surface.
/// </remarks>
internal static class QuarantineEndpoints
{
    internal static async Task<IResult> ReleaseAsync(
        string id,
        ClaimsPrincipal user,
        ISubmissionIntake intake,
        HostMetrics metrics,
        CancellationToken cancellationToken)
    {
        var tenantId = user.TenantId()!;
        var releasedBy = user.PrincipalId() ?? "unknown";

        var item = await intake.GetAsync(id, tenantId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return EndpointResults.NotFound(
                "submission_not_found",
                "No submission with that identifier is held for this tenant.");
        }

        bool performed;
        try
        {
            performed = await intake
                .ResolveQuarantineAsync(id, tenantId, releasedBy, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable(
                "The release could not be durably recorded, so it was not applied.");
        }

        if (!performed)
        {
            // Nothing was quarantined to release. That is the expected outcome of a retry, so it is
            // not an error, but the message must genuinely not be quarantined, or we would be
            // reporting a release that did not happen.
            var current = await intake.GetAsync(id, tenantId, cancellationToken).ConfigureAwait(false);
            var stillQuarantined = current is not null
                && current.Recipients.Any(r => r.State == DeliveryState.Quarantined);

            if (stillQuarantined)
            {
                return EndpointResults.Conflict(
                    "not_released",
                    "The submission is still quarantined; the release was not applied.");
            }
        }

        metrics.QuarantineReleased();

        return Results.Ok(new QuarantineReleaseResponse
        {
            QueueId = id,
            Released = performed,
            ReleasedBy = releasedBy,
        });
    }
}

/// <summary>
/// <c>POST /v1/controls/senders/{id}/pause</c>.
/// </summary>
/// <remarks>
/// Requires <c>Administer</c>. A pause stops an account's outbound delivery, which makes it a
/// control-plane action rather than something a sending principal authorises for itself.
/// </remarks>
internal static class ControlsEndpoints
{
    internal static async Task<IResult> PauseSenderAsync(
        string id,
        PauseSenderRequest? request,
        ClaimsPrincipal user,
        ISenderControlStore controls,
        HostMetrics metrics,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return EndpointResults.Invalid("principal_required", "A principal identifier is required.");
        }

        var updatedBy = user.PrincipalId() ?? "unknown";

        try
        {
            await controls
                .PauseAsync(user.TenantId()!, id, request?.Reason ?? string.Empty, updatedBy, clock.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable(
                "The pause could not be durably recorded, so it was not applied.");
        }

        metrics.SenderPaused();

        return Results.Ok(new PauseSenderResponse
        {
            PrincipalId = id,
            Paused = true,
            UpdatedBy = updatedBy,
        });
    }

    /// <summary>
    /// <c>POST /v1/controls/senders/{id}/resume</c>, the audited mirror of the pause.
    /// </summary>
    /// <remarks>
    /// Same <c>Administer</c> privilege as the pause: lifting an intervention is no less a
    /// control-plane act than imposing one, and a route that let anyone undo a containment
    /// decision would be a way around the privilege rather than a convenience within it.
    /// </remarks>
    internal static async Task<IResult> ResumeSenderAsync(
        string id,
        PauseSenderRequest? request,
        ClaimsPrincipal user,
        ISenderControlStore controls,
        HostMetrics metrics,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return EndpointResults.Invalid("principal_required", "A principal identifier is required.");
        }

        var updatedBy = user.PrincipalId() ?? "unknown";

        try
        {
            await controls
                .ResumeAsync(user.TenantId()!, id, request?.Reason ?? string.Empty, updatedBy, clock.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable(
                "The resume could not be durably recorded, so it was not applied.");
        }

        metrics.SenderResumed();

        return Results.Ok(new ResumeSenderResponse
        {
            PrincipalId = id,
            Paused = false,
            UpdatedBy = updatedBy,
        });
    }
}
