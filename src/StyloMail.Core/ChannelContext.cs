namespace StyloMail.Core;

/// <summary>
/// Where a message came from, and what the channel tells us about who could see it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kept separate from message content</b>, the same way tagged context is, so a reader can tell
/// what the platform asserted from what the message said. On Slack the platform authenticates the
/// member and not the message, which means a compromised account is authenticated exactly like a
/// legitimate one. Membership is therefore context and never a verdict.
/// </para>
/// <para>
/// <b>Every field beyond <see cref="Kind"/> is nullable and null means the channel does not have
/// that concept.</b> Email has no workspace, so <see cref="ChannelContext.Email"/> leaves it null
/// rather than inventing one. Absence is a distinct state here as it is everywhere else in this
/// engine.
/// </para>
/// </remarks>
public sealed record ChannelContext
{
    public required ChannelKind Kind { get; init; }

    /// <summary>The tenant's workspace on the platform, where the channel has one.</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>The channel or conversation the message was posted in.</summary>
    public string? ChannelId { get; init; }

    /// <summary>The thread it belongs to, where the platform has threads.</summary>
    public string? ThreadId { get; init; }

    /// <summary>The email channel, which has none of the fields above.</summary>
    public static ChannelContext Email { get; } = new() { Kind = ChannelKind.Email };
}
