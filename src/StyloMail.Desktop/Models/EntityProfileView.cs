using System.Globalization;
using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Models;

/// <summary>Presentation-only projection of a Host profile; it makes no judgement about a person.</summary>
public sealed class EntityProfileView
{
    private readonly string _policyBinding;
    public EntityProfileView(ProfileResponse profile, PolicyEffectiveBindingResponse? binding = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Title = profile.DisplayLabelAvailable && !string.IsNullOrWhiteSpace(profile.DisplayLabel)
            ? profile.DisplayLabel : $"Entity {Short(profile.Scope.Id)}";
        LabelDetail = profile.DisplayLabelAvailable
            ? $"Label source: {profile.DisplayLabelSource}."
            : "No stored display label is available.";
        RelationshipAge = profile.Relationship.FirstSeenAt is { } first
            ? $"Relationship observed since {first.LocalDateTime:d}."
            : "Relationship age was not produced.";
        LastContact = profile.Relationship.LastSeenAt is { } last
            ? $"Last contact: {last.LocalDateTime:g}."
            : "Last contact was not produced.";
        Coverage = $"Coverage: {profile.Coverage.Messages} attributable of {profile.Coverage.TenantMessages} tenant messages." +
            (profile.Coverage.Truncated ? " Limited to the most recent 2,000 messages." : string.Empty) +
            (string.IsNullOrWhiteSpace(profile.Coverage.Reason) ? string.Empty : $" {profile.Coverage.Reason}");
        Tiles = profile.Kpis.Select(k => new KpiTileView(k)).ToArray();
        NotMeasured = profile.NotMeasured.Select(x => $"{x.Label}: {x.Reason}").ToArray();
        _policyBinding = DescribeBinding(binding);
    }

    public string Title { get; }
    public string LabelDetail { get; }
    public string RelationshipAge { get; }
    public string LastContact { get; }
    public string Coverage { get; }
    public string PolicyBinding => _policyBinding;
    public IReadOnlyList<KpiTileView> Tiles { get; }
    public IReadOnlyList<string> NotMeasured { get; }

    private static string Short(string value) => value.Length <= 12 ? value : value[..12] + "…";
    private static string DescribeBinding(PolicyEffectiveBindingResponse? binding)
    {
        if (binding is null) return "Effective policy binding: unavailable from the policy service.";
        if (!string.Equals(binding.State, "Bound", StringComparison.Ordinal)) return $"Effective policy binding: {binding.State}. {binding.Reason}".Trim();
        var selected = binding.Bindings.FirstOrDefault(x => x.Selected);
        var chain = binding.AddressedChain.Count == 0 ? "no addressed scopes" : string.Join(" → ", binding.AddressedChain);
        return selected is null ? $"Effective policy binding: no selected binding (resolved chain: {chain})." :
            $"Effective policy binding: {selected.Action} at {selected.ScopeKind}; resolved chain: {chain}.";
    }
}

public sealed class KpiTileView
{
    public KpiTileView(KpiValueResponse value)
    {
        Label = value.Definition.Label;
        Question = value.Definition.Question;
        Observation = value.State switch
        {
            KpiState.Produced when value.Value is { } result => $"Observed: {Format(result, value.Definition.Unit)}.",
            KpiState.NotEnoughHistory => $"Not enough history ({value.Denominator ?? 0} seen; needs {value.Definition.MinimumSample}).",
            _ => $"Not produced: {value.Reason ?? value.Detail ?? "reason not supplied"}.",
        };
        Coverage = $"Coverage: {value.Coverage.Analysed} analysed of {value.Coverage.Total}." +
            (value.Coverage.Truncated ? " Limited to the most recent 2,000 messages." : string.Empty) +
            (string.IsNullOrWhiteSpace(value.Coverage.Reason) ? string.Empty : $" {value.Coverage.Reason}");
        Trend = value.Trend switch
        {
            { State: KpiTrendState.Produced, Movement: { } movement } trend => $"Trend: {movement.ToString().ToLowerInvariant()} ({Format(trend.Delta ?? 0, value.Definition.Unit)} change).",
            { } trend => $"Trend not produced: {trend.Reason ?? trend.Detail ?? trend.State.ToString()}.",
            null => "Trend not produced: profile did not return a comparison.",
        };
        Evidence = $"Definition v{value.Definition.Version}. {value.Definition.Question}";
    }
    public string Label { get; }
    public string Question { get; }
    public string Observation { get; }
    public string Trend { get; }
    public string Coverage { get; }
    public string Evidence { get; }
    private static string Format(double value, KpiUnit unit) => unit switch
    {
        KpiUnit.Ratio => value.ToString("P0", CultureInfo.CurrentCulture),
        KpiUnit.Hours => value.ToString("0.##", CultureInfo.CurrentCulture) + " hours",
        KpiUnit.Milliseconds => value.ToString("0", CultureInfo.CurrentCulture) + " ms",
        _ => value.ToString("0.##", CultureInfo.CurrentCulture),
    };
}
