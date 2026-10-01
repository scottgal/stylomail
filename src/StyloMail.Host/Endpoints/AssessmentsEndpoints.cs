using System.Security.Claims;
using StyloMail.Core;
using StyloMail.Host.Assessors;
using StyloMail.Host.Contracts;
using StyloMail.Host.Decisions;
using StyloMail.Host.Hosting;
using StyloMail.Host.Observability;
using StyloMail.Host.Storage;
using StyloMail.Mime;

namespace StyloMail.Host.Endpoints;

/// <summary>
/// <c>POST /v1/assessments</c>, assess a message with no delivery and no learning.
/// </summary>
/// <remarks>
/// <para>
/// This route deliberately shares ingress checks with submission and deliberately shares nothing
/// else with it. It does not spool a payload, does not create queue state, and does not commit
/// trusted learning. Callers who want to participate in live traffic accounting use
/// <c>POST /v1/submissions</c> instead; the two are separate routes precisely so that the choice
/// is explicit rather than implied by a flag.
/// </para>
/// <para>
/// <b>It supplies the original bytes, because it stores none.</b> A submission spools its payload and
/// the assessor reads it back through the envelope's durable reference; an assessment writes nothing
/// durable, so that reference is deliberately ephemeral and resolves to nothing. The bytes this route
/// has already decoded are handed to the assessor instead, and that is not a convenience. Without
/// them the deterministic extraction never runs, no checkable evidence exists for the message, and
/// the policy engine holds every message whose risk index would otherwise have allowed it: measured,
/// the same bytes submitted both ways return <c>Allow</c> with twenty-two deterministic signals on
/// the submission route and <c>Hold</c> with none here.
/// <b>Assessing without deterministic evidence is a defect on this route, not a mode</b>, because
/// the two routes are handed the same message in the same shape and are expected to reach the same
/// decision about it.
/// </para>
/// </remarks>
internal static class AssessmentsEndpoints
{
    internal static async Task<IResult> AssessAsync(
        ClaimsPrincipal user,
        MessageSubmissionRequest request,
        IMimeMessageAnalyzer analyzer,
        IMailAssessor assessor,
        IDecisionLedger ledger,
        HostMetrics metrics,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (MessageIngress.RejectTenantMismatch(user, request.TenantId) is { } tenantError)
        {
            return tenantError;
        }

        if (MessageIngress.RejectUnpermittedShadow(user, request.ShadowMode) is { } shadowError)
        {
            return shadowError;
        }

        // Checked before any parsing work: with no assessor there is nothing this route can
        // produce, and doing the work first would only obscure that.
        if (assessor is UnavailableMailAssessor)
        {
            metrics.AssessorUnavailable();
            return EndpointResults.AssessorUnavailable();
        }

        var internalMessageId = $"msg_{Guid.NewGuid():N}";

        // Assessment-only: there is no durable payload, and claiming a spool reference we never
        // wrote would be a lie. Core's canonical ephemeral reference says exactly that, a single
        // shared value rather than a per-message string, so "this call stored nothing" is greppable
        // and cannot be mistaken for a reference that resolves to something.
        var (prepared, error) = MessageIngress.Prepare(
            user,
            request,
            analyzer,
            clock,
            payloadReference: PayloadReferences.Ephemeral,
            internalMessageId);

        if (error is not null)
        {
            return error;
        }

        var context = MessageIngress.BuildAssessmentContext(
            user,
            shadowMode: request.ShadowMode,
            assessmentOnly: true,
            clock);

        // The decoded body travels with the call. The envelope's reference is ephemeral on this route
        // by design, so without this the assessor reaches no bytes at all and the parse it would have
        // done for itself never happens: nothing durable is written by handing them over, and the
        // assessor reads them only after its own source has come back empty.
        var assessment = await assessor.AssessAsync(
            prepared!.Analysis,
            context,
            cancellationToken,
            prepared.RawBytes);

        // Recording the decision is part of assessing, not a delivery side effect: an assessment
        // nobody can look up afterwards is not explainable. If it cannot be recorded we do not
        // hand back an id that will never resolve, we report the failure instead.
        try
        {
            await ledger.RecordAsync(assessment, cancellationToken);
        }
        catch (StorageUnavailableException)
        {
            metrics.StorageUnavailable();
            return EndpointResults.StorageUnavailable(
                "The decision could not be durably recorded, so no assessment is being returned.");
        }

        metrics.AssessmentCompleted();
        return Results.Ok(DecisionResponse.From(assessment));
    }
}
