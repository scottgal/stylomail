using StyloMail.Assessment.Semantic;
using StyloMail.Core;

namespace StyloMail.Assessment.Tests;

/// <summary>
/// The key is over the whole classifier input, and "the whole input" has to mean something precise
/// for the cache to be safe. These tests pin down where the line falls.
/// </summary>
public sealed class CacheKeyTests
{
    private static readonly SemanticCacheOptions Options = new() { ClassifierModelVersion = "jev-1.13.0" };

    private static SemanticMailInput Input(
        MailAnalysisInput message,
        IReadOnlyDictionary<string, string>? tagged = null) => new()
        {
            Message = message,
            Dimensions = SemanticDimensions.All,
            TaggedContext = tagged,
        };

    [Fact]
    public void TaggedContextIsPartOfTheKey()
    {
        var without = ClassifierInputCanonicalizer.Digest(Input(Builders.Message()));
        var with = ClassifierInputCanonicalizer.Digest(
            Input(Builders.Message(), new Dictionary<string, string> { ["relationship.established"] = "true" }));

        // "If relationship context went into the input, it is part of the key." A message answered
        // with context and one answered without it are two different questions.
        Assert.NotEqual(without, with);
    }

    [Fact]
    public void TaggedContextOrderDoesNotChangeTheKey()
    {
        var first = Input(
            Builders.Message(),
            new Dictionary<string, string> { ["a"] = "1", ["b"] = "2", ["c"] = "3" });

        var second = Input(
            Builders.Message(),
            new Dictionary<string, string> { ["c"] = "3", ["a"] = "1", ["b"] = "2" });

        // Dictionary enumeration order is an implementation detail. Letting it reach the key would
        // make the same facts produce two keys, which reads as a cache that works and a hit rate
        // that is quietly halved.
        Assert.Equal(
            ClassifierInputCanonicalizer.Digest(first),
            ClassifierInputCanonicalizer.Digest(second));
    }

    [Fact]
    public void DifferentTaggedContextValuesProduceDifferentKeys()
    {
        var first = Input(Builders.Message(), new Dictionary<string, string> { ["relationship.established"] = "true" });
        var second = Input(Builders.Message(), new Dictionary<string, string> { ["relationship.established"] = "false" });

        Assert.NotEqual(
            ClassifierInputCanonicalizer.Digest(first),
            ClassifierInputCanonicalizer.Digest(second));
    }

    [Fact]
    public void ConversationContextIsPartOfTheKey()
    {
        var without = Input(Builders.Message());
        var with = Builders.Message();
        with = with with { ConversationContext = ["earlier message in the thread"] };

        Assert.NotEqual(
            ClassifierInputCanonicalizer.Digest(without),
            ClassifierInputCanonicalizer.Digest(Input(with)));
    }

    [Fact]
    public void FieldBoundariesAreNotForgeable()
    {
        var split = Builders.Message("y");
        split = split with { Subject = "x" };

        var joined = Builders.Message(string.Empty);
        joined = joined with { Subject = "xy" };

        // Under delimiter-joined encoding these two could collide, and a message could then be
        // served another message's assessment by knowing where the separators fall. Length prefixes
        // make the field structure part of the input rather than an accident of its content.
        Assert.NotEqual(
            ClassifierInputCanonicalizer.Canonicalize(Input(split)),
            ClassifierInputCanonicalizer.Canonicalize(Input(joined)));
    }

    [Fact]
    public void AnEmptyFieldIsNotTheSameAsAnAbsentOne()
    {
        var absent = Builders.Message() with { Subject = null };
        var empty = Builders.Message() with { Subject = string.Empty };

        // "No subject" and "an empty subject" are different inputs the classifier can answer
        // differently, so a scheme that collapsed them would serve one's answer for the other.
        Assert.NotEqual(
            ClassifierInputCanonicalizer.Digest(Input(absent)),
            ClassifierInputCanonicalizer.Digest(Input(empty)));
    }

    [Fact]
    public void TheKeyIsStableAcrossEquivalentConstructions()
    {
        var first = Input(Builders.Message("body text"), new Dictionary<string, string> { ["k"] = "v" });
        var second = Input(Builders.Message("body text"), new Dictionary<string, string> { ["k"] = "v" });

        Assert.Equal(
            SemanticCacheKey.Digest(first, Options),
            SemanticCacheKey.Digest(second, Options));
    }

