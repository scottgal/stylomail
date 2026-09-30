namespace StyloMail.Nimble.Tests;

/// <summary>
/// A clock a test can move, so the circuit breaker's recovery can be tested without waiting for it.
/// </summary>
/// <remarks>
/// Only <see cref="GetUtcNow"/> is overridden. Everything else on <see cref="TimeProvider"/> is the
/// real implementation, which is what the adapter uses it for.
/// </remarks>
internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
