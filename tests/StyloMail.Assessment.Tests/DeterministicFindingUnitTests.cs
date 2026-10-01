using StyloMail.Policy;

namespace StyloMail.Assessment.Tests;

/// <summary>
/// The normalisation policy applies to a count is the one a reader of a served row would apply.
/// </summary>
/// <remarks>
/// <para>
/// A ledger exists so that a decision can be reproduced later by somebody who has the row and not the
/// code. That reader has the score, the weight and the declared unit, and the arithmetic they will
/// naturally apply to a count is a clamp, because a count is a magnitude and a magnitude belongs in
/// 0..1. Policy does something else: <see cref="DeterministicFindings.Normalise"/> is a bounded step,
/// <c>1.0</c> at one or more and <c>0.0</c> at zero. The two agree, and they agree for a reason worth
/// stating rather than assuming: a count is a non-negative integer, and on that domain a clamp and a
/// step are the same function.
/// </para>
/// <para>
/// <b>Nothing in the tree enforces the domain.</b> The producer happens to publish integers, and a
/// finding whose value ever became fractional would make these two readers disagree about the same
/// row with no test anywhere saying so. The half of that risk which is reachable is pinned below; the
/// half which is not is pinned as a divergence rather than left implied, because a test asserting only
/// the agreement would read as a claim that the functions are equal.
/// </para>
/// </remarks>
public sealed class DeterministicFindingUnitTests
{
    /// <summary>Hoisted because an inline constant array fails the build here (`CA1861`).</summary>
    private static readonly double[] BoundedValues = [0.0, 0.25, 0.5, 1.0];

    /// <summary>
    /// The values a count can actually take, including the ones either side of the step's knee.
    /// </summary>
    /// <remarks>
    /// Two and five are here rather than only zero and one because the step flattens them: a future
    /// change to a ramp would leave the zero and one cases green, and the whole point of the step is
    /// that five homographs weigh exactly what one does.
    /// </remarks>
    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(5.0)]
    [InlineData(1000.0)]
    public void The_count_step_agrees_with_a_clamp_across_the_domain_a_count_occupies(double count)
    {
        Assert.Equal(
            Math.Clamp(count, 0.0, 1.0),
            DeterministicFindings.Normalise(count, SignalUnit.Count));
    }

    [Fact]
    public void The_count_step_and_a_clamp_diverge_strictly_between_zero_and_one()
    {
        // The boundary, stated rather than left to be discovered. It is not reachable through any
        // fixture: no message produces half a link, half an attachment or half a failed authentication,
        // so no served row can carry this value and only the function can be asked about it directly.
        // That is exactly why it is asserted here rather than through a route.
        Assert.Equal(0.5, Math.Clamp(0.5, 0.0, 1.0));
        Assert.Equal(0.0, DeterministicFindings.Normalise(0.5, SignalUnit.Count));

        // And the same holds at the other end of the open interval, so the divergence is the interval
        // rather than one sampled point.
        Assert.Equal(0.999, Math.Clamp(0.999, 0.0, 1.0));
        Assert.Equal(0.0, DeterministicFindings.Normalise(0.999, SignalUnit.Count));
    }

    [Fact]
    public void The_ratio_and_boolean_units_are_already_the_clamp()
    {
        // The other two units declare values that need no interpretation, so for them the reader's
        // model and the engine's are the same call. Asserted so that a later change which made Ratio
        // or Boolean do something else would have to fail here rather than quietly re-scale a
        // magnitude that the producer already bounded.
        foreach (var value in BoundedValues)
        {
            Assert.Equal(
                Math.Clamp(value, 0.0, 1.0),
                DeterministicFindings.Normalise(value, SignalUnit.Ratio));
            Assert.Equal(
                Math.Clamp(value, 0.0, 1.0),
                DeterministicFindings.Normalise(value, SignalUnit.Boolean));
        }
    }
}
