using StyloMail.Adaptive.Profiles;
using StyloMail.Core;

namespace StyloMail.Assessment.Tests;

/// <summary>
/// There are two profile operations and the wrong one for each workload is a real failure, so each
/// test here is really asserting which store call was made — not merely that something happened.
/// </summary>
public sealed class ProfileCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static ProfileKey Key(string id = "sender-1") =>
        ProfileScopes.OutboundSender("tenant-1", id);

    private static ProfileObservation Observation(int recipients = 1) => new()
    {
        ObservedAt = Now,
        RecipientCount = recipients,
        WasRejected = false,
    };

    [Fact]
    public void AnObservationUsesTheDeltaPathAndNotTheWholeProfileOne()
    {
        var store = new FakeProfileStore();
        var coordinator = new ProfileCoordinator(store);

        coordinator.Observe(Key(), Observation(), Now);

        // The append path, and the assertion is about which operation was chosen rather than about
        // safety: both store operations take the write lock before reading, so a burst would survive
        // either. What ApplyObservation buys is that "this is an append" is stated in one place
        // instead of every caller reimplementing the merge inside an update delegate.
        Assert.Equal(1, store.ApplyObservationCount);
        Assert.Equal(0, store.UpdateCount);
        Assert.Equal(1, coordinator.Statistics.Observed);
        Assert.Equal(0, coordinator.Statistics.Mutated);
    }

    [Fact]
    public void AWholeProfileChangeUsesTheTransactionalUpdate()
    {
        var store = new FakeProfileStore();
        var coordinator = new ProfileCoordinator(store);

        var result = coordinator.Mutate(
            Key(),
            Now,
            profile =>
            {
                profile.Promote(Trusted());
                return profile.Baseline.Version;
            });

        Assert.Equal(1, store.UpdateCount);
        Assert.Equal(0, store.ApplyObservationCount);
        Assert.Equal(1, result);
        Assert.Equal(1, coordinator.Statistics.Mutated);
    }

    [Fact]
    public void AnObservationOnAnUnseenProfileCreatesIt()
    {
        var store = new FakeProfileStore();
        var coordinator = new ProfileCoordinator(store);

        coordinator.Observe(Key("never-seen"), Observation(recipients: 3), Now);

        // The ingest path has no "profile must already exist" precondition for a caller to get
        // wrong: the first message from a principal is an ordinary message.
        var profile = store.Peek(Key("never-seen"));
        Assert.NotNull(profile);
        Assert.Equal(1, profile!.Observed.Attempts);
        Assert.Equal(3, profile.Observed.Recipients);
    }

    [Fact]
    public void ObservationsAccumulatePerProfileRatherThanIntoOne()
    {
        var store = new FakeProfileStore();
        var coordinator = new ProfileCoordinator(store);

        coordinator.Observe(Key("a"), Observation(recipients: 1), Now);
        coordinator.Observe(Key("a"), Observation(recipients: 2), Now);
        coordinator.Observe(Key("b"), Observation(recipients: 4), Now);

        // A delta applied to the wrong row would show up here as a count that is too high rather
        // than as a failure anywhere.
        Assert.Equal(3, store.Peek(Key("a"))!.Observed.Recipients);
        Assert.Equal(4, store.Peek(Key("b"))!.Observed.Recipients);
    }

    [Fact]
    public void AMutationCarriesTheObservationsItLoaded()
    {
        var store = new FakeProfileStore();
        var coordinator = new ProfileCoordinator(store);
        var key = Key();

        coordinator.Observe(key, Observation(recipients: 2), Now);

        coordinator.Mutate(key, Now, profile =>
        {
            profile.Promote(Trusted());
            return true;
        });

        // A whole-profile write is exactly what can lose a delta written beside it, which is why the
        // two paths are kept apart rather than sharing one mechanism.
        var profile = store.Peek(key)!;
        Assert.Equal(1, profile.Observed.Attempts);
        Assert.Equal(2, profile.Observed.Recipients);
        Assert.Equal(1, profile.Baseline.Version);
    }

    [Fact]
    public void ReadingAnUnseenProfileYieldsAnEmptyOneRatherThanNothing()
    {
        var coordinator = new ProfileCoordinator(new FakeProfileStore());

        var profile = coordinator.Read(Key("never-seen"));

        // "What do we know about this sender" has an honest answer for a sender nobody has seen, and
        // it is not "I could not answer".
        Assert.Equal(0, profile.Observed.Attempts);
        Assert.Equal(0, profile.Baseline.Version);
    }

    private static TrustedSample Trusted() => new()
    {
        Dimensions = DimensionVector.Create(
            DimensionSample.Available(SemanticDimensions.ConversationalContinuityId, 0.5)),
        Provenance = LabelProvenance.AuthenticatedOperator,
        RecordedAt = Now,
    };
}
