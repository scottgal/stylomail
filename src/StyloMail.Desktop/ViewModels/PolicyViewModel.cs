using System.Collections.ObjectModel;
using System.Globalization;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.ViewModels;

/// <summary>
/// The policy page: what the service will do, which pack is in effect, and which exceptions exist.
/// </summary>
/// <remarks>
/// Read-only. The mission is to make the policy legible, not to author it: no pack, binding or
/// tolerance action is offered here. The Host's raw state, versions and hashes are in Advanced.
/// </remarks>
public sealed class PolicyViewModel : ObservableObject
{
    private StyloMailApiClient? _client;
    private bool _isLoading;
    private string _protectionLabel = "Protection is not read yet.";
    private string _shadowLabel = "Shadow mode is not read yet.";
    private string _jurisdictionLabel = "Jurisdiction is not read yet.";
    private string _effectivePackLabel = "The effective pack is not read yet.";
    private string _statusLabel = "The policy surface has not been read yet.";
    private string _lastUpdatedLabel = "Not read yet.";
    private bool _hasActivePack;
    private string _activePackTitle = string.Empty;
    private string _activePackContext = string.Empty;
    private string _activePackCapture = string.Empty;
    private string _activePackCeiling = string.Empty;
    private string _activePackRetention = string.Empty;
    private bool _hasPacks;
    private bool _hasBindings;
    private bool _sectionFailed;

    public PolicyViewModel(StyloMailApiClient? client = null)
    {
        _client = client;
        Tolerances.CollectionChanged += (_, _) => Raise(nameof(HasTolerances));
        Packs.CollectionChanged += (_, _) =>
        {
            _hasPacks = Packs.Count > 0;
            Raise(nameof(HasPacks));
        };
        Bindings.CollectionChanged += (_, _) =>
        {
            _hasBindings = Bindings.Count > 0;
            Raise(nameof(HasBindings));
        };
    }

    public ObservableCollection<PolicyToleranceRow> Tolerances { get; } = [];

    public ObservableCollection<PolicyPackListItem> Packs { get; } = [];

    public ObservableCollection<PolicyBindingItem> Bindings { get; } = [];

    public ObservableCollection<string> TechnicalLines { get; } = [];

    public bool IsLoading => _isLoading;

    public bool HasTolerances => Tolerances.Count > 0;

    public bool HasPacks => _hasPacks;

    public bool HasBindings => _hasBindings;

    public bool HasActivePack
    {
        get => _hasActivePack;
        private set => Set(ref _hasActivePack, value);
    }

    public string ProtectionLabel
    {
        get => _protectionLabel;
        private set => Set(ref _protectionLabel, value);
    }

    public string ShadowLabel
    {
        get => _shadowLabel;
        private set => Set(ref _shadowLabel, value);
    }

    public string JurisdictionLabel
    {
        get => _jurisdictionLabel;
        private set => Set(ref _jurisdictionLabel, value);
    }

    public string EffectivePackLabel
    {
        get => _effectivePackLabel;
        private set => Set(ref _effectivePackLabel, value);
    }

    public string StatusLabel
    {
        get => _statusLabel;
        private set => Set(ref _statusLabel, value);
    }

    public string LastUpdatedLabel
    {
        get => _lastUpdatedLabel;
        private set => Set(ref _lastUpdatedLabel, value);
    }

    public string ActivePackTitle
    {
        get => _activePackTitle;
        private set => Set(ref _activePackTitle, value);
    }

    public string ActivePackContext
    {
        get => _activePackContext;
        private set => Set(ref _activePackContext, value);
    }

    public string ActivePackCapture
    {
        get => _activePackCapture;
        private set => Set(ref _activePackCapture, value);
    }

    public string ActivePackCeiling
    {
        get => _activePackCeiling;
        private set => Set(ref _activePackCeiling, value);
    }

    public string ActivePackRetention
    {
        get => _activePackRetention;
        private set => Set(ref _activePackRetention, value);
    }

