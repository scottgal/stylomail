using System.Security.Cryptography;
using StyloMail.Core;
using StyloMail.Host.Assessors;
using StyloMail.Queue;
using StyloMail.Transport.Ingress;

namespace StyloMail.Host.Hosting;

/// <summary>
/// The host's answer to <see cref="ISmtpIngressSink"/>: the one component that turns a message an
/// ingress has finished reading into an assessment, and the assessment into an SMTP reply.
/// </summary>
/// <remarks>
/// <para>
/// <b>It does not accept. The assessor accepts.</b> This type is handed an
/// <see cref="IMailAssessor"/> and a <see cref="SpoolStore"/> and nothing else, there is no queue
/// and no intake in its constructor, so the double-accept that cost this project a day of duplicate
/// mail is not something this class can do even by mistake. A sink that "runs the pipeline, then
/// accepts" accepts under a second idempotency key, the queue cannot see the two as the same
/// submission, and the message is delivered twice. The pipeline's step seven already discharged
/// that contract; this class reads its answer.
/// </para>
///
/// <para>
/// <b>Why it spools before assessing.</b> The pipeline parses the message from the original bytes,
/// reached through <c>MailEnvelope.PayloadReference</c>, and refuses outright to make a non-durable
/// reference durable. So the bytes have to be on disk <em>before</em> the assessor is called, under
/// a <c>spool://</c> reference that resolves. The queue then writes its own copy under the queue id
/// it mints, that second write is the acceptance, and it is the only one that becomes delivery
/// state. This ingress copy is therefore never referenced by queue metadata and is left for the
/// orphan sweep; deleting it once acceptance succeeds is a separate change, deliberately not made
/// here (see the handover for the two questions still open with <c>queue-</c>).
/// </para>
///
/// <para>
/// <b>No client idempotency key, and that is the honest answer rather than a gap.</b> An SMTP
/// client supplies no key and this component will not mint one: a synthesised key looks like replay
/// protection while providing none, because a retry would carry a different one. The duplicate that
/// a lost acknowledgement can produce is the SMTP ambiguity the spec accepts, not something to
/// paper over here.
/// </para>
/// </remarks>
public sealed class HostIngressSink : ISmtpIngressSink
{
    private readonly IMailAssessor _assessor;
    private readonly SpoolStore _spool;
    private readonly TimeProvider _clock;

