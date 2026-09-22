using Microsoft.Extensions.DependencyInjection;
using StyloMail.Host.Chat;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Tests;

/// <summary>
/// The durable hand-off between answering the platform and assessing the event.
/// </summary>
/// <remarks>
/// The answer given to the platform is this path's equivalent of the mail path's <c>250</c>. These
/// pin the two properties that make it true: the event is on disk before the answer, and an event
/// already dealt with is never dealt with twice.
/// </remarks>
public sealed class ChatIntakeStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000);

    private static ChatIntakeEntry Entry(string id, int secondsLater = 0) =>
        new(id, $$"""{"event_id":"{{id}}"}""", Now.AddSeconds(secondsLater));

    [Fact]
    public void An_admitted_event_survives_the_process_that_admitted_it()
    {
        // The property the whole design rests on: we answer the platform, so what we answered for has
        // to still exist if we die before assessing it.
        using var host = new TestHost();
        var store = host.Services.GetRequiredService<IChatIntakeStore>();

        Assert.Equal(ChatIntakeAdmission.Admitted, store.Admit(Entry("Ev01"), capacity: 8));

        // A second host over the same storage is the closest the suite gets to a restart.
        using var reopened = new TestHost().ReusingStorageOf(host);
        var afterRestart = reopened.Services.GetRequiredService<IChatIntakeStore>();

        var waiting = afterRestart.Waiting(8);

        var entry = Assert.Single(waiting);
        Assert.Equal("Ev01", entry.EventId);
        Assert.Equal(Now, entry.ReceivedAt);
    }

    [Fact]
    public void An_event_already_waiting_is_not_admitted_a_second_time()
    {
        // A platform retry of something still queued. Assessing it twice would double every
        // observation the behavioural engine counts, which is a rate change it cannot tell from real
        // traffic.
        using var host = new TestHost();
        var store = host.Services.GetRequiredService<IChatIntakeStore>();

        store.Admit(Entry("Ev01"), capacity: 8);

        Assert.Equal(ChatIntakeAdmission.AlreadyKnown, store.Admit(Entry("Ev01"), capacity: 8));
        Assert.Single(store.Waiting(8));
    }

    [Fact]
    public void An_event_already_assessed_is_not_admitted_again()
    {
        // The retry that matters most, because it arrives after the answer and inside the platform's
        // retry window. This is why the row is marked rather than deleted.
        using var host = new TestHost();
        var store = host.Services.GetRequiredService<IChatIntakeStore>();

        store.Admit(Entry("Ev01"), capacity: 8);
        store.Complete("Ev01", Now.AddSeconds(1));

        Assert.Equal(ChatIntakeAdmission.AlreadyKnown, store.Admit(Entry("Ev01"), capacity: 8));
        Assert.Empty(store.Waiting(8));
    }

    [Fact]
    public void A_full_intake_refuses_rather_than_storing_past_its_bound()
    {
        // Refusing is what makes the platform retry, which is honest backpressure. Storing past the
        // bound would be a queue that grows.
        using var host = new TestHost();
        var store = host.Services.GetRequiredService<IChatIntakeStore>();

        Assert.Equal(ChatIntakeAdmission.Admitted, store.Admit(Entry("Ev01"), capacity: 1));
        Assert.Equal(ChatIntakeAdmission.Full, store.Admit(Entry("Ev02"), capacity: 1));

        // And the refused event was not left behind, because a row for it would make the platform's
        // retry look like a duplicate and the event would sit unassessed with nobody coming back.
        var waiting = store.Waiting(8);
        Assert.Equal("Ev01", Assert.Single(waiting).EventId);
    }

    [Fact]
    public void The_oldest_waiting_event_comes_back_first()
    {
        // A burst must not starve what arrived before it.
        using var host = new TestHost();
        var store = host.Services.GetRequiredService<IChatIntakeStore>();

        store.Admit(Entry("Ev02", 5), capacity: 8);
        store.Admit(Entry("Ev01", 1), capacity: 8);

        var waiting = store.Waiting(8);

        Assert.Equal(["Ev01", "Ev02"], waiting.Select(e => e.EventId));
    }

    [Fact]
    public void Pruning_removes_answered_events_and_leaves_waiting_ones()
    {
        // Bounded retention rather than growth. A waiting event is never pruned: it has not been
        // assessed, so removing it would be losing traffic we already answered for.
        using var host = new TestHost();
        var store = host.Services.GetRequiredService<IChatIntakeStore>();

        store.Admit(Entry("Ev01"), capacity: 8);
        store.Admit(Entry("Ev02"), capacity: 8);
        store.Complete("Ev01", Now.AddMinutes(-30));

        var removed = store.Prune(Now.AddMinutes(-10));

        Assert.Equal(1, removed);
        Assert.Equal("Ev02", Assert.Single(store.Waiting(8)).EventId);
    }
}
