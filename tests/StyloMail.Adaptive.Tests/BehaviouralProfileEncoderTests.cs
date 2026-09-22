using System.Reflection;
using StyloMail.Adaptive.Profiles;
using StyloMail.Adaptive.Signals;
using StyloMail.Adaptive.Temporal;
using StyloMail.Core;

namespace StyloMail.Adaptive.Tests;

/// <summary>
/// The encoder that gives the classifier the context it was judging without.
/// </summary>
/// <remarks>
/// The classifier scores every message in isolation, so a credential request from an account with
/// six months of transactional receipts looks exactly like the same words from an account created
/// yesterday that is fanning out to strangers. The text is identical; the relationship is not.
///
/// <para>
/// Two properties are load-bearing and are asserted rather than described. The encoding carries
/// <b>observations and their support, never verdicts</b>: a profile arriving pre-judged would make
/// the classifier's answers a restatement of our own flags. And <b>a cold profile is a distinct
/// state</b>, not a quiet-looking one: "we do not know this sender" is not "this sender looks
/// ordinary".
/// </para>
/// </remarks>
public class BehaviouralProfileEncoderTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AColdProfileIsUnavailableRatherThanQuiet()
    {
        var profile = Profile();

        var encoded = BehaviouralProfileEncoder.Encode(profile, Start);

        Assert.False(encoded.ProfileAvailable);

        // The whole point. A profile we do not have must not encode as zero messages, zero
        // recipients and an ordinary-looking past, because that is indistinguishable from a
        // sender who genuinely has been quiet, and it is the same error as scoring an
        // unavailable signal as zero.
        Assert.Null(encoded.MessagesObserved);
        Assert.Null(encoded.TrustedSamples);
        Assert.Null(encoded.FirstSeenDaysAgo);
        Assert.Null(encoded.MessagesLastHour);
        Assert.Null(encoded.MessagesLast24Hours);
        Assert.Null(encoded.FanoutLastHour);
        Assert.Null(encoded.BaselineFanoutPerHour);
        Assert.Null(encoded.DimensionsWithSupport);
        Assert.Null(encoded.Movements);
        Assert.Null(encoded.TrendNarrative);
        Assert.Null(encoded.Regime);
    }

    [Fact]
    public void AnEstablishedSteadySenderReportsCountsAndBaselines()
    {
        var profile = Profile();
        for (var i = 0; i < 60; i++)
        {
            profile.Promote(Sample(at: Start.AddDays(i - 60), semantic: 0.05, fanoutPerSecond: 0.0005));
            profile.Observe(Observation(at: Start.AddDays(i - 60), recipients: 2, semantic: 0.05));
        }

        for (var i = 0; i < 5; i++)
        {
            profile.Observe(Observation(at: Start.AddMinutes(i), recipients: 2, semantic: 0.05));
        }

        var encoded = BehaviouralProfileEncoder.Encode(profile, Start.AddMinutes(5));

        Assert.True(encoded.ProfileAvailable);
        Assert.False(encoded.ColdStart);
        Assert.Equal(MailDirection.Outbound, encoded.Direction);
        Assert.Equal(65, encoded.MessagesObserved);
        Assert.Equal(60, encoded.TrustedSamples);

        // Observed and trusted are separate statements and never collapsed into one number:
        // a sent or unreported message is not approved history.
        Assert.NotEqual(encoded.MessagesObserved, encoded.TrustedSamples);

        Assert.Equal(60, encoded.FirstSeenDaysAgo);
        Assert.Equal(10, encoded.FanoutLastHour);

        // Support, not conclusion: three dimensions were promoted enough times to be comparable,
        // and the count says how much the comparison rests on rather than how it came out.
        Assert.Equal(3, encoded.DimensionsWithSupport);
        Assert.True(encoded.BaselineFanoutPerHour > 0);
    }

    [Fact]
    public void ASenderWhoseFanOutHasJustRisenSaysSoInWords()
    {
        var profile = Profile();
        for (var i = 0; i < 20; i++)
        {
            profile.Promote(Sample(at: Start.AddDays(i - 20), semantic: 0.05, fanoutPerSecond: 0.5));
        }

        // Then a burst: fan-out climbing across the burst window.
        for (var i = 0; i < 3; i++)
        {
            profile.Observe(Observation(
                at: Start.AddMinutes(i),
                recipients: 30 * (i + 1),
                semantic: 0.05 + (i * 0.15)));
        }

        var encoded = BehaviouralProfileEncoder.Encode(profile, Start.AddMinutes(3));

        Assert.True(encoded.ProfileAvailable);
        Assert.NotNull(encoded.TrendNarrative);

        // A narrative rather than a scalar: an unexplained acceleration number tells the
        // classifier nothing it can reason with.
        Assert.Contains("rising", encoded.TrendNarrative, StringComparison.Ordinal);
        Assert.Contains("fan-out", encoded.TrendNarrative, StringComparison.Ordinal);
        Assert.NotNull(encoded.Movements);
        Assert.Contains(encoded.Movements, m => m.DimensionId == FeatureIds.RecipientsPerSecond);
        Assert.All(encoded.Movements, m => Assert.Equal("rising", m.Direction));
    }

    [Fact]
    public void ARegimeChangeIsReportedAsAValueNotHidden()
    {
        var profile = Profile(initialRegimeId: "regime-a");
        profile.Promote(Sample(at: Start, semantic: 0.5, fanoutPerSecond: 0.5));

        profile.BeginRegime("regime-b");
        for (var i = 0; i < 12; i++)
        {
            profile.Promote(Sample(at: Start.AddMinutes(1), semantic: 0.9, fanoutPerSecond: 0.5));
        }

        Assert.True(profile.PromoteRegime());

        var encoded = BehaviouralProfileEncoder.Encode(profile, Start.AddMinutes(2));

        // The new regime is named. A change suppresses derivative evidence, and the classifier
        // needs to know which regime the numbers describe rather than being handed a summary
        // that silently spans two behaviours.
        Assert.Equal("regime-b", encoded.Regime);
    }

    [Fact]
    public void MovementsAreCappedSoTheStateBudgetCannotBeSpentByTrends()
    {
        var profile = Profile();
        for (var i = 0; i < 20; i++)
        {
            profile.Promote(Sample(at: Start.AddDays(-1), semantic: 0.05, fanoutPerSecond: 0.5));
        }

        for (var i = 0; i < 4; i++)
        {
            profile.Observe(Observation(
                at: Start.AddMinutes(i),
                recipients: 10 * (i + 1),
                semantic: 0.05 + (i * 0.2)));
        }

        var encoded = BehaviouralProfileEncoder.Encode(profile, Start.AddMinutes(4));

        Assert.NotNull(encoded.Movements);
        Assert.True(encoded.Movements.Count <= BehaviouralProfile.MaxMovements);
    }

    [Fact]
    public void DistinctRecipientsAndAddressedRecipientsAreDifferentMeasurements()
    {
        var sender = Profile();

        // Fifty messages, all to the same two people. High traffic, no fan-out at all.
        for (var i = 0; i < 50; i++)
        {
            sender.Observe(new ProfileObservation
            {
                ObservedAt = Start.AddMinutes(i),
                RecipientCount = 2,
                WasRejected = false,
                RecipientKeys = ["alice", "bob"],
                Dimensions = DimensionVector.Create(
                    DimensionSample.Available("semantic.credential_request", 0.05)),
            });
        }

        var encoded = BehaviouralProfileEncoder.Encode(sender, Start.AddMinutes(50));

        // A hundred addresses written to two people is not fan-out. Counting addresses rather than
        // people would call this fifty times more alarming than it is.
        Assert.Equal(100, encoded.FanoutLastHour);
        Assert.Equal(2, encoded.DistinctRecipientsLastHour);
        Assert.NotEqual(encoded.FanoutLastHour, encoded.DistinctRecipientsLastHour);
    }

    [Fact]
    public void ASenderFanningOutToStrangersCountsDistinctRecipients()
    {
        var sender = Profile();

        for (var i = 0; i < 40; i++)
        {
            sender.Observe(new ProfileObservation
            {
                ObservedAt = Start.AddMinutes(i),
                RecipientCount = 1,
                WasRejected = false,
                RecipientKeys = [$"stranger-{i}"],
                Dimensions = DimensionVector.Create(
                    DimensionSample.Available("semantic.credential_request", 0.05)),
            });
        }

        var encoded = BehaviouralProfileEncoder.Encode(sender, Start.AddMinutes(40));

        Assert.Equal(40, encoded.DistinctRecipientsLastHour);
        Assert.Equal(40, encoded.DistinctRecipientsLast30Days);
        Assert.False(encoded.RecipientDistinctnessIsFloor);
    }

    [Fact]
    public void NoveltyIsUnknownWhenTheMessageRecipientsWereNotSupplied()
    {
        var sender = Profile();
        sender.Observe(new ProfileObservation
        {
            ObservedAt = Start,
            RecipientCount = 1,
            WasRejected = false,
            RecipientKeys = ["alice"],
            Dimensions = DimensionVector.Create(
                DimensionSample.Available("semantic.credential_request", 0.05)),
        });

        var encoded = BehaviouralProfileEncoder.Encode(sender, Start);

        // We were not told who this message went to, so the question cannot be answered. Zero
        // would be an answer, and a reassuring one.
        Assert.Null(encoded.RecipientsNovelToSender);
    }

    [Fact]
    public void NoveltySurvivesTheDistinctSetSaturating()
    {
        var sender = new AdaptiveProfile(
            new ProfileKey
            {
                TenantId = "tenant-a",
                Scope = ProfileScopeKind.OutboundSender,
                Key = "sender",
                Direction = MailDirection.Outbound,
            },
            new AdaptiveOptions { RecipientHistoryCapacity = 4 });

        sender.Observe(new ProfileObservation
        {
            ObservedAt = Start,
            RecipientCount = 5,
            WasRejected = false,
            RecipientKeys = ["a", "b", "c", "d", "e"],
            Dimensions = DimensionVector.Create(
                DimensionSample.Available("semantic.credential_request", 0.05)),
        });

        var encoded = BehaviouralProfileEncoder.Encode(sender, Start, messageRecipients: ["a", "z"]);

        // The capped set ran out of room, so distinct counting is a floor, but the membership
        // filter never forgets, and "have we ever seen 'a'?" is still answerable. Taking novelty
        // dark here would blind the profile on exactly the widest-reaching senders.
        Assert.True(sender.Recipients.Truncated);
        Assert.Equal(1, encoded.RecipientsNovelToSender);
    }

    [Fact]
    public void NoveltyIsUnknownWhenTheHistoryDoesNotCoverThePrincipalsPast()
    {
        var sender = Profile();
        sender.Observe(new ProfileObservation
        {
            ObservedAt = Start,
            RecipientCount = 1,
            WasRejected = false,
            RecipientKeys = ["alice"],
            Dimensions = DimensionVector.Create(
                DimensionSample.Available("semantic.credential_request", 0.05)),
        });

        // Exactly what the store does when it loads a profile whose membership filter was not
        // stored. The filter is empty, so every recipient would look new, and novelty is the most
        // alarming signal this profile carries, so an empty filter must not be allowed to answer.
        sender.Recipients.MarkIncomplete();

        var encoded = BehaviouralProfileEncoder.Encode(sender, Start, messageRecipients: ["stranger"]);

        Assert.Null(encoded.RecipientsNovelToSender);
    }

    [Fact]
    public void NoveltyIsAnsweredWhileTheHistoryIsStillComplete()
    {
        var sender = Profile();
        sender.Observe(new ProfileObservation
        {
            ObservedAt = Start,
            RecipientCount = 1,
            WasRejected = false,
            RecipientKeys = ["alice"],
            Dimensions = DimensionVector.Create(
                DimensionSample.Available("semantic.credential_request", 0.05)),
        });

        var encoded = BehaviouralProfileEncoder.Encode(sender, Start, messageRecipients: ["alice", "stranger"]);

        Assert.Equal(1, encoded.RecipientsNovelToSender);
    }

    [Fact]
    public void ADistinctCountUnderReportsRatherThanOverReportsOnceTheHistoryIsTruncated()
    {
        var sender = new AdaptiveProfile(
            new ProfileKey
            {
                TenantId = "tenant-a",
                Scope = ProfileScopeKind.OutboundSender,
                Key = "sender",
                Direction = MailDirection.Outbound,
            },
            new AdaptiveOptions { RecipientHistoryCapacity = 4 });

        sender.Observe(new ProfileObservation
        {
            ObservedAt = Start,
            RecipientCount = 6,
            WasRejected = false,
            RecipientKeys = ["a", "b", "c", "d", "e", "f"],
            Dimensions = DimensionVector.Create(
                DimensionSample.Available("semantic.credential_request", 0.05)),
        });

        var encoded = BehaviouralProfileEncoder.Encode(sender, Start);

        // Six distinct recipients against a set that holds four. The emitted figure is a floor:
        // it under-states, which for a count is the direction that cannot manufacture alarm: the
        // opposite of novelty, where over-stating is the danger.
        //
        // The count is a floor and the record now says so, which is the difference between a
        // small honest lie and an explicit "at least".
        Assert.True(sender.Recipients.Truncated);
        Assert.Equal(4, encoded.DistinctRecipientsLast30Days);
        Assert.True(encoded.RecipientDistinctnessIsFloor);
    }

    [Fact]
    public void TheEncodingCarriesNoVerdictShapedField()
    {
        // The cheapest possible guard on the constraint that matters most: a profile arriving
        // pre-judged would make the classifier's answers a restatement of our own flags, and the
        // independence that makes semantic evidence worth having would be gone. Adding one is now
        // a failing build rather than a design debate.
        var offending = VerdictShapedFields(typeof(BehaviouralProfile), typeof(DimensionMovement));

        Assert.True(
            offending.Length == 0,
            $"the behavioural profile carries verdict-shaped fields ({string.Join(", ", offending)}). "
            + "It must carry observations and their support only: deterministic policy authorises "
            + "actions, and a pre-judged profile makes the classifier's answers a restatement of ours.");
    }

    [Fact]
    public void TheVerdictDetectorFiresOnTypesThatAreGenuinelyVerdictShaped()
    {
        // A guard that has never been seen firing is not a guard. These are existing Core types
        // that legitimately carry judgements, so pointing the same detector at them proves it
        // would catch a judgement added to the profile rather than merely passing on anything.
        Assert.NotEmpty(VerdictShapedFields(typeof(RiskDimension)));
        Assert.NotEmpty(VerdictShapedFields(typeof(MailAssessment)));
        Assert.NotEmpty(VerdictShapedFields(typeof(RecipientDisposition)));
    }

    private static string[] VerdictShapedFields(params Type[] types)
    {
        string[] forbidden =
        [
            "score", "risk", "severity", "suspicious", "verdict", "action",
            "malicious", "threat", "anomaly", "anomalous", "safe", "judgement", "judgment",
        ];

        return
        [
            .. types
                .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                .Select(property => property.Name)
                .Where(name => forbidden.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))),
        ];
    }

    [Fact]
    public void AProfileWeDoNotHaveEncodesAsUnavailableRatherThanEmpty()
    {
        // A profile row can exist with nothing behind it: the centroid store creates one for any
        // principal it records a vector for, including one nobody has ever assessed. That is still
        // "we do not know this sender", not a sender with an unremarkable history.
        var encoded = BehaviouralProfileEncoder.Encode(Profile(), Start);

        Assert.False(encoded.ProfileAvailable);
        Assert.Null(encoded.TrustedSamples);
        Assert.Null(encoded.MessagesObserved);
    }

    [Fact]
    public void ApprovedHistoryAloneCountsAsKnowingTheSender()
    {
        var profile = Profile();
        profile.Promote(Sample(at: Start.AddDays(-10), semantic: 0.5, fanoutPerSecond: 0.5));

        var encoded = BehaviouralProfileEncoder.Encode(profile, Start);

        // No observed traffic, but a trusted baseline: the one thing we do know is worth more
        // than reporting the sender as unknown.
        Assert.True(encoded.ProfileAvailable);
        Assert.Equal(1, encoded.TrustedSamples);
        Assert.Equal(0, encoded.MessagesObserved);
    }

    private static AdaptiveProfile Profile(string initialRegimeId = "regime-0") => new(
        new ProfileKey
        {
            TenantId = "tenant-a",
            Scope = ProfileScopeKind.OutboundSender,
            Key = "sender",
            Direction = MailDirection.Outbound,
        },
        options: null,
        initialRegimeId);

    private static ProfileObservation Observation(
        DateTimeOffset at,
        int recipients,
        double semantic) => new()
    {
        ObservedAt = at,
        RecipientCount = recipients,
        WasRejected = false,
        Dimensions = DimensionVector.Create(
            DimensionSample.Available("semantic.credential_request", semantic),
            DimensionSample.Available(
                FeatureIds.RecipientsPerSecond,
                recipients / 60.0)),
    };

    private static TrustedSample Sample(
        DateTimeOffset at,
        double semantic,
        double fanoutPerSecond) => new()
    {
        RecordedAt = at,
        Provenance = LabelProvenance.AuthenticatedOperator,
        Dimensions = DimensionVector.Create(
            DimensionSample.Available("semantic.credential_request", semantic),
            DimensionSample.Available(FeatureIds.RecipientsPerSecond, fanoutPerSecond),
            DimensionSample.Available(FeatureIds.MessagesPerSecond, fanoutPerSecond / 2)),
    };
}
