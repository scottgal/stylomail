namespace StyloMail.Nimble.Tests;

/// <summary>
/// The breaker is a copy of the hosted adapter's, so these tests are too. That is the point: a copy
/// that behaves differently from the original would make the two providers fail in different ways,
/// and the comparison between them is the reason this lane exists.
/// </summary>
public sealed class NimbleCircuitBreakerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Stays_closed_below_the_threshold()
    {
        var time = new MutableTimeProvider(Start);
        var breaker = new NimbleCircuitBreaker(3, TimeSpan.FromSeconds(30), time);

        breaker.RecordFailure();
        breaker.RecordFailure();

        Assert.False(breaker.IsOpen);
    }

    [Fact]
    public void Opens_at_the_threshold()
    {
        var time = new MutableTimeProvider(Start);
        var breaker = new NimbleCircuitBreaker(3, TimeSpan.FromSeconds(30), time);

        for (var i = 0; i < 3; i++)
        {
            breaker.RecordFailure();
        }

        Assert.True(breaker.IsOpen);
    }

    [Fact]
    public void Success_clears_accumulated_failures()
    {
        var time = new MutableTimeProvider(Start);
        var breaker = new NimbleCircuitBreaker(3, TimeSpan.FromSeconds(30), time);

        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordSuccess();
        breaker.RecordFailure();
        breaker.RecordFailure();

        // Two, not four: the failures either side of a success do not add up.
        Assert.False(breaker.IsOpen);
    }

    [Fact]
    public void Allows_a_probe_once_the_open_duration_has_elapsed()
    {
        var time = new MutableTimeProvider(Start);
        var breaker = new NimbleCircuitBreaker(1, TimeSpan.FromSeconds(30), time);

        breaker.RecordFailure();
        Assert.True(breaker.IsOpen);

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.True(breaker.IsOpen);

        // Recovery is discovered rather than waited out. The probe is allowed through, and only a
        // further failure re-opens it.
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(breaker.IsOpen);
    }

    [Fact]
    public void Refuses_a_threshold_that_could_never_trip()
    {
        var time = new MutableTimeProvider(Start);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NimbleCircuitBreaker(0, TimeSpan.FromSeconds(30), time));
    }
}
