namespace StyloMail.Core;

/// <summary>The channel a communication arrived on.</summary>
public enum ChannelKind
{
    /// <summary>SMTP. The only channel with an envelope, and the only one we can refuse before delivery.</summary>
    Email,

    /// <summary>Slack. A message event arrives after the platform has already delivered it.</summary>
    Slack,

    /// <summary>Discord. Same post-delivery constraint as Slack.</summary>
    Discord,
}
