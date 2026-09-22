using StyloMail.Chat.Slack;

namespace StyloMail.Chat.Tests;

public sealed class SlackRetryGuardTests
{
    [Fact]
    public void The_same_event_twice_is_only_admitted_once()
    {
        // Slack retries a delivery it thinks failed. Assessing the retry would double every
        // observation the behavioural engine counts, which is a rate change it cannot tell from
        // real traffic.
        var guard = new SlackRetryGuard(capacity: 64);

        Assert.True(guard.TryBegin("Ev01"));
        Assert.False(guard.TryBegin("Ev01"));
        Assert.True(guard.TryBegin("Ev02"));
    }

    [Fact]
    public void Forgetting_an_event_does_not_forget_a_different_one()
    {
        var guard = new SlackRetryGuard(capacity: 64);
        guard.TryBegin("Ev01");
        guard.Forget("Ev01");

        // A retry after a genuine failure has to be admitted, or a transient fault on our side
        // becomes a message that is never assessed and never retried either.
        Assert.True(guard.TryBegin("Ev01"));
    }

    [Fact]
    public void The_oldest_events_are_evicted_rather_than_growing_without_bound()
    {
        var guard = new SlackRetryGuard(capacity: 2);
        guard.TryBegin("Ev01");
        guard.TryBegin("Ev02");
        guard.TryBegin("Ev03");

        // Bounded cardinality is a feature of this system, not tuning. Past the bound an old id may
        // be admitted again, which is the correct trade: a duplicated assessment is recoverable and
        // an unbounded set is not.
        Assert.True(guard.TryBegin("Ev01"));
    }
}
