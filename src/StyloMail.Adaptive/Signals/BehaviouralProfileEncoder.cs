using StyloMail.Adaptive.Profiles;
using StyloMail.Adaptive.Scoring;
using StyloMail.Adaptive.Temporal;
using StyloMail.Core;

namespace StyloMail.Adaptive.Signals;

/// <summary>
/// Encodes a profile's observed state into the bounded context the semantic classifier judges a
/// message against.
/// </summary>
/// <remarks>
/// The classifier scores each message in isolation, so a credential request from an account with
/// six months of transactional receipts reads exactly like the same words from an account created
/// yesterday and fanning out to strangers. The text is identical; the relationship is not.
///
/// <para>
/// <b>Observations and their support, never verdicts.</b> The encoder emits counts, rates, windows
/// and baselines. There is no severity, no score, no flag: because a profile that arrived
/// pre-judged would make the classifier's answers a restatement of our own flags, and the
/// independence that makes semantic evidence worth having would be gone.
/// </para>
///
/// <para>
/// <b>A field we cannot fill is left null, never zero.</b> Absence is a distinct state throughout
/// this engine, and it stays one here: an unavailable signal encoded as zero would tell the
/// classifier that a sender we have never seen has been quiet, which is a different and much more
/// reassuring claim.
/// </para>
/// </remarks>
public static class BehaviouralProfileEncoder
{
    /// <summary>
    /// Encodes <paramref name="profile"/> as at <paramref name="at"/>.
    /// </summary>
    /// <param name="profile">The observed state. Never the trusted baseline alone.</param>
    /// <param name="at">The instant the assessment is being made. Stated, not read from a clock.</param>
    /// <param name="options">Window configuration. Defaults are fine unless a host overrides them.</param>
    public static BehaviouralProfile Encode(
        AdaptiveProfile profile,
        DateTimeOffset at,
        IReadOnlyList<string>? messageRecipients = null,
        AdaptiveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var adaptive = options ?? new AdaptiveOptions();
        var direction = profile.Key.Direction ?? MailDirection.Outbound;

        // "We know nothing about this principal" is the unavailable state, and it needs *both*
        // sides to be empty. A profile row can exist with nothing behind it, because the centroid
        // store creates one for any principal it records a vector for, and an empty row is not
        // knowledge of a sender.
        //
        // Approved history counts as knowing them, though: a profile with trusted samples and no
        // observations is unusual, but reporting it as "we do not know this sender" would discard
        // the one thing we do know.
        if (profile.Observed.Attempts == 0 && profile.Baseline.TrustedSupport == 0)
        {
            return Unavailable(direction);
        }

        var scaleModel = profile.Baseline.ScaleModel;
        var support = scaleModel?.Dimensions.Count ?? 0;

        var trend = TrendFor(profile, adaptive.Burst, scaleModel, at);

        return new BehaviouralProfile
        {
            Direction = direction,
            ProfileAvailable = true,

            // Cold start means we know the sender but cannot compare them to anything yet. It is
            // deliberately not set when the profile is unavailable: those are two different
            // statements and the reader must check availability first.
            ColdStart = support == 0,

            FirstSeenDaysAgo = DaysSince(profile.Observed.FirstObservedAt, at),
            MessagesObserved = Count(profile.Observed.Attempts),
            TrustedSamples = profile.Baseline.TrustedSupport,
            Regime = profile.CurrentRegimeId,

            MessagesLastHour = Count(MessagesIn(profile, adaptive.Slow, at, TimeSpan.FromHours(1))),
            MessagesLast24Hours = Count(MessagesIn(profile, adaptive.Slow, at, TimeSpan.FromHours(24))),
            BaselineMessagesPerHour = BaselinePerHour(scaleModel, FeatureIds.MessagesPerSecond),

            // Recipients, not distinct recipients: this engine tracks how many recipients a
            // principal addressed, and a distinct-recipient count is a different measurement it
            // does not take. The field names below say what they hold rather than what would be
            // convenient.
            FanoutLastHour = Count(RecipientsIn(profile, adaptive.Slow, at, TimeSpan.FromHours(1))),
            BaselineFanoutPerHour = BaselinePerHour(scaleModel, FeatureIds.RecipientsPerSecond),

            // Distinct people, not addresses. FanoutLastHour counts how many times the sender
            // wrote to somebody; these count how many separate people they wrote to, and one
            // message to fifty colleagues is a large number in the first and one in the second.
            //
            // FLOORS, not measurements, once the recipient history is truncated: the true count
            // can only be larger. That is the safe direction for a count: it under-reports and so
            // cannot manufacture alarm, and the flag below says so rather than leaving the caller
            // to guess. Novelty takes the opposite treatment for the opposite reason: over-
            // reporting THAT would manufacture the alarm.
            DistinctRecipientsLastHour = profile.Recipients.DistinctSince(at - TimeSpan.FromHours(1)),
            DistinctRecipientsLast30Days = profile.Recipients.DistinctSince(at - profile.Recipients.Window),
            RecipientDistinctnessIsFloor = profile.Recipients.Truncated,

            // Null when the message's recipients were not identified to us, and null when the
            // history does not cover the principal's past. Those are different unknowns and
            // neither is zero. Note it is *not* null merely because the distinct set saturated:
            // the membership filter carries on answering that case, which is the whole reason for
            // keeping two structures.
            RecipientsNovelToSender = messageRecipients is null
                ? null
                : profile.Recipients.NovelCount(messageRecipients),

            DimensionsWithSupport = support,
            TrendNarrative = trend?.Narrative,
            Movements = Movements(trend),
        };
    }

