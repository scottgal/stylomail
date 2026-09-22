namespace StyloMail.Core;

/// <summary>
/// The platform slugs that form part of a persisted chat profile key.
/// </summary>
/// <remarks>
/// <b>These are key components, not display names, and they must not follow a rename of anything
/// else.</b> A profile key containing "slack" addresses history that was written under that value,
/// so changing it orphans every observation behind it rather than renaming them. Written as a
/// constant rather than derived from <see cref="ChannelKind"/> for exactly that reason: an enum
/// member can be renamed by someone reading a compiler error, and this cannot.
/// </remarks>
public static class ChatPlatforms
{
    public const string Slack = "slack";
}
