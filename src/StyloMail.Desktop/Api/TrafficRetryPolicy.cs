using Microsoft.AspNetCore.SignalR.Client;

namespace StyloMail.Desktop.Api;

/// <summary>
/// How long this console waits between attempts when a feed will not open.
/// </summary>
/// <remarks>
/// <para>
/// <b>One table with two callers, and that is the whole point.</b> SignalR
/// applies this cadence by itself to a connection that had been live and
/// dropped, and the console applies the same one to a connect an operator asked
/// for. Two copies of the numbers would be two answers to "how long until the
/// console gives up", and the person watching the status bar would have no way
/// to tell which one they were watching.
/// </para>
/// <para>
/// The delays are SignalR's own defaults: an immediate retry, then 2, 10 and 30
/// seconds, so four retries and about 42 seconds before the console stops
/// trying. They are written down here rather than left to the library so the
/// budget is something this code states rather than something it inherits, and
/// so the operator's sequence can ask for the same next delay the automatic path
/// would use.
/// </para>
/// <para>
/// <b>These four numbers must stay exactly SignalR's own defaults.</b> The
/// automatic reconnect is not following this object into new territory; it is
/// being handed the schedule it already had, and the only reason to state it is
/// so that the operator's sequence and the automatic one are provably the same
/// cadence rather than two that happen to agree today. So a change here is not a
/// tuning decision, it is a divergence: it would leave the two paths waiting
/// different amounts, and nothing on the status line would say which one the
/// operator was watching. There is no acceptable divergence. If SignalR's
/// defaults ever move, they move here in the same change, and the automatic path
/// is re-measured against the operator's, not assumed.
/// </para>
/// <para>
/// Deliberately an instance type rather than a static table: a test can build
/// one with millisecond delays and assert the whole sequence's behaviour without
/// spending the real 42 seconds. Production uses <see cref="Shared"/>.
/// </para>
/// </remarks>
public sealed class TrafficRetryPolicy : IRetryPolicy
{
    /// <summary>
    /// The four waits, in the order they are taken.
    /// </summary>
    /// <remarks>
    /// SignalR's own default reconnect sequence, and it has to stay that way:
    /// zero, then two, then ten, then thirty seconds. See the class remarks for
    /// why no divergence is acceptable. The assertion that this is what the
    /// object produces, and that the automatic path is handed the same numbers,
    /// is in tests/StyloMail.Desktop.Tests/TrafficFeedTests.cs, which is where
    /// the constraint is checked rather than merely stated.
    /// </remarks>
    private static readonly TimeSpan[] DefaultDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    private readonly TimeSpan[] _delays;

    /// <summary>
    /// A policy with exactly these waits before each successive retry.
    /// </summary>
    /// <remarks>
    /// An array rather than four parameters so the limit and the budget are
    /// derived from one fact rather than repeated as three that can disagree.
    /// </remarks>
    public TrafficRetryPolicy(params TimeSpan[] delays)
    {
        ArgumentNullException.ThrowIfNull(delays);

        if (delays.Length == 0)
        {
            throw new ArgumentException("A retry policy with no delays would never retry.", nameof(delays));
        }

        // Copied rather than held: the caller's array is the caller's, and a
        // policy that could be changed from outside is not one cadence.
        _delays = (TimeSpan[])delays.Clone();
    }

    /// <summary>
    /// The cadence both paths use: the automatic reconnect and the operator's.
    /// </summary>
    public static TrafficRetryPolicy Shared { get; } = new(DefaultDelays);

    /// <summary>How many retries follow a first attempt that failed.</summary>
    public int RetryLimit => _delays.Length;

    /// <summary>
    /// How long a whole sequence can take, from the first failure to giving up.
    /// </summary>
    /// <remarks>
    /// The sum of the waits, not a second constant: a budget that could drift
    /// from the cadence would be a number the console tells the operator that is
    /// not the number the console keeps to.
    /// </remarks>
    public TimeSpan Budget => TimeSpan.FromTicks(_delays.Sum(delay => delay.Ticks));

    /// <summary>
    /// The wait before retry <paramref name="retry"/>, counting from one, or
    /// null once the retries are spent.
    /// </summary>
    public TimeSpan? DelayBeforeRetry(int retry)
        => retry >= 1 && retry <= _delays.Length ? _delays[retry - 1] : null;

    /// <summary>The wait after this many consecutive failures, or null to stop.</summary>
    /// <remarks>
    /// The <see cref="IRetryPolicy"/> shape, so SignalR's automatic reconnect can
    /// be handed this same policy rather than a copy of its numbers.
    /// </remarks>
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
        => DelayBeforeRetry((int)retryContext.PreviousRetryCount + 1);
}
