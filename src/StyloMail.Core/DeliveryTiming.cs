namespace StyloMail.Core;

/// <summary>
/// Whether an assessment was made before the recipient could see the message.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is recorded rather than derived.</b> It would be possible to infer it from
/// <see cref="ChannelKind"/> today and wrong to, for the same reason an envelope is not inferred
/// from a header: a channel that changes its delivery model, or a future channel that offers both,
/// would silently keep the old answer.
/// </para>
/// <para>
/// <b>It is required, so no call site can omit it.</b> A defaulted value would let an assessment
/// claim <see cref="PreAcceptance"/> by not thinking about it, and a console showing a post-hoc
/// hold as though the system could have stopped the message is the specific false statement this
/// property exists to prevent.
/// </para>
/// </remarks>
public enum DeliveryTiming
{
    /// <summary>We were in the delivery path and could decline responsibility before delivery.</summary>
    PreAcceptance,

    /// <summary>The platform had already delivered it. Every action available is post-hoc.</summary>
    PostDelivery,
}
