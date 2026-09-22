namespace StyloMail.Adaptive.Tests;

/// <summary>
/// A clock the test moves by hand. Every adaptive rule here is time-dependent, so a test that
/// used the wall clock would be measuring the machine's scheduling, not the model's behaviour.
/// </summary>
internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now;

    public TestClock(DateTimeOffset start) => _now = start;

    /// <summary>An arbitrary but fixed origin, so bucket arithmetic is legible in failures.</summary>
    public static TestClock AtEpoch() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);

    public void Set(DateTimeOffset at) => _now = at;
}
