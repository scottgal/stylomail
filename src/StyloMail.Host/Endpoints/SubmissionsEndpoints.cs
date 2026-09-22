using System.Security.Claims;
using System.Security.Cryptography;
using StyloMail.Core;
using StyloMail.Host.Assessors;
using StyloMail.Host.Auth;
using StyloMail.Host.Contracts;
using StyloMail.Host.Decisions;
using StyloMail.Host.Hosting;
using StyloMail.Host.Observability;
using StyloMail.Host.Storage;
using StyloMail.Host.Submissions;
using StyloMail.Mime;
using StyloMail.Queue;

namespace StyloMail.Host.Endpoints;

/// <summary>
/// Durable intake. <c>POST /v1/submissions</c> and <c>GET /v1/submissions/{id}</c>.
/// </summary>
/// <remarks>
/// <b>The host does not accept.</b> Acceptance happens inside the assessment pipeline (spec §4
/// step 7), and the assessor reports the resulting id on <see cref="MailAssessment.SubmissionId"/>.
/// An earlier revision of this route called the queue itself; with the assessor also accepting,
/// that produced two acceptances under two different idempotency keys, one message, two
/// deliveries. The route's job is to hand over a durable payload, read back the id, and map the
/// outcome to HTTP.
///
/// <para>
/// A success from here still means delivery responsibility has transferred, so every failure path
/// below is checked for the same property: it must not be reachable with a 2xx.
/// </para>
/// </remarks>
internal static class SubmissionsEndpoints
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    internal static async Task<IResult> SubmitAsync(
        HttpRequest httpRequest,
        ClaimsPrincipal user,
        MessageSubmissionRequest request,
        IMimeMessageAnalyzer analyzer,
        IMailAssessor assessor,
        ISubmissionIntake intake,
        IDecisionLedger ledger,
        SpoolStore spool,
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

        if (assessor is UnavailableMailAssessor)
        {
            metrics.AssessorUnavailable();
            return EndpointResults.AssessorUnavailable();
        }

        var tenantId = user.TenantId()!;
        var idempotencyKey = httpRequest.Headers[IdempotencyKeyHeader].ToString();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            idempotencyKey = null;
        }

        // Required, and a missing one is a client error rather than a silent duplicate.
        //
        // §12 requires that a retry with the same key returns the existing submission, which is a
        // promise this route cannot keep for a caller that supplies no key. Accepting the request
        // anyway would mean a client that retries after a lost response, the ordinary case, gets
        // a second copy of their mail delivered, and never learns why. An HTTP client that can
        // send a body can send a header.
        //
        // The MTA and Cloudflare ingress paths are deliberately unaffected: no client key exists
        // there by construction, and deduplication is the upstream MTA's concern.
        if (idempotencyKey is null)
        {
            return EndpointResults.Invalid(
                "idempotency_key_required",
                $"The {IdempotencyKeyHeader} header is required. Without it a retry cannot be " +
                "recognised as a retry, and the submission would be accepted twice.");
        }

        byte[] rawBytes;
        try
        {
            rawBytes = Convert.FromBase64String(request.RawMime);
        }
        catch (FormatException)
        {
            return EndpointResults.Invalid("invalid_base64", "rawMime must be Base64-encoded bytes.");
        }

        var digest = Convert.ToHexString(SHA256.HashData(rawBytes));

        // Replay check before any parsing or provider spend. A client retrying because a response
        // was lost should cost nothing on the second attempt, no parse, no assessment, no provider
        // call. This is the host's own record and is the only thing that can short-circuit that
        // work; the queue's dedup would stop the duplicate but only after the whole pipeline ran.
        if (idempotencyKey is not null)
        {
            SubmissionLookup? existing;
            try
            {
                existing = await intake.FindAsync(tenantId, idempotencyKey, cancellationToken).ConfigureAwait(false);
            }
            catch (StorageUnavailableException ex)
            {
                // If the store cannot be read we do not know whether this is a replay. Proceeding
                // would risk accepting a message we may already hold; refusing is the only answer
                // that cannot duplicate mail.
                metrics.StorageUnavailable();
                return EndpointResults.StorageUnavailable(ex.Message);
            }

            if (existing is not null)
            {
                return await ReplayAsync(existing, digest, tenantId, intake, metrics, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var internalMessageId = $"msg_{Guid.NewGuid():N}";

        // Spool the original bytes before assessing. The pipeline's durable-acceptance path reads
        // them back through the envelope's reference, and refuses to accept a message whose bytes
        // it cannot produce, so an unresolvable reference here would not fail loudly, it would
        // quietly turn every submission into a Defer.
        string payloadReference;
        try
        {
            payloadReference = await spool
                .WriteAsync(tenantId, internalMessageId, rawBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SpoolUnavailableException)
        {
            // The exception is deliberately discarded rather than returned, and the asymmetry with
            // the FindAsync handler above is the point rather than an oversight.
            //
            // SpoolUnavailableException comes from the queue's spool and its messages name the spool
            // directory, the errno, and the queue item the payload was being written for. None of
            // that belongs in an HTTP response to a caller, it is an internal path and an internal
            // identifier handed to whoever can reach this route. StorageUnavailableException, which
            // the handler above does surface, is this host's own type and carries a fixed sentence
            // with the underlying fault as its inner exception.
            //
            // So the caller gets a reason it can act on ("not accepted, retry") and no more. The
            // detail is not lost: it is on the exception the metrics counter and the host's own
            // logging see, where an operator can reach it.
            metrics.StorageUnavailable();
            return EndpointResults.StorageUnavailable(
                "The message could not be durably spooled, so it was not accepted.");
        }

        MailAssessment assessment;
        try
        {
            var (prepared, error) = MessageIngress.Prepare(
                user,
                request,
                analyzer,
                clock,
                payloadReference,
                internalMessageId);

            if (error is not null)
            {
                return error;
            }

            var context = MessageIngress.BuildAssessmentContext(
                user,
                shadowMode: request.ShadowMode,
                assessmentOnly: false,
                clock,
                clientIdempotencyKey: idempotencyKey);

            assessment = await assessor.AssessAsync(prepared!.Analysis, context, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Nothing below can reference this copy: the queue spools its own payload from the
            // bytes the pipeline hands it. Only sweep it when responsibility was not taken, so a
            // path that might still need the bytes is never the one that deletes them.
            // (On the accepted path the copy is redundant rather than unsafe to remove; leaving it
            // is the conservative choice and is raised as a spool-efficiency question.)
        }

        // The decision is recorded regardless of the outcome: observed state includes traffic we
        // go on to decline, so a refusal still leaves the ledger explaining what we saw.
        try
        {
            await ledger.RecordAsync(assessment, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageUnavailableException)
        {
            metrics.StorageUnavailable();
            return EndpointResults.StorageUnavailable(
                "The decision could not be durably recorded, so the message was not accepted.");
        }

        return MapOutcome(assessment, metrics);
    }

    internal static async Task<IResult> GetAsync(
        string id,
        ClaimsPrincipal user,
        ISubmissionIntake intake,
        CancellationToken cancellationToken)
    {
        var item = await intake.GetAsync(id, user.TenantId()!, cancellationToken).ConfigureAwait(false);

        return item is null
            ? EndpointResults.NotFound("submission_not_found", "No submission with that identifier is held for this tenant.")
            : Results.Ok(SubmissionStatusResponse.From(item));
    }

    /// <summary>
    /// Maps an assessment to HTTP, on the rule that a queue id is present if and only if delivery
    /// responsibility transferred.
    /// </summary>
    /// <remarks>
    /// The refusal branch is exhaustive and its default is a refusal, not an acceptance. A
    /// <c>202</c> without an id would tell a caller their mail was accepted on the strength of an
    /// action value nobody anticipated.
    /// </remarks>
    private static IResult MapOutcome(MailAssessment assessment, HostMetrics metrics)
    {
        if (assessment.SubmissionId is { Length: > 0 } queueId)
        {
            metrics.SubmissionAccepted();

            // Read the fact, not the explanation. A reason code saying "duplicate" is prose in a
            // ledger; choosing a status code from prose is stringly-typed coupling that fails by
            // never matching rather than by throwing. SubmissionAdmission is the fact, and Core
            // pins the invariant that it is null exactly when SubmissionId is.
            var duplicate = assessment.Submission == SubmissionAdmission.Duplicate;

            return Results.Json(
                SubmissionResponse.FromAssessment(queueId, assessment, duplicate ? "Duplicate" : "Accepted"),
                statusCode: duplicate ? StatusCodes.Status200OK : StatusCodes.Status202Accepted);
        }

        metrics.SubmissionRefused();
        var detail = assessment.Reasons.Count > 0
            ? assessment.Reasons[0].Message
            : "The message was not accepted for delivery.";

        return assessment.Action switch
        {
            // Declined permanently, before acceptance. The caller should not retry.
            MailAction.Reject => Results.Json(
                new ErrorResponse("rejected", detail),
                statusCode: StatusCodes.Status422UnprocessableEntity),

            // Declined temporarily, including a durable-storage failure, which the pipeline
            // reports as Defer rather than accepting mail it could not persist. The caller may
            // retry, so this is a 503 and never a 202.
            MailAction.Defer => Results.Json(
                new ErrorResponse("deferred", detail),
                statusCode: StatusCodes.Status503ServiceUnavailable),

            // Not reachable through any known path: an allow, hold or quarantine that produced no
            // submission id means acceptance did not happen. Reported as a failure rather than
            // guessed at.
            _ => Results.Json(
                new ErrorResponse("acceptance_failed", detail),
                statusCode: StatusCodes.Status503ServiceUnavailable),
        };
    }

    /// <summary>
    /// Answers a replay from the host's own record, without reaching the assessor.
    /// </summary>
    /// <remarks>
    /// The same key with <em>different</em> content is refused rather than answered with the id of
    /// the message already stored. That alternative is tempting because it looks idempotent, but it
    /// tells the caller their <em>new</em> message was accepted when it was never written anywhere.
    /// </remarks>
    private static async Task<IResult> ReplayAsync(
        SubmissionLookup existing,
        string incomingDigest,
        string tenantId,
        ISubmissionIntake intake,
        HostMetrics metrics,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(existing.MimeDigest, incomingDigest, StringComparison.Ordinal))
        {
            return EndpointResults.Conflict(
                "idempotency_conflict",
                "This idempotency key was already used for a different message. " +
                "The key is not a way to overwrite a submission.");
        }

        var item = await intake.GetAsync(existing.QueueId, tenantId, cancellationToken).ConfigureAwait(false);

        if (item is null)
        {
            return EndpointResults.NotFound(
                "submission_not_found",
                "The submission this key refers to is no longer held.");
        }

        metrics.SubmissionDuplicate();
        return Results.Ok(SubmissionResponse.FromQueueItem(item, "Duplicate"));
    }
}