    private static BehaviouralProfile Unavailable(MailDirection direction) => new()
    {
        Direction = direction,
        ProfileAvailable = false,
        ColdStart = false,
    };

    private static TrendResult? TrendFor(
        AdaptiveProfile profile,
        TrendWindow window,
        RobustScaleModel? scaleModel,
        DateTimeOffset at)
    {
        if (scaleModel is null || !profile.Series.TryGetValue(window.Name, out var series))
        {
            return null;
        }

        return TrendAnalyzer.Analyze(new TrendRequest
        {
            Series = series,
            Window = window,
            ScaleModel = scaleModel,
            Now = at,
            DimensionSchemaVersion = "adaptive-dimensions/1",
            PriorDimensionSchemaVersion = "adaptive-dimensions/1",
            RegimeId = profile.CurrentRegimeId,
            PriorRegimeId = profile.CurrentRegimeId,
        });
    }

    private static IReadOnlyList<DimensionMovement>? Movements(TrendResult? trend)
    {
        if (trend is null || trend.Movements.Count == 0)
        {
            // No movement is a real finding and an empty list says so; a null would say we could
            // not look. Both are honest here because the trend analysis reported either way.
            return trend is null ? null : [];
        }

        return
        [
            .. trend.Movements
                .Take(BehaviouralProfile.MaxMovements)
                .Select(movement => new DimensionMovement
                {
                    DimensionId = movement.DimensionId,
                    Direction = "rising",
                    Magnitude = movement.VelocityPerSecond,
                }),
        ];
    }

    private static int? MessagesIn(AdaptiveProfile profile, TrendWindow window, DateTimeOffset at, TimeSpan span)
    {
        if (!profile.Series.TryGetValue(window.Name, out var series))
        {
            return null;
        }

        var from = at - span;

        return series.Buckets
            .Where(bucket => bucket.Start >= from && bucket.Start <= at)
            .Sum(bucket => bucket.SampleCount);
    }

    private static int? RecipientsIn(AdaptiveProfile profile, TrendWindow window, DateTimeOffset at, TimeSpan span)
    {
        if (!profile.Series.TryGetValue(window.Name, out var series))
        {
            return null;
        }

        var from = at - span;

        return series.Buckets
            .Where(bucket => bucket.Start >= from && bucket.Start <= at)
            .Sum(bucket => bucket.RecipientCount);
    }

    /// <summary>
    /// The trusted baseline's expectation for a rate feature, per hour.
    /// </summary>
    /// <remarks>
    /// Null unless the baseline actually models the feature. Rate features are synthesised per
    /// bucket rather than observed per message, so a promotion path that only approves semantic
    /// dimensions leaves this unmodelled, and an unmodelled baseline is not a baseline of zero.
    /// </remarks>
    private static double? BaselinePerHour(RobustScaleModel? scaleModel, string featureId)
    {
        if (scaleModel is null || !scaleModel.Dimensions.TryGetValue(featureId, out var scale))
        {
            return null;
        }

        return scale.Mean * 3600.0;
    }

    private static int? DaysSince(DateTimeOffset? from, DateTimeOffset at) =>
        from is null ? null : (int)(at - from.Value).TotalDays;

    private static int? Count(long value) => (int)Math.Min(value, int.MaxValue);

    private static int? Count(int? value) => value;
}
