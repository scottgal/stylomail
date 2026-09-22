namespace StyloMail.Transport.Smtp;

/// <summary>How far through the protocol a conversation had got.</summary>
/// <remarks>
/// Recorded on every outcome, not just failures. The stage is what makes an <see cref="SmtpDeliveryStage.InDoubt"/>
/// distinguishable from a plain failure, and it is the first thing an operator needs when an
/// upstream starts refusing mail: "it never got past EHLO" and "it was rejected at RCPT TO" are
/// different incidents with different fixes.
/// </remarks>
public enum SmtpDeliveryStage
{
    Connect = 0,
    Greeting = 1,
    Ehlo = 2,
    StartTls = 3,
    Auth = 4,
    MailFrom = 5,
    RcptTo = 6,

    /// <summary>The <c>DATA</c> verb was accepted; the body has not been written yet.</summary>
    DataCommand = 7,

    /// <summary>The body was being written when the failure happened.</summary>
    DataBody = 8,

    /// <summary>
    /// The end-of-data terminator was written. <b>The message may already be accepted.</b>
    /// </summary>
    DataTerminator = 9,

    /// <summary>The final <c>250</c> after DATA was outstanding.</summary>
    FinalReply = 10,
}

/// <summary>What happened to one recipient's copy of a message.</summary>
/// <remarks>
/// The three failure kinds are deliberately distinct, because collapsing them loses the one piece
/// of information the queue's retry policy runs on:
///
/// <list type="bullet">
/// <item><see cref="TransientRejection"/>, a 4xx. The upstream says "not now". Retrying is correct.</item>
/// <item><see cref="PermanentRejection"/>, a 5xx. Retrying cannot help, and the spec forbids
/// inventing a bounce to tell the sender, so this is recorded and left to the upstream's DSN policy.</item>
/// <item><see cref="Failed"/>, we never got far enough for the upstream to have an opinion. A
/// connection or protocol fault; nothing was committed.</item>
/// <item><see cref="InDoubt"/>, <b>the message may have been delivered and we will never know.</b>
/// See the remarks on <see cref="SmtpTransactionResult.InDoubt"/>.</item>
/// </list>
/// </remarks>
public enum SmtpTransactionOutcome
{
    /// <summary>The upstream returned a 2xx after the body. Delivery responsibility has transferred.</summary>
    Accepted = 0,

    /// <summary>A 4xx. Retryable.</summary>
    TransientRejection = 1,

    /// <summary>A 5xx. Not retryable for this recipient.</summary>
    PermanentRejection = 2,

    /// <summary>A connection, TLS, authentication or protocol fault before the message was committed.</summary>
    Failed = 3,

    /// <summary>
    /// The end-of-data terminator was written and the acknowledgement never arrived.
    /// </summary>
    /// <remarks>
    /// <b>The ambiguity the spec requires us to surface rather than eliminate.</b> Upstream may have
    /// accepted the message and lost only the reply. Reporting this as a plain failure would invite
    /// a silent drop; reporting it as success would invite a silent loss. It is reported as what it
    /// is and the caller decides, the queue records it as <c>InDoubt</c> and retries, because a
    /// duplicate is recoverable and a silent loss is not.
    /// </remarks>
    InDoubt = 4,
}

/// <summary>The result of one SMTP transaction for one recipient.</summary>
public sealed record SmtpTransactionResult
{
    public required SmtpTransactionOutcome Outcome { get; init; }

    /// <summary>How far the conversation got. Meaningful for every outcome.</summary>
    public required SmtpDeliveryStage Stage { get; init; }

    /// <summary>The upstream's reply, when one was received.</summary>
    public SmtpReply? Reply { get; init; }

    /// <summary>Human-readable explanation for the ledger and the operator surface.</summary>
    public string? Detail { get; init; }

    /// <summary>True when retrying this recipient could plausibly succeed.</summary>
    public bool IsRetryable => Outcome is SmtpTransactionOutcome.TransientRejection or SmtpTransactionOutcome.Failed
        or SmtpTransactionOutcome.InDoubt;

    internal static SmtpTransactionResult Accepted(SmtpReply reply) => new()
    {
        Outcome = SmtpTransactionOutcome.Accepted,
        Stage = SmtpDeliveryStage.FinalReply,
        Reply = reply,
    };

    internal static SmtpTransactionResult FromRejection(SmtpReply reply, SmtpDeliveryStage stage, string detail) => new()
    {
        Outcome = reply.IsTransientNegative
            ? SmtpTransactionOutcome.TransientRejection
            : SmtpTransactionOutcome.PermanentRejection,
        Stage = stage,
        Reply = reply,
        Detail = detail,
    };

    internal static SmtpTransactionResult Failed(SmtpDeliveryStage stage, string detail) => new()
    {
        Outcome = SmtpTransactionOutcome.Failed,
        Stage = stage,
        Detail = detail,
    };

    /// <summary>
    /// The transport refused to attempt this recipient, without consulting the upstream.
    /// </summary>
    /// <remarks>
    /// Reported as a permanent rejection because the reason is a property of the message itself,     /// an oversize body, or an envelope address that is not a legal SMTP path. Retrying would repeat
    /// it exactly, and the spec forbids telling the sender about it with a bespoke bounce, so it is
    /// recorded and left to the upstream's DSN policy.
    /// </remarks>
    internal static SmtpTransactionResult RefusedLocally(SmtpDeliveryStage stage, string detail) => new()
    {
        Outcome = SmtpTransactionOutcome.PermanentRejection,
        Stage = stage,
        Detail = detail,
    };

    internal static SmtpTransactionResult InDoubt(string detail) => new()
    {
        Outcome = SmtpTransactionOutcome.InDoubt,
        Stage = SmtpDeliveryStage.DataTerminator,
        Detail = detail,
    };
}
