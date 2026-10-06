using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// The decision body the harness falls back to has to bind.
/// </summary>
/// <remarks>
/// <c>ux-scripts/decision-fixture.json</c> is what a smoke script gets in the
/// detail pane when it has no real decision to open, and the shipped app reads
/// it: <c>MainWindow.LoadHarnessDecisionAsync</c> deserializes it with the
/// client's own options. Nothing in this suite parsed it, and that is how it
/// rotted. The mirror grew two required members (<c>channel</c> and
/// <c>deliveryTiming</c>) and the fixture did not, so every run that fell back
/// to it logged that it could not load it and drew an empty pane, which a
/// reader of the screenshot cannot tell from a pane defect. The fixtures in
/// <see cref="Wire"/> were updated with the contract, which is exactly why the
/// suites stayed green while the shipped file did not.
///
/// <para>
/// So this reads the <b>shipped file</b>, from the repository and not from the
/// test binary's output, and binds it the way the app binds a Host body:
/// <c>StyloMailApiClient</c> deserializes with one
/// <see cref="System.Text.Json.JsonSerializerOptions"/> instance for every
/// response, the same instance the fallback path passes explicitly. A member
/// added to the mirror as <c>required</c> now fails here rather than only in
/// front of an operator.
/// </para>
/// </remarks>
public sealed class DecisionFixtureTests
{
    /// <summary>
    /// Reads the file the harness actually points the app at. The path is
    /// written here rather than assembled per test because it is a fact about
    /// <c>console-harness.sh</c>: it exports
    /// <c>STYLOMAIL_SMOKE_DECISION_FILE</c> as this file, so a test that read
    /// some other copy would prove nothing about a run.
    /// </summary>
    private static async Task<DecisionResponse> ReadShippedFixture()
    {
        var path = Path.Combine(
            FindRepositoryRoot(), "ux-scripts", "decision-fixture.json");

        Assert.True(File.Exists(path), $"The harness's decision fixture is not at {path}.");

        var client = new StyloMailApiClient(
            StubHttpMessageHandler.ReturningJson(await File.ReadAllTextAsync(path)).CreateClient(),
            new TestApiKeyProvider());

        return await client.GetDecisionAsync("asm_0f4d2a");
    }

    /// <summary>
    /// The file binds at all, and it still says what the pane renders: an
    /// operator looking at a fallback decision sees these values, so they are
    /// the fixture's meaning rather than incidental content.
    /// </summary>
    [Fact]
    public async Task The_fixture_the_harness_ships_binds_as_a_decision()
    {
        var decision = await ReadShippedFixture();

        Assert.Equal("asm_0f4d2a", decision.AssessmentId);
        Assert.Equal(MailAction.Quarantine, decision.Action);
        Assert.Equal(0.82, decision.RiskIndex);
        Assert.Equal(3, decision.Reasons.Count);
        Assert.Equal("credential_request_high", decision.Reasons[0].Code);
    }

    /// <summary>
    /// The two members whose absence broke it, named so that a fixture carrying
    /// them with the wrong values fails as loudly as one that dropped them.
    /// </summary>
    /// <remarks>
    /// Both are required on the mirror, and the reason they are required is the
    /// reason this fixture has to say something: a decision that does not say
    /// where the message came from, or whether anything could have been done,
    /// is missing the two facts the pane's caveats are built on.
    /// </remarks>
    [Fact]
    public async Task The_fixture_says_which_channel_and_which_timing()
    {
        var decision = await ReadShippedFixture();

        Assert.Equal(ChannelKind.Email, decision.Channel.Kind);
        Assert.Equal(DeliveryTiming.PreAcceptance, decision.DeliveryTiming);
    }

    [Fact]
    public async Task The_shipped_fixture_has_two_distinct_windowed_trend_rows()
    {
        var view = DecisionView.From(await ReadShippedFixture(), isFixture: true);

        Assert.True(view.IsFixture);
        Assert.Equal(
            ["OutboundSender · burst", "OutboundSender · slow"],
            view.TrendObservations.Select(row => row.ScopeLabel));
        Assert.All(view.TrendObservations, row => Assert.Equal("Available", row.AvailabilityLabel));
    }

    /// <summary>
    /// The fixture's masked row says it did not count, and names the weight
    /// that stayed out of the divisor.
    /// </summary>
    /// <remarks>
    /// The fixture carries a masked row precisely so the no-feed smoke can
    /// assert this sentence on the shipped file. If the fixture stops saying it,
    /// that smoke fails in front of an operator with a selector that matched
    /// nothing, which reads as a rendering defect and is not one. Asserted here
    /// so the fixture's own claim is checked where it can be read.
    /// </remarks>
    [Fact]
    public async Task The_fixture_says_which_row_did_not_count_and_what_it_weighed()
    {
        var decision = await ReadShippedFixture();
        var view = DecisionView.From(decision);

        var masked = view.Dimensions.Single(dimension => dimension.Name == "reputation");

        Assert.False(masked.Counted);
        Assert.Equal(0.25, masked.Weight);
        Assert.Equal(
            "Not counted towards the index: Masked by the trusted-history rule. "
                + "Its weight of 0.25 is not in the divisor.",
            masked.ContributionLabel);
    }

    /// <summary>
    /// Walks up from the test binary until it finds the solution file.
    /// </summary>
    /// <remarks>
    /// A relative path from the binary would be a guess about configuration
    /// depth, and the guess breaks the first time somebody builds to a
    /// different output path. The same walk is in <c>JevCorpus</c> and
    /// <c>NimbleCorpus</c>, which read committed files for the same reason.
    /// </remarks>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StyloMail.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root by walking up from '{AppContext.BaseDirectory}'.");
    }
}
