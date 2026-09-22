using StyloMail.Queue;

namespace StyloMail.Host.Hosting;

/// <summary>
/// The two couplings the ingress has with components it does not own, checked rather than described.
/// </summary>
/// <remarks>
/// <para>
/// Both of these are properties of the <em>pair</em> of components, and neither is visible to a
/// reader of either one alone. That is the whole reason they are asserted here: a drift between them
/// produces a symptom that points somewhere else, and the symptom is the kind that gets explained
/// away rather than investigated.
/// </para>
/// <para>
/// These are composition-time checks, not request-time ones. They run once, where the components are
/// built, so a mismatch is a startup failure with both values in the message, not a message that
/// behaves oddly under load months later.
/// </para>
/// </remarks>
public static class IngressComposition
{
    /// <summary>
    /// An ingress may not be willing to take messages the queue is unwilling to store.
    /// </summary>
    /// <remarks>
    /// <b>If <c>transportMax &gt; queueMax</c>, the failure presents as something else entirely.</b>
    /// The ingress reads the message happily and answers the client that it is authorised; the sink
    /// spools it; the queue refuses it for its own size limit; and the deferral that comes back
    /// looks like spool pressure or an admission problem. The actual cause is a size-policy
    /// mismatch two components away, and nothing in the symptom names it.
    ///
    /// <para>
    /// The tighter bound must win, and today the two are deliberately equal. Asserted rather than
    /// documented because the coupling lives in nobody's file: an operator raising the SMTP limit
    /// sees only the SMTP limit.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The ingress bound exceeds the queue's.</exception>
    public static void RequireIngressFitsQueue(
        string ingressName,
        long ingressMaxMessageBytes,
        QueueOptions queueOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ingressName);
        ArgumentNullException.ThrowIfNull(queueOptions);

        if (ingressMaxMessageBytes <= queueOptions.MaxPayloadBytes)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{ingressName} accepts messages up to {ingressMaxMessageBytes} bytes, but the delivery " +
            $"queue refuses any payload over {queueOptions.MaxPayloadBytes} bytes. A message between " +
            "those two sizes would be read and authorised by the ingress, spooled by the sink, and " +
            "then refused by the queue, so the caller sees a capacity deferral that looks like spool " +
            "pressure while the cause is this mismatch. Lower the ingress limit to the queue's, or " +
            "raise MaxPayloadBytes with it.");
    }

    /// <summary>
    /// The ingress must write through the very same spool the pipeline reads back from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Compared by instance, not by path.</b> The harm this guards is one directory becoming two:
    /// the sink spools a payload into one root while the assessor resolves the reference against
    /// another, so the queue's orphan sweep, the assessor's read-back and any later
    /// delete-after-accept each reason about a different filesystem and none of them is wrong on its
    /// own. Compare paths and a second instance over the same directory passes a check it should not
    /// need to pass, the fix is to hand over the composition root's instance, so that is what is
    /// asserted.
    /// </para>
    /// <para>
    /// The transport has no spool at all by design, so there is no third instance anywhere to
    /// reconcile: the composition root's instance is the only one.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The two are not the same instance.</exception>
    public static void RequireSharedSpool(
        SpoolStore ingressSpool,
        SpoolStore pipelineSpool,
        string pipelineDescription)
    {
        ArgumentNullException.ThrowIfNull(ingressSpool);
        ArgumentNullException.ThrowIfNull(pipelineSpool);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineDescription);

        if (ReferenceEquals(ingressSpool, pipelineSpool))
        {
            return;
        }

        throw new InvalidOperationException(
            $"The ingress sink writes payloads through the spool rooted at '{ingressSpool.Root}', but " +
            $"{pipelineDescription} reads them back through a different spool instance rooted at " +
            $"'{pipelineSpool.Root}'. The assessor's read-back, the queue's orphan sweep and any " +
            "delete-after-accept would each be reasoning about a different directory, each correct " +
            "alone, and silent together. The sink must be given the composition root's spool, not one " +
            "it constructs.");
    }

    /// <summary>
    /// The spool everything shares must be the spool this deployment actually configured.
    /// </summary>
    /// <remarks>
    /// The second half of the same rule, and it is a genuinely separate check rather than a
    /// restatement. <see cref="RequireSharedSpool"/> establishes that there is only <em>one</em>
    /// spool; this establishes that the one is rooted where the deployment says. A single instance
    /// pointed at the wrong directory satisfies the first and fails the second, and it would put
    /// every payload somewhere the configured spool root, the operator's backup and any retention
    /// job do not look.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The roots differ.</exception>
    public static void RequireSpoolRoot(SpoolStore spool, string configuredSpoolRoot)
    {
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredSpoolRoot);

        // Compared as resolved paths so that a relative and an absolute spelling of one directory
        // are recognised as the same place, rather than reported as a mismatch that is not one.
        var actual = Path.GetFullPath(spool.Root);
        var expected = Path.GetFullPath(configuredSpoolRoot);

        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"The spool the assessment pipeline and the ingress share is rooted at '{actual}', but " +
            $"this deployment configures its spool root as '{expected}'. Payloads would be written " +
            "outside the configured spool, where retention and backup do not reach them.");
    }
}
