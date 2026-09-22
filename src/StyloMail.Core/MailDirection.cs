namespace StyloMail.Core;

/// <summary>
/// Which way a message is travelling through StyloMail.
/// </summary>
/// <remarks>
/// Inbound and outbound statistics are kept distinct in profiles and are never
/// merged into one pool, an inbound stranger and an outbound authenticated
/// principal carry entirely different meaning for the same numeric value.
/// Relationship linkage between the two is permitted, but explicit.
/// </remarks>
public enum MailDirection
{
    /// <summary>Arriving from outside, destined for a configured recipient domain.</summary>
    Inbound = 0,

    /// <summary>Submission from an authenticated principal, destined for delivery.</summary>
    Outbound = 1,
}
