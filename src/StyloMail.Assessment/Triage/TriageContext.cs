namespace StyloMail.Assessment.Triage;

/// <summary>What triage is allowed to know, which is what the deployment configured.</summary>
/// <remarks>
/// <b>Deliberately narrow.</b> Triage decides what <em>not</em> to look at, so the less it is given
/// the less it can be wrong about. Every addition here is a decision about which messages can be
/// dismissed without being read.
/// </remarks>
public sealed record TriageContext
{
    /// <summary>
    /// The channels this deployment watches. Anything else is dismissed on scope.
    /// </summary>
    /// <remarks>
    /// <b>An empty set watches nothing, and that is a decision rather than a default.</b> Answering
    /// "watch everything" for an unconfigured deployment would make the host that has not been set up
    /// the most permissive one, which is the opposite of how every other unset value behaves here.
    /// The scope check is configuration rather than judgement, so its absence has to mean "nothing
    /// configured" rather than "no restriction".
    /// </remarks>
    public required IReadOnlySet<string> WatchedChannels { get; init; }

    /// <summary>
    /// The tenant a chat event is assessed under, since this surface carries no principal.
    /// </summary>
    public string TenantId { get; init; } = "inbound";

    /// <summary>
    /// The recent-campaign window, when this deployment has one.
    /// </summary>
    /// <remarks>
    /// Absent means the duplicate check does not run and says so in the outcome's
    /// <see cref="TriageOutcome.NotRun"/>, rather than reporting that it looked and found nothing.
    /// </remarks>
    public Campaign.CampaignNearDuplicateDetector? Campaign { get; init; }

    /// <summary>
    /// Where the author's and the conversation's behavioural evidence is read from.
    /// </summary>
    /// <remarks>
    /// Absent means the behavioural evidence is not attached, and the outcome says so through
    /// <see cref="TriageOutcome.NotRun"/> rather than reporting that it looked and found nothing.
    /// </remarks>
    public ChatObservationRecorder? Observations { get; init; }

    /// <summary>The clock the behavioural evaluator reads.</summary>
    /// <remarks>
    /// Defaults to the system clock, which triage can accept because it makes no decision the clock
    /// changes: it attaches evidence and escalates. The assessment's own clock is injected as it is
    /// everywhere else.
    /// </remarks>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public static TriageContext For(params string[] watchedChannels) => new()
    {
        WatchedChannels = new HashSet<string>(watchedChannels, StringComparer.Ordinal),
    };
}