    public void SetClient(StyloMailApiClient? client) => _client = client;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_isLoading) return;

        _isLoading = true;
        _sectionFailed = false;
        TechnicalLines.Clear();
        Tolerances.Clear();
        Packs.Clear();
        Bindings.Clear();
        HasActivePack = false;

        try
        {
            if (_client is null)
            {
                StatusLabel = "The policy surface is unavailable until the console is connected to a Host.";
                return;
            }

            PolicyStatusResponse? status = null;
            try
            {
                status = await _client.GetPolicyStatusAsync(cancellationToken).ConfigureAwait(true);
                ProtectionLabel = status.AuthoritativeEnabled ? "Protection is on." : "Protection is off.";
                ShadowLabel = status.ShadowEnabled ? "Shadow mode is on. Decisions are recorded but nothing is enforced." : "Shadow mode is off.";
                JurisdictionLabel = string.IsNullOrWhiteSpace(status.Jurisdiction)
                    ? "No jurisdiction is declared."
                    : "Jurisdiction: " + status.Jurisdiction + ".";
                EffectivePackLabel = status.EffectivePackId is null
                    ? "No effective pack is selected."
                    : "Effective pack: " + status.EffectivePackId + (status.EffectivePackVersion is null ? string.Empty : " v" + status.EffectivePackVersion) + ".";
                TechnicalLines.Add(
                    $"Status: authoritative {status.AuthoritativeEnabled}; shadow {status.ShadowEnabled}; "
                    + $"jurisdiction {status.Jurisdiction ?? "not declared"}; effective {status.EffectivePackId ?? "none"} "
                    + $"v{status.EffectivePackVersion ?? "-"}; review state {status.ReviewStateAvailable}; cache age {status.CacheAge ?? "not reported"}.");
            }
            catch (StyloMailApiException failure)
            {
                _sectionFailed = true;
                StatusLabel = "Policy status could not be read. " + (failure.Detail ?? failure.Code ?? failure.Message);
            }

            try
            {
                var packs = await _client.GetPolicyPacksAsync(cancellationToken).ConfigureAwait(true);
                foreach (var pack in packs.Packs)
                {
                    Packs.Add(PolicyPackListItem.From(pack));
                    TechnicalLines.Add(
                        $"Pack {pack.Id ?? "none"} v{pack.Version ?? "-"}: state {pack.State ?? "not reported"}; "
                        + $"jurisdiction {pack.Jurisdiction ?? "not reported"}; effective {pack.EffectiveFrom:u}; "
                        + $"review due {pack.ReviewDue:u}; author {pack.Author ?? "not reported"}; "
                        + $"approver {pack.Approver ?? "none"}; second {pack.SecondApprover ?? "none"}; hash {pack.ContentSha256 ?? "not reported"}.");
                }
            }
            catch (StyloMailApiException failure)
            {
                _sectionFailed = true;
                StatusLabel = "Policy packs could not be read. " + (failure.Detail ?? failure.Code ?? failure.Message);
            }

            if (status?.EffectivePackId is { } effectiveId)
            {
                try
                {
                    var pack = await _client.GetPolicyPackAsync(effectiveId, status.EffectivePackVersion, cancellationToken).ConfigureAwait(true);
                    ApplyActivePack(pack);
                }
                catch (StyloMailApiException failure)
                {
                    _sectionFailed = true;
                    StatusLabel = "The effective pack could not be read. " + (failure.Detail ?? failure.Code ?? failure.Message);
                }
            }
            else
            {
                ActivePackTitle = "No active policy pack.";
                ActivePackContext = "Protection is not configured on this Host.";
                ActivePackCapture = string.Empty;
                ActivePackCeiling = string.Empty;
                ActivePackRetention = string.Empty;
            }

            try
            {
                var bindings = await _client.GetPolicyBindingsAsync(cancellationToken).ConfigureAwait(true);
                foreach (var binding in bindings.Bindings)
                {
                    Bindings.Add(PolicyBindingItem.From(binding));
                    TechnicalLines.Add(
                        $"Binding {binding.Id ?? "none"}: state {binding.State ?? "not reported"}; "
                        + $"scope {binding.ScopeKind ?? "not reported"} {binding.ScopeValue ?? "not reported"}; "
                        + $"action {binding.Action ?? "not reported"}; category {binding.CategoryId ?? "none"}; "
                        + $"target {binding.TargetLevel ?? "none"}; pack {binding.PackId ?? "none"} v{binding.PackVersion ?? "-"}; "
                        + $"starts {binding.StartsAt:u}; expires {binding.ExpiresAt:u}; review by {binding.ReviewBy:u}; "
                        + $"on expiry {binding.OnExpiry ?? "not reported"}.");
                }
            }
            catch (StyloMailApiException failure)
            {
                _sectionFailed = true;
                StatusLabel = "Policy bindings could not be read. " + (failure.Detail ?? failure.Code ?? failure.Message);
            }

            TechnicalLines.Add("Routes read: GET /v1/policy/status; GET /v1/policy/packs; GET /v1/policy/packs/{id}?version=; GET /v1/policy/bindings.");
            LastUpdatedLabel = "Updated " + DateTimeOffset.UtcNow.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            StatusLabel = _sectionFailed
                ? "Some policy data could not be read. See the section notes."
                : HasActivePack
                    ? "The effective pack and its tolerances are shown below. This page does not change policy."
                    : "No active policy pack is configured. This page does not change policy.";
        }
        finally
        {
            _isLoading = false;
            Raise(nameof(IsLoading));
        }
    }

    private void ApplyActivePack(PolicyPackResponse pack)
    {
        HasActivePack = true;
        ActivePackTitle = "Pack " + (pack.Id ?? "unknown") + " v" + (pack.Version ?? "-") + ".";
        ActivePackContext = DescribeContext(pack.Context);
        ActivePackCeiling = "The furthest automatic action is " + VerdictLabel(pack.ActionCeiling) + ".";
        ActivePackCapture = pack.CaptureText is { } capture
            ? (string.IsNullOrWhiteSpace(capture.Statement)
                ? "Text capture mode: " + CaptureLabel(capture.Mode) + "."
                : capture.Statement)
            : "Text capture is not described by this pack.";
        ActivePackRetention = pack.EvidenceRetentionDays is { } days
            ? $"Evidence is retained for {days:N0} days. Review deadline {pack.ReviewDeadline ?? "not reported"}."
            : "Evidence retention is not reported.";

        Tolerances.Clear();
        foreach (var (category, level) in pack.CategoryLevels.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            Tolerances.Add(new PolicyToleranceRow(Humanize(category), LevelLabel(level)));
        }

        TechnicalLines.Add(
            $"Effective pack {pack.Id ?? "none"} v{pack.Version ?? "-"}: state {pack.State ?? "not reported"}; "
            + $"context {pack.Context?.Vertical ?? "not reported"}/{pack.Context?.Audience ?? "not reported"}/{pack.Context?.Jurisdiction ?? "not reported"}; "
            + $"ceiling {pack.ActionCeiling ?? "not reported"}; capture {pack.CaptureText?.Mode ?? "not reported"}; "
            + $"retention {pack.EvidenceRetentionDays?.ToString(CultureInfo.InvariantCulture) ?? "not reported"}; "
            + $"rules {pack.RuleSetVersion ?? "not reported"}; scorer {pack.ScorerVersion ?? "not reported"}; "
            + $"hash {pack.ContentSha256 ?? "not reported"}.");
    }

    private static string DescribeContext(PolicyPackContextRow? context)
    {
        if (context is null) return "Pack context is not reported.";

        return "Context: " + (context.Vertical ?? "not declared")
            + ", " + (context.Audience ?? "audience not declared")
            + ", " + (context.Jurisdiction ?? "jurisdiction not declared") + ".";
    }

    private static string Humanize(string id)
        => string.Join(' ', id.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

    private static string LevelLabel(string? level) => level switch
    {
        "Off" => "Off",
        "Observe" => "Observe only",
        "Standard" => "Standard",
        "Strict" => "Strict",
        "ZeroTolerance" => "Zero tolerance",
        null or "" => "Not reported",
        _ => level,
    };

    private static string VerdictLabel(string? verdict) => verdict switch
    {
        "Permit" => "allow.",
        "Observe" => "observe only.",
        "Review" => "send to human review.",
        "Hold" => "hold for re-check.",
        "Quarantine" => "stop before delivery.",
        null or "" => "not reported.",
        _ => verdict,
    };

    private static string CaptureLabel(string? mode) => mode switch
    {
        "None" => "no content",
        "MetadataOnly" => "metadata only",
        "Excerpt" => "bounded excerpt",
        null or "" => "not reported",
        _ => mode,
    };
}

