using StyloMail.Adaptive.Profiles;
using StyloMail.Core;

namespace StyloMail.Assessment;

/// <summary>
/// Records that an author posted, separately from assessing what they posted.
/// </summary>
/// <remarks>
/// <para>
/// <b>The write and the assessment have to be separable, and this is the seam.</b> Triage runs before
/// the assessment and stops some messages before it, so a write that only happened inside the
/// assessor would mean a member who posts fifty near-duplicates contributes nothing to their own
/// profile, and the behaviour check then computes velocity and drift from a history that the checks in
/// front of it have been quietly thinning. That is the behaviour check starved by the checks it is
/// behind.
/// </para>
/// <para>
/// <b>It counts attempts, not outcomes</b>, which is the rule the mail path already lives by: observed
/// state is what the behavioural engine reads, so it has to hold every message that was seen rather
/// than the ones that turned out interesting. A baseline made only of what we found suspicious is not
/// a baseline.
/// </para>
/// <para>
/// <b>Which checks record is a decision, not an accident.</b> A message dismissed on scope is not
/// recorded, because an out-of-scope channel is one this deployment decided not to look at and putting
/// ignored traffic into the baseline we judge by would be the decision being reversed quietly.
/// Everything past scope is recorded, because a dismissal there is about cost rather than about the
/// message being uninteresting.
/// </para>
/// </remarks>
public sealed class ChatObservationRecorder
{
    private readonly ProfileCoordinator _profiles;
    private readonly MailAssessorOptions _options;

    public ChatObservationRecorder(IAdaptiveProfileStore profileStore, MailAssessorOptions options)
    {
        ArgumentNullException.ThrowIfNull(profileStore);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _profiles = new ProfileCoordinator(profileStore);
        _options = options;
    }

    /// <summary>
    /// The profile keys one message belongs to: its author, and the conversation it went to.
    /// </summary>
    /// <remarks>
    /// Built here rather than in the assessor so the write and the read cannot disagree about which
    /// profile a message belongs to. A record written to one key and read from another is a
    /// behavioural history that silently never grows.
    /// </remarks>
    public IReadOnlyList<ProfileKey> KeysFor(ChatAnalysisInput input, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(input);

        var author = AuthorScope(input, tenantId);
        var target = TargetScope(input, tenantId, author);

        return target is null ? [author] : [author, target];
    }

    /// <summary>
    /// Records that this message was seen, whatever happens to it afterwards.
    /// </summary>
    public void Record(
        ChatAnalysisInput input,
        string tenantId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);

        var recipientKeys = TargetPseudonym(input, tenantId) is { } target
            ? new[] { target }
            : null;

        var observation = new ProfileObservation
        {
            ObservedAt = now,

            // One target per message: a chat message goes to the one conversation it was posted in,
            // rather than to a list of addresses the way a mail message does.
            RecipientCount = 1,

            // Nothing is declined on this path. Chat has no delivery responsibility and takes no
            // action, so an attempt is never a refused one. Hard-coded rather than read from an
            // action, which is always Allow here and would look like a measurement when it is a
            // constant.
            WasRejected = false,

            // Null because no semantic evidence was obtained, and a vector of zeros would claim the
            // dimensions were measured and came back calm.
            Dimensions = null,

            // Absent rather than empty when the conversation kind is unknown, which leaves novelty
            // unanswerable for that message rather than making it zero.
            RecipientKeys = recipientKeys,
        };

        foreach (var key in KeysFor(input, tenantId))
        {
            _profiles.Observe(key, observation, now);
        }
    }

    /// <summary>
    /// The author's profile, in the pool the derived direction selects.
    /// </summary>
    /// <remarks>
    /// The direction is what says whether this author is an authenticated principal of this tenant or
    /// a stranger to it, and the two pools are never merged. A member read as a stranger would lose
    /// exactly the job this evidence exists for.
    /// </remarks>
    private ProfileKey AuthorScope(ChatAnalysisInput input, string tenantId)
    {
        var pseudonym = _options.ProfileKeyHasher.Hash(tenantId, input.Membership.AuthorId);

        return input.Membership.Direction == MailDirection.Inbound
            ? ProfileScopes.ChatAuthor(
                tenantId,
                ChatPlatforms.Slack,

                // Always present in practice, because the connector states it from the event. Stated
                // rather than defaulted anyway, so a hand-built input that omits it produces a key
                // naming the absence instead of silently joining another workspace's pool.
                input.Channel.WorkspaceId ?? "workspace-unknown",
                pseudonym)
            : ProfileScopes.OutboundSender(tenantId, pseudonym);
    }

    /// <summary>
    /// The conversation the message went to, with its kind in the key.
    /// </summary>
    /// <remarks>
    /// "Talking to people it never talks to" and "posting in channels it never posts in" are different
    /// claims, and a member's direct messages and their channel posts accumulate separately so the
    /// fan-out evidence cannot report them as suddenly talking to new people when they have merely
    /// posted somewhere new. An unknown kind yields no target at all, which is the honest answer
    /// rather than filing it as either.
    /// </remarks>
    private ProfileKey? TargetScope(ChatAnalysisInput input, string tenantId, ProfileKey author) =>
        TargetPseudonym(input, tenantId) is { } target
            ? ProfileScopes.Relationship(tenantId, input.Membership.Direction, author.Key, target)
            : null;

    private string? TargetPseudonym(ChatAnalysisInput input, string tenantId) =>
        input.Conversation == ChatConversationKind.Unknown
            || input.Channel.ChannelId is not { Length: > 0 } channelId
                ? null
                : _options.ProfileKeyHasher.Hash(tenantId, $"{input.Conversation}|{channelId}");

    /// <summary>Reads a profile, for the behaviour check and for the assessment.</summary>
    public AdaptiveProfile Read(ProfileKey key) => _profiles.Read(key);
}
