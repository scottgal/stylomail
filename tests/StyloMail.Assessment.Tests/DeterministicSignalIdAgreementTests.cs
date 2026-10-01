using System.Reflection;
using StyloMail.Mime;
using StyloMail.Policy;

namespace StyloMail.Assessment.Tests;

/// <summary>
/// The weighted deterministic ids agree between the producer and the policy that weights them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DeterministicFindings"/> holds its ids as literals rather than referencing
/// <see cref="MimeSignals"/>, because Policy sits below Mime and must not depend on it. That split is
/// right, and its failure mode is silent: rename a constant in <c>MimeSignals</c> and the producer
/// starts publishing an id no weight is attached to, so a decisive MIME fact quietly stops moving the
/// index while every test still passes. Nothing in either assembly can catch that on its own, which is
/// why the guard lives here, the one project that references both.
/// </para>
/// <para>
/// The mutation that would prove these assertions bite is a rename inside
/// <see cref="DeterministicFindings"/>, which is not this project's file to edit, so the detector is
/// exercised directly instead: <see cref="The_detector_reports_an_id_the_producer_does_not_publish"/>
/// hands it a fabricated id and requires it back. That is the half a passing guard is otherwise free
/// to be vacuous about.
/// </para>
/// <para>
/// This is the id half only. What a finding's value means is carried by <see cref="SignalUnit"/>,
/// declared beside the weight and asserted below to cover every weighted deterministic id, so a weight
/// cannot exist without a declared unit either.
/// </para>
/// </remarks>
public sealed class DeterministicSignalIdAgreementTests
{
    /// <summary>Every string constant the MIME analyser publishes as a signal id.</summary>
    private static readonly HashSet<string> Published = typeof(MimeSignals)
        .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The unit each weighted deterministic id declares, written against the producer's own constant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed on <see cref="MimeSignals"/> rather than on <see cref="DeterministicFindings"/>'s copy of
    /// the same literal, which is the whole point: what this pins is the unit policy attaches to the
    /// id the producer <em>publishes</em>. A rename on either side stops compiling here, and a unit
    /// that quietly changes shape stops passing, which matters because the unit is the only thing
    /// telling a count apart from a ratio when the number arrives.
    /// </para>
    /// <para>
    /// Contributed by <c>policy-</c>, who could not home it: these ids are literals in both assemblies
    /// because Policy sits below Mime, and this project is the one that sees both.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, SignalUnit> DeclaredUnits = new(StringComparer.Ordinal)
    {
        [MimeSignals.TrustedAuthenticationFailure] = SignalUnit.Count,
        [MimeSignals.LinkDisplayMismatch] = SignalUnit.Ratio,
        [MimeSignals.LinkIdnHomograph] = SignalUnit.Count,
        [MimeSignals.DisplayNameAddressMismatch] = SignalUnit.Boolean,
        [MimeSignals.AttachmentTypeMismatch] = SignalUnit.Count,
        [MimeSignals.HtmlTextDisagreement] = SignalUnit.Ratio,
        [MimeSignals.PaddingObfuscation] = SignalUnit.Count,
        [MimeSignals.ReplyToDivergence] = SignalUnit.Boolean,
        [MimeSignals.EnvelopeHeaderIdentity] = SignalUnit.Count,
        [MimeSignals.ThreadHeaderConsistency] = SignalUnit.Count,
    };

    /// <summary>The ids in <paramref name="ids"/> that the MIME analyser never publishes.</summary>
    private static List<string> Orphans(IEnumerable<string> ids) =>
        ids.Where(id => !Published.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void The_detector_reports_an_id_the_producer_does_not_publish()
    {
        const string fabricated = "deterministic.this_id_was_never_published";

        // The other two tests pass when both sides agree, and they would also pass if the comparison
        // were broken in the direction of agreeing with everything. This one fails in that case.
        Assert.Contains(fabricated, Orphans([MimeSignals.PaddingObfuscation, fabricated]));

        Assert.Empty(Orphans([MimeSignals.PaddingObfuscation, MimeSignals.LinkDisplayMismatch]));
    }

    [Fact]
    public void Every_id_policy_declares_deterministic_is_an_id_mime_publishes()
    {
        // Both sides non-empty, or the comparison below is vacuously true: an empty Units map has no
        // orphans, and an empty Published set is a reflection bug rather than an agreement.
        Assert.NotEmpty(DeterministicFindings.Units);
        Assert.NotEmpty(Published);

        var orphans = Orphans(DeterministicFindings.Units.Keys);

        // Naming the orphans rather than asserting a count: the failure a reader has to act on is
        // which id stopped matching, not how many did.
        Assert.True(
            orphans.Count == 0,
            "DeterministicFindings declares ids the MIME analyser does not publish, so no finding can "
            + "ever match them and their weight is dead: " + string.Join(", ", orphans));
    }

    [Fact]
    public void Every_weighted_deterministic_id_declares_its_unit()
    {
        var weights = new PolicyOptions().DimensionWeights;
        Assert.NotEmpty(weights);

        var weighted = weights.Keys
            .Where(id => !id.StartsWith("semantic.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        // Both directions, because they fail differently: an id weighted without a unit cannot be
        // normalised, and an id declared with a unit but never weighted is a finding that was meant to
        // count and does not.
        var undeclared = weighted.Except(DeterministicFindings.Units.Keys, StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal).ToList();
        var unweighted = DeterministicFindings.Units.Keys.Except(weighted, StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal).ToList();

        Assert.True(
            undeclared.Count == 0,
            "Weighted in DimensionWeights with no declared unit in DeterministicFindings, so the "
            + "value has no shape to normalise: " + string.Join(", ", undeclared));

        Assert.True(
            unweighted.Count == 0,
            "Declared as deterministic with a unit but absent from DimensionWeights, so the finding "
            + "is never counted: " + string.Join(", ", unweighted));
    }

    [Fact]
    public void Every_weighted_deterministic_id_declares_the_unit_the_producer_publishes()
    {
        Assert.NotEmpty(DeclaredUnits);

        foreach (var (id, unit) in DeclaredUnits)
        {
            // The one assertion the test above cannot make. That one requires a unit to exist; this
            // one requires it to be the right shape, because a count read as a ratio is not a smaller
            // number, it is a different claim about the message.
            Assert.True(
                DeterministicFindings.Units.TryGetValue(id, out var actual),
                $"{id} is published by the MIME analyser and weighted by policy with no unit declared "
                + "for it, so nothing can normalise its value.");

            Assert.Equal(unit, actual);
        }

        // Named rather than counted, for the reason the id checks above are: an eleventh id, weighted
        // and given a unit of its own, would leave the loop agreeing about ten things while saying
        // nothing about the one that was added.
        var undeclared = DeterministicFindings.Units.Keys
            .Except(DeclaredUnits.Keys, StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            undeclared.Count == 0,
            "Weighted and given a unit, but not named here, so its unit is unpinned: "
            + string.Join(", ", undeclared));
    }
}
