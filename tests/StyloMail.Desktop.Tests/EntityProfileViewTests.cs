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
        Assert.Contains("pending", view.PolicyBinding, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Trend not produced: prior_window_empty", view.Tiles.Single().Trend);
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