public sealed record PolicyToleranceRow(string Category, string Level);

public sealed record PolicyPackListItem(string Title, string Detail)
{
    public static PolicyPackListItem From(PolicyPackSummaryRow pack)
        => new(
            "Pack " + (pack.Id ?? "unknown") + " v" + (pack.Version ?? "-") + " · " + PackStateLabel(pack.State),
            (pack.Jurisdiction ?? "Jurisdiction not reported")
            + (pack.EffectiveFrom is { } effective ? " · effective " + effective.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture) : string.Empty)
            + (pack.ReviewDue is { } review ? " · review due " + review.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture) : string.Empty) + ".");

    private static string PackStateLabel(string? state) => state switch
    {
        "Draft" => "Draft",
        "Approved" => "Approved",
        "Effective" => "In effect",
        "Superseded" => "Superseded",
        "Withdrawn" => "Withdrawn",
        null or "" => "State not reported",
        _ => state,
    };
}

public sealed record PolicyBindingItem(string Title, string Detail)
{
    public static PolicyBindingItem From(PolicyBindingRow binding)
        => new(
            ScopeLabel(binding.ScopeKind) + " · " + ActionLabel(binding.Action),
            "Pack " + (binding.PackId ?? "not reported") + " v" + (binding.PackVersion ?? "-")
            + (binding.ExpiresAt is { } expires ? " · expires " + expires.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture) : string.Empty)
            + (binding.ReviewBy is { } review ? " · review by " + review.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture) : string.Empty)
            + ".");

    private static string ScopeLabel(string? scope) => scope switch
    {
        "Tenant" => "Whole service",
        "Company" => "Company",
        "Domain" => "Domain",
        "Entity" => "Person",
        "Conversation" => "Conversation",
        null or "" => "Scope not reported",
        _ => scope,
    };

    private static string ActionLabel(string? action) => action switch
    {
        "Permit" => "Permit",
        "Observe" => "Observe only",
        "CaptureText" => "Capture text",
        "RaiseTolerance" => "Raise tolerance",
        "LowerTolerance" => "Lower tolerance",
        null or "" => "Action not reported",
        _ => action,
    };
}
