using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.Tests;

public sealed class EntityProfileViewTests
{
    [Fact]
    public void Missing_display_label_uses_only_a_short_opaque_id_and_trend_absence_is_named()
    {
        var view = new EntityProfileView(Profile());
        Assert.Equal("Entity entity-ident…", view.Title);
        Assert.Contains("unavailable", view.PolicyBinding, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Trend not produced: prior_window_empty", view.Tiles.Single().Trend);
    }

    [Fact]
    public void Reduced_coverage_keeps_the_value_and_names_its_limit()
    {
        var profile = Profile() with
        {
            Kpis = [new KpiValueResponse
            {
                Definition = new() { Id = "pressure", Label = "Pressure", Question = "Was pressure language observed?", MinimumSample = 5, Unit = KpiUnit.Ratio, Version = "1.1.0" },
                State = KpiState.ReducedCoverage, Value = .6, Reason = "reply_candidates_incomplete",
                Coverage = new() { Analysed = 5, Total = 6, Truncated = true, Reason = "reply_candidates_incomplete" },
                Trend = new() { State = KpiTrendState.NotProduced, PriorTruncated = false, Reason = "prior_window_reduced_coverage" }
            }]
        };

        var tile = new EntityProfileView(profile).Tiles.Single();
        Assert.Contains("Observed with reduced coverage: 60%", tile.Observation);
        Assert.Contains("reply_candidates_incomplete", tile.Observation);
    }

    [Fact]
    public void Ratio_trend_delta_is_rendered_as_percentage_points_not_a_relative_percent()
    {
        var profile = Profile() with { Kpis = [new KpiValueResponse
        {
            Definition = new() { Id = "responsiveness.address_reciprocity", Label = "Responded from the same address within 7 days", Question = "Q", MinimumSample = 5, Unit = KpiUnit.Ratio, Version = "1.1.0" },
            State = KpiState.Produced, Value = .8, Coverage = new() { Analysed = 5, Total = 5, Truncated = false },
            Trend = new() { State = KpiTrendState.Produced, Movement = KpiTrendMovement.Up, Delta = .6, PriorTruncated = false }
        }] };

        Assert.Equal("Trend: up (+60 percentage points).", new EntityProfileView(profile).Tiles.Single().Trend);
    }

    private static ProfileResponse Profile() => new()
    {
        TenantId = "tenant", Scope = new() { Kind = "entity", Id = "entity-identifier-long" },
        DisplayLabelAvailable = false, Coverage = new() { Messages = 3, TenantMessages = 8, Truncated = false, Unparsable = 0, WindowStart = DateTimeOffset.UtcNow.AddDays(-90), WindowEnd = DateTimeOffset.UtcNow },
        Relationship = new(), Kpis = [new()
        {
            Definition = new() { Id = "pressure", Label = "Pressure", Question = "Was pressure language observed?", MinimumSample = 5, Unit = KpiUnit.Ratio, Version = "1" },
            State = KpiState.NotEnoughHistory, Coverage = new() { Analysed = 3, Total = 3, Truncated = false }, Denominator = 3,
            Trend = new() { State = KpiTrendState.NotEnoughHistory, PriorTruncated = false, Reason = "prior_window_empty" }
        }]
    };
}