    /// <param name="assessor">
    /// The composition root's assessor. It is the only acceptor, see the class remarks.
    /// </param>
    /// <param name="spool">
    /// <b>The composition root's spool, not a new one.</b> A second instance would put this
    /// component's writes somewhere the assessor's read-back, the queue's orphan sweep and the
    /// delete-after-accept path do not look, each correct in isolation, together silent. The
    /// composition root asserts the identity; see <see cref="IngressComposition.RequireSharedSpool"/>.
    /// </param>
    public HostIngressSink(IMailAssessor assessor, SpoolStore spool, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(assessor);
        ArgumentNullException.ThrowIfNull(spool);

        _assessor = assessor;
        _spool = spool;
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The spool this sink writes through, so the composition root can check its identity.</summary>
    public SpoolStore Spool => _spool;

    /// <summary>
    /// The analysis view handed to the assessor when this component has parsed nothing.
    /// </summary>
    /// <remarks>
    /// Every field says "nothing was extracted here" rather than "nothing was found". The pipeline's
    /// own parser is authoritative and replaces this view entirely whenever the payload resolves,     /// which for an ingress message it always does, because the bytes were just spooled. This value
    /// is what remains if that read fails, and it is deliberately not a cheerful one.
    /// </remarks>
    private static readonly AnalysisCoverage Unparsed = new()
    {
        BodyParsed = false,
        HtmlPresent = false,
        HasAttachments = false,
        HtmlTextDisagreement = false,
        ParserLimitExceeded = false,
        OversizeRejected = false,
        ContentEncrypted = false,
        Truncated = false,
        ConversationContextMissing = true,
    };

    public async ValueTask<IngressDecision> SubmitAsync(
        IngressSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);

        var payloadReference = await SpoolAsync(submission, cancellationToken).ConfigureAwait(false);
        if (payloadReference is null)
        {
            return IngressDecision.Defer(
                "The message could not be durably stored, so responsibility was not transferred.");
        }

        var envelope = new MailEnvelope
        {
            InternalMessageId = submission.InternalMessageId,
            TenantId = submission.TenantId,
            Direction = submission.Direction,

            // Identity comes from the boundary, never from the message. For a submission that is
            // the authenticated principal; for an inbound handoff it is the connector's own name.
            TrustedPrincipalId = submission.TrustedPrincipalId,
            MailFrom = submission.MailFrom,
            RcptTo = submission.Recipients,
            ReceivedAt = _clock.GetUtcNow(),

            // Over the bytes as received, including the hop marker the ingress prepended, the
            // digest has to describe the artefact actually being handed on, not an earlier form of it.
            MimeDigest = Convert.ToHexString(SHA256.HashData(submission.RawMessage.Span)),
            PayloadReference = payloadReference,
            UntrustedMessageIdHeader = submission.UntrustedMessageIdHeader,

            // Always observed, never null. Both ingresses scan the message's own Received headers
            // before they call here and refuse an over-limit message themselves, so by this point a
            // count exists and is meaningful. The distinction the nullable type carries, "not
            // observed" is not zero, is for callers that genuinely did not look; this one did, and
            // reporting null would tell the queue its hop backstop had not run when it had.
            HopCount = submission.HopCount,
        };

        var input = new MailAnalysisInput
        {
            Envelope = envelope,
            Authentication = submission.Authentication,
            Channel = ChannelContext.Email,
            Subject = null,
            BodyText = string.Empty,
            QuotedText = null,
            Links = [],
            Attachments = [],
            ConversationContext = null,
            Coverage = Unparsed,
        };

        var context = new AssessmentContext
        {
            TenantId = submission.TenantId,

            // Shadow is an operator observation mode selected per request. An ingress message has no
            // request to select it on, and inventing one would let a connector record a different
            // action from the one it took.
            ShadowMode = false,

            // The whole point of this path: not assessment-only. This call is where delivery
            // responsibility is taken on, so the pipeline runs its acceptance step.
            AssessmentOnly = false,
            CorrelationId = "cor_" + Guid.NewGuid().ToString("N"),

            // Null for every ingress path, deliberately, an SMTP session has no client-supplied key
            // and the transport cannot invent one. See the class remarks.
            ClientIdempotencyKey = null,
            TimeProvider = _clock,
        };

        MailAssessment assessment;

        try
        {
            assessment = await _assessor
                .AssessAsync(input, context, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AssessorUnavailableException)
        {
            // This deployment has no assessor configured, so nothing was examined and nothing was
            // stored. A 4xx leaves the message with the client, which is the only recoverable answer.
            //
            // Any other exception is deliberately allowed to escape: both ingresses wrap the call
            // and convert an unclassified failure into a deferral, and repeating that rule here
            // would be a second place for it to be got wrong.
            return IngressDecision.Defer(
                "No assessor is configured on this host, so the message was not accepted.");
        }

        return Decide(assessment);
    }

    /// <summary>
    /// Places the original bytes beyond loss, or reports that it could not.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing so the caller has exactly one place where "not durable"
    /// is turned into a deferral. <see cref="SpoolUnavailableException"/> is the spool's own signal
    /// for disk full, an unmounted volume or a permissions change, every one of which means
    /// "do not accept", because accepting here would destroy mail we cannot produce.
    /// </remarks>
    private async Task<string?> SpoolAsync(IngressSubmission submission, CancellationToken cancellationToken)
    {
        try
        {
            return await _spool
                .WriteAsync(
                    submission.TenantId,

                    // Prefixed so an ingress payload is distinguishable on sight from the queue's own
                    // copy of the same message, which is named by its queue id. The name comes from
                    // the ingress's own message id, which is minted per transaction, so a retry of
                    // the same SMTP transaction is a fresh message here, exactly as it is downstream.
                    "ingress-" + submission.InternalMessageId,
                    submission.RawMessage,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SpoolUnavailableException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the outcome off the assessment and expresses it in SMTP's terms.
    /// </summary>
    /// <remarks>
    /// <b>The queue id decides, and nothing else may.</b> A non-null
    /// <see cref="MailAssessment.SubmissionId"/> is the only evidence that a durable row exists, so
    /// it is the only thing that produces a <c>250</c>. The action is consulted afterwards and only
    /// to choose between the two ways of declining, never to upgrade a decline into an acceptance,
    /// which is why <c>Allow</c> is not handled as a success case. An action of <c>Allow</c> with no
    /// queue id means the pipeline examined the message and could not take it on; the truthful reply
    /// is a deferral, and answering <c>250</c> there would tell the client to delete mail we do not have.
    /// </remarks>
    private static IngressDecision Decide(MailAssessment assessment)
    {
        if (assessment.SubmissionId is { Length: > 0 } queueId)
        {
            return IngressDecision.Accepted(queueId);
        }

        return assessment.Action == MailAction.Reject
            ? IngressDecision.Reject(550, "5.7.1", Explain(assessment))
            : IngressDecision.Defer(Explain(assessment));
    }

    /// <summary>
    /// Summarises a decline by reason <em>code</em>, never by reason prose.
    /// </summary>
    /// <remarks>
    /// Reason messages are written for an operator reading the decision ledger and are free to name
    /// internal facts, a spool path, a tenant, a component. This string is written into an SMTP
    /// reply that goes to whoever is on the other end of the socket, so it carries the stable
    /// identifier and nothing else. The detail stays where it belongs.
    /// </remarks>
    private static string Explain(MailAssessment assessment)
    {
        var code = assessment.Reasons.Count > 0
            ? assessment.Reasons[0].Code
            : "assessment.no_reason_supplied";

        return $"Not accepted; responsibility did not transfer ({code}).";
    }
}