    [Fact]
    public void EachVersionStampChangesTheKey()
    {
        var input = Input(Builders.Message());
        var baseline = SemanticCacheKey.Digest(input, Options);

        Assert.NotEqual(baseline, SemanticCacheKey.Digest(input, Options with { ClassifierModelVersion = "jev-1.14.0" }));
        Assert.NotEqual(baseline, SemanticCacheKey.Digest(input, Options with { QuestionSchemaVersion = "semantic-dimensions/2" }));
        Assert.NotEqual(baseline, SemanticCacheKey.Digest(input, Options with { PreprocessingVersion = "assessment-preprocessing/2" }));
    }

    // ---------------------------------------------------------------------------------------------
    // Behavioural context is part of the key
    // ---------------------------------------------------------------------------------------------

    private static BehaviouralProfile Profile(
        int? messagesObserved = 500,
        bool profileAvailable = true,
        bool coldStart = false) => new()
        {
            Direction = MailDirection.Inbound,
            FirstSeenDaysAgo = 180,
            MessagesObserved = messagesObserved,
            TrustedSamples = 40,
            Regime = "regime-0",
            DistinctRecipientsLastHour = 2,
            DistinctRecipientsLast30Days = 30,
            RecipientsNovelToSender = 1,
            MessagesLastHour = 3,
            MessagesLast24Hours = 12,
            BaselineMessagesPerHour = 0.5,
            FanoutLastHour = 2,
            BaselineFanoutPerHour = 0.4,
            TrendNarrative = "steady",
            Movements =
            [
                new DimensionMovement { DimensionId = "semantic.urgency_pressure", Direction = "up", Magnitude = 0.2 },
            ],
            DimensionsWithSupport = 9,
            ProfileAvailable = profileAvailable,
            ColdStart = coldStart,
        };

    [Fact]
    public void TwoMessagesDifferingOnlyInSenderBehaviourDoNotShareAKey()
    {
        // The case the cache must never merge. Identical message, identical tenant, identical model:
        // one sender with months of history, one who is new and fanning out. If the key digested only
        // the message, the second would be served the first's assessment: a judgement formed when the
        // sender looked ordinary, reused after their behaviour changed.
        var message = Builders.Message();

        var established = Input(message) with { Profile = Profile(messagesObserved: 500) };
        var newAndFanningOut = Input(message) with { Profile = Profile(messagesObserved: 3) };

        Assert.NotEqual(
            SemanticCacheKey.Digest(established, Options),
            SemanticCacheKey.Digest(newAndFanningOut, Options));
    }

    [Fact]
    public void AnAbsentProfileIsNotTheSameKeyAsAnUnavailableOne()
    {
        var message = Builders.Message();

        // Null means no profile was available at all. ProfileAvailable: false is a positive statement
        // that we looked and found nothing. Different inputs, so different questions, so different keys.
        var absent = Input(message) with { Profile = null };
        var lookedAndFoundNothing = Input(message) with { Profile = Profile(profileAvailable: false) };

        Assert.NotEqual(
            SemanticCacheKey.Digest(absent, Options),
            SemanticCacheKey.Digest(lookedAndFoundNothing, Options));
    }

    [Fact]
    public void TheEncoderCoversEveryFieldCoreDeclares()
    {
        // A tripwire against the omission this whole section exists to prevent. `BehaviouralProfile`
        // is Core's and will grow; a field added there but not encoded here is part of the classifier
        // input and NOT part of the key, so two genuinely different questions silently share one
        // answer. Counting is cruder than varying each field, but it needs no reflection tricks and
        // it fails at the moment Core changes rather than at the moment somebody notices.
        //
        // When this fails, it is not noise: encode the new field in
        // ClassifierInputCanonicalizer.EncodeProfile, then update the number.
        const int encodedFieldCount = 19;
        const int movementFieldCount = 3;

        var declared = typeof(BehaviouralProfile).GetProperties().Length;
        var movement = typeof(DimensionMovement).GetProperties().Length;

        Assert.True(
            declared == encodedFieldCount,
            $"BehaviouralProfile declares {declared} properties but EncodeProfile encodes "
            + $"{encodedFieldCount}. A field that is part of the classifier input but not of the cache "
            + "key means two different questions share one cached assessment.");

        Assert.True(
            movement == movementFieldCount,
            $"DimensionMovement declares {movement} properties but EncodeProfile encodes "
            + $"{movementFieldCount}.");
    }

    // ---------------------------------------------------------------------------------------------
    // Security-bearing fingerprint
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheFingerprintFollowsTheDestinationNotThePresentation()
    {
        var plain = Builders.MessageWithLink("https://bank.example/pay").Links;
        var styled = Builders.MessageWithLink("https://bank.example/pay").Links;

        var restyled = new LinkObservation
        {
            DisplayedText = "Click here to verify your account",
            ActualTarget = "https://bank.example/pay",
        };

        var left = Builders.Message("body", links: plain);
        var right = Builders.Message("body", links: [restyled]);

        // Presenting the same destination differently changes what the classifier is asked, so the
        // key differs, but the message does the same thing, so the fingerprint agrees. That split
        // is what lets campaign comparison call these the same campaign without letting one's
        // assessment answer for the other.
        Assert.NotEqual(
            ClassifierInputCanonicalizer.Digest(Input(left)),
            ClassifierInputCanonicalizer.Digest(Input(right)));

        Assert.Equal(
            SecurityBearingFingerprint.Compute(left).Digest,
            SecurityBearingFingerprint.Compute(right).Digest);
    }

    [Fact]
    public void TheFingerprintChangesWhenTheDestinationDoes()
    {
        var original = Builders.MessageWithLink("https://bank.example/pay");
        var moved = Builders.MessageWithLink("https://bank.example/redirect?to=attacker");

        Assert.NotEqual(
            SecurityBearingFingerprint.Compute(original).Digest,
            SecurityBearingFingerprint.Compute(moved).Digest);
    }

    [Fact]
    public void TheFingerprintFollowsAttachmentBytesRatherThanAttachmentNames()
    {
        var renamed = new AttachmentMetadata
        {
            FileName = "invoice-renamed.pdf",
            DeclaredContentType = "application/pdf",
            SizeBytes = 1024,
            ContentUnavailable = false,
            ContentHash = "hash-abc",
        };

        var original = new AttachmentMetadata
        {
            FileName = "invoice.pdf",
            DeclaredContentType = "application/pdf",
            SizeBytes = 1024,
            ContentUnavailable = false,
            ContentHash = "hash-abc",
        };

        // A rename with unchanged bytes is the same payload, so the hash, not the name, decides.
        Assert.NotEqual(
            SecurityBearingFingerprint.Compute(Builders.Message(attachments: [original])).Digest,
            SecurityBearingFingerprint.Compute(Builders.Message(attachments: [renamed])).Digest);
    }

    [Fact]
    public void TheFingerprintChangesWhenTheSendingIdentityChanges()
    {
        var first = Builders.Message(envelope: Builders.Envelope(mailFrom: "sender@example.com"));
        var second = Builders.Message(envelope: Builders.Envelope(mailFrom: "sender@example.test"));

        Assert.NotEqual(
            SecurityBearingFingerprint.Compute(first).Digest,
            SecurityBearingFingerprint.Compute(second).Digest);
    }

    [Fact]
    public void TheFingerprintChangesWhenTheAccountNumberDoes()
    {
        var original = Builders.Message("Please pay invoice 4471 into account 12345678 immediately.");
        var changed = Builders.Message("Please pay invoice 4471 into account 87654321 immediately.");

        Assert.NotEqual(
            SecurityBearingFingerprint.Compute(original).Digest,
            SecurityBearingFingerprint.Compute(changed).Digest);
    }

    // ---------------------------------------------------------------------------------------------
    // Payment identifier extraction
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void PaymentIdentifiersAreFoundRegardlessOfGrouping()
    {
        var grouped = PaymentIdentifiers.Extract("Account 1234 5678 sort 40-11-22.");
        var ungrouped = PaymentIdentifiers.Extract("Account 12345678 sort 401122.");

        Assert.Contains("12345678", grouped);

        // Separators are stripped before comparison, or the same account typed two ways would read
        // as two different accounts.
        Assert.Equal(ungrouped, PaymentIdentifiers.Extract("Account 12345678 sort 401122."));
    }

    [Fact]
    public void PaymentIdentifiersIncludeIbans()
    {
        var found = PaymentIdentifiers.Extract("Please remit to GB33BUKB20201555555555 by Friday.");

        Assert.Contains("GB33BUKB20201555555555", found);
    }

    [Fact]
    public void PaymentIdentifierExtractionIsBounded()
    {
        var noisy = string.Join(' ', Enumerable.Range(0, 5_000).Select(i => $"ref 1000000{i:D2}"));

        var found = PaymentIdentifiers.Extract(noisy);

        // A message crafted to make this scan expensive must not be able to make it expensive, and
        // it must not be able to make the fingerprint arbitrarily long either.
        Assert.Equal(PaymentIdentifiers.MaxIdentifiers, found.Count);
    }

    [Fact]
    public void ShortNumbersAreNotTreatedAsAccountIdentifiers()
    {
        // "invoice 4471" is a reference, not a destination. Treating short numbers as payment
        // identifiers would make every routine invoice look like a changed destination.
        Assert.Empty(PaymentIdentifiers.Extract("Please pay invoice 4471 today."));
    }
}
