using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.ViewModels;

/// <summary>
/// The policy page: what the service will do, which pack is in effect, and which exceptions exist.
/// </summary>
/// <remarks>
/// <para>
/// The page is a configuration and approval surface, not a policy engine. Every mutation is a call
/// to an audited Host route; the console never edits a level, approves a pack or applies a binding
/// locally. The Host remains the only authority for validation, the second approver and the
/// effective state.
/// </para>
/// <para>
/// Authoritative evaluation is deliberately absent as an action. It is deployment configuration, and
/// the Host exposes no route to change it. The page states that boundary rather than rendering a
/// control that cannot work. A preview against recent traffic is absent for the same reason: there
/// is no route that evaluates a draft, and the console does not invent a simulation.
/// </para>
/// </remarks>
public sealed class PolicyViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions TechnicalJsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private StyloMailApiClient? _client;
    private bool _isLoading;
    private bool _sectionFailed;
    private bool _hasActivePack;
    private bool _hasPacks;
    private bool _hasBindings;

    private PolicyStatusResponse? _status;
    private string? _effectivePackId;
    private string? _effectivePackVersion;

    private string _protectionLabel = "Protection is not read yet.";
    private string _protectionModeLabel = "Not read yet";
    private string _protectionMeaningLabel = "The protection state has not been read yet.";
    private string _shadowLabel = "Shadow mode is not read yet.";
    private string _jurisdictionLabel = "Jurisdiction is not read yet.";
    private string _effectivePackLabel = "The effective pack is not read yet.";
    private string _statusLabel = "The policy surface has not been read yet.";
    private string _lastUpdatedLabel = "Not read yet.";
    private string _consequenceLabel = "Changing policy is consequential: it affects real mail, and every change is audited.";
    private string _authoritativeStateLabel = "Authoritative evaluation is not read yet.";
    private string _authoritativeGapLabel =
        "Authoritative evaluation and its jurisdiction are deployment configuration. This console has no audited route to change them, so no toggle is shown.";
    private string _previewGapLabel =
        "A preview cannot be produced. The Host exposes no audited route that evaluates a draft against recent traffic, and this console does not invent one.";

    private string _activePackTitle = string.Empty;
    private string _activePackContext = string.Empty;
    private string _activePackCapture = string.Empty;
    private string _activePackCeiling = string.Empty;
    private string _activePackRetention = string.Empty;

    private bool _isDraftEditorOpen;
    private bool _isEditingDraft;
    private string _draftEditorTitle = "New policy pack draft";
    private string _draftId = "pack";
    private string _draftVersion = "1";
    private string _draftJurisdiction = string.Empty;
    private PolicyChoice _draftVertical = PolicyChoices.Find(PolicyChoices.Verticals, "Commerce", "Unknown");
    private PolicyChoice _draftAudience = PolicyChoices.Find(PolicyChoices.Audiences, "Adults", "Unknown");
    private DateTimeOffset? _draftEffectiveDate = LocalDate(DateTimeOffset.Now.AddDays(1));
    private DateTimeOffset? _draftReviewDate = LocalDate(DateTimeOffset.Now.AddDays(91));
    private string _draftEvidenceRetentionDays = "30";
    private string _draftReviewDeadlineHours = "24";
    private string _draftRuleSetVersion = string.Empty;
    private string _draftScorerVersion = string.Empty;
    private PolicyChoice _draftActionCeiling = PolicyChoices.Find(PolicyChoices.ActionCeilings, "Review", "Review");
    private PolicyChoice _draftCaptureMode = PolicyChoices.Find(PolicyChoices.CaptureModes, "None", "None");
    private string _draftCaptureStatement = "No message content is retained.";
    private string _draftCaptureMaxCharacters = "0";
    private string _draftCaptureRetentionDays = "0";
    private string _draftSupersedes = string.Empty;
    private PolicyTemplate _selectedTemplate = PolicyTemplates.All[0];
    private string _draftError = string.Empty;
    private string _draftStatus = string.Empty;
    private bool _draftBusy;

    private PolicyPackListItem? _selectedPack;
    private string _packAction = string.Empty;
    private bool _isPackActionOpen;
    private string _packActionTitle = string.Empty;
    private string _packActionDetail = string.Empty;
    private string _secondApprover = string.Empty;
    private string _packActionReason = string.Empty;
    private bool _packActionConfirmed;
    private string _packActionError = string.Empty;
    private string _packActionStatus = string.Empty;
    private bool _packActionBusy;

    private bool _isBindingEditorOpen;
    private PolicyChoice _bindingScopeKind = PolicyChoices.Find(PolicyChoices.ScopeKinds, "Domain", "Domain");
    private string _bindingScopeValue = string.Empty;
    private PolicyChoice _bindingAction = PolicyChoices.Find(PolicyChoices.BindingActions, "Permit", "Permit");
    private PolicyChoice _bindingCategory = PolicyChoices.Find(PolicyChoices.Categories, "promotion", "promotion");
    private PolicyChoice _bindingTargetLevel = PolicyChoices.Find(PolicyChoices.Levels, "Standard", "Standard");
    private string _bindingPackId = string.Empty;
    private string _bindingPackVersion = string.Empty;
    private string _bindingReason = string.Empty;
    private DateTimeOffset? _bindingStartDate = LocalDate(DateTimeOffset.Now);
    private DateTimeOffset? _bindingExpiryDate = LocalDate(DateTimeOffset.Now.AddDays(30));
    private DateTimeOffset? _bindingReviewDate = LocalDate(DateTimeOffset.Now.AddDays(14));
    private PolicyChoice _bindingOnExpiry = PolicyChoices.Find(PolicyChoices.OnExpiry, "RevertToPack", "RevertToPack");
    private string _bindingConsequence = string.Empty;
    private string _bindingPrecedence = string.Empty;
    private string _bindingApprovedBy = string.Empty;
    private bool _bindingConfirmed;
    private string _bindingError = string.Empty;
    private string _bindingStatus = string.Empty;
    private bool _bindingBusy;

    private PolicyBindingItem? _selectedBinding;
    private bool _isBindingRevokeOpen;
    private string _bindingRevokeReason = string.Empty;
    private bool _bindingRevokeConfirmed;
    private string _bindingRevokeError = string.Empty;
    private string _bindingRevokeStatus = string.Empty;
    private bool _bindingRevokeBusy;

    private string _actionMessage = string.Empty;
    private bool _actionFailed;
    private string _technicalJson = "{}";

    public PolicyViewModel(StyloMailApiClient? client = null)
    {
        _client = client;
        Tolerances.CollectionChanged += (_, _) => Raise(nameof(HasTolerances));
        DraftTolerances.CollectionChanged += (_, _) => Raise(nameof(HasDraftTolerances));
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

    public ObservableCollection<PolicyToleranceEditorRow> DraftTolerances { get; } = [];

    public ObservableCollection<PolicyPackListItem> Packs { get; } = [];

    public ObservableCollection<PolicyBindingItem> Bindings { get; } = [];

    public ObservableCollection<string> TechnicalLines { get; } = [];

    public string TechnicalJson
    {
        get => _technicalJson;
        private set => Set(ref _technicalJson, value);
    }

    public IReadOnlyList<PolicyChoice> VerticalChoices { get; } = PolicyChoices.Verticals;

    public IReadOnlyList<PolicyChoice> AudienceChoices { get; } = PolicyChoices.Audiences;

    public IReadOnlyList<PolicyChoice> ActionCeilingChoices { get; } = PolicyChoices.ActionCeilings;

    public IReadOnlyList<PolicyChoice> CaptureModeChoices { get; } = PolicyChoices.CaptureModes;

    public IReadOnlyList<PolicyChoice> ScopeKindChoices { get; } = PolicyChoices.ScopeKinds;

    public IReadOnlyList<PolicyChoice> BindingActionChoices { get; } = PolicyChoices.BindingActions;

    public IReadOnlyList<PolicyChoice> BindingCategoryChoices { get; } = PolicyChoices.Categories;

    public IReadOnlyList<PolicyChoice> BindingOnExpiryChoices { get; } = PolicyChoices.OnExpiry;

    public IReadOnlyList<PolicyTemplate> TemplateChoices { get; } = PolicyTemplates.All;

    public IReadOnlyList<PolicyChoice> BindingTargetLevels =>
        PolicyCategoryCatalog.IsHardFloor(BindingCategory.Id)
            ? PolicyChoices.HardFloorLevels
            : PolicyChoices.Levels;

    public bool IsLoading => _isLoading;

    public bool HasTolerances => Tolerances.Count > 0;

    public bool HasDraftTolerances => DraftTolerances.Count > 0;

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

    public string ProtectionModeLabel
    {
        get => _protectionModeLabel;
        private set => Set(ref _protectionModeLabel, value);
    }

    public string ProtectionMeaningLabel
    {
        get => _protectionMeaningLabel;
        private set => Set(ref _protectionMeaningLabel, value);
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

    public string ConsequenceLabel
    {
        get => _consequenceLabel;
        private set => Set(ref _consequenceLabel, value);
    }

    public string AuthoritativeStateLabel
    {
        get => _authoritativeStateLabel;
        private set => Set(ref _authoritativeStateLabel, value);
    }

    public string AuthoritativeGapLabel
    {
        get => _authoritativeGapLabel;
        private set => Set(ref _authoritativeGapLabel, value);
    }

    public string PreviewGapLabel
    {
        get => _previewGapLabel;
        private set => Set(ref _previewGapLabel, value);
    }

    /// <summary>There is no console action for authoritative evaluation; this is the safety boundary.</summary>
    public bool CanEnableAuthoritative { get; private set; }

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

    public string ActionMessage
    {
        get => _actionMessage;
        private set
        {
            if (Set(ref _actionMessage, value)) Raise(nameof(HasActionMessage));
        }
    }

    public bool ActionFailed
    {
        get => _actionFailed;
        private set => Set(ref _actionFailed, value);
    }

    public bool HasActionMessage => !string.IsNullOrWhiteSpace(ActionMessage);

    // ---------------------------------------------------------------------------------------------
    // Draft editor
    // ---------------------------------------------------------------------------------------------

    public bool IsDraftEditorOpen
    {
        get => _isDraftEditorOpen;
        private set => Set(ref _isDraftEditorOpen, value);
    }

    public bool IsEditingDraft
    {
        get => _isEditingDraft;
        private set => Set(ref _isEditingDraft, value);
    }

    public string DraftEditorTitle
    {
        get => _draftEditorTitle;
        private set => Set(ref _draftEditorTitle, value);
    }

    public string DraftId
    {
        get => _draftId;
        set => Set(ref _draftId, value);
    }

    public string DraftVersion
    {
        get => _draftVersion;
        set => Set(ref _draftVersion, value);
    }

    public string DraftJurisdiction
    {
        get => _draftJurisdiction;
        set => Set(ref _draftJurisdiction, value);
    }

    public PolicyChoice DraftVertical
    {
        get => _draftVertical;
        set => Set(ref _draftVertical, value);
    }

    public PolicyChoice DraftAudience
    {
        get => _draftAudience;
        set => Set(ref _draftAudience, value);
    }

    public DateTimeOffset? DraftEffectiveDate
    {
        get => _draftEffectiveDate;
        set => Set(ref _draftEffectiveDate, value);
    }

    public DateTimeOffset? DraftReviewDate
    {
        get => _draftReviewDate;
        set => Set(ref _draftReviewDate, value);
    }

    public string DraftEvidenceRetentionDays
    {
        get => _draftEvidenceRetentionDays;
        set => Set(ref _draftEvidenceRetentionDays, value);
    }

    public string DraftReviewDeadlineHours
    {
        get => _draftReviewDeadlineHours;
        set => Set(ref _draftReviewDeadlineHours, value);
    }

    public string DraftRuleSetVersion
    {
        get => _draftRuleSetVersion;
        set => Set(ref _draftRuleSetVersion, value);
    }

    public string DraftScorerVersion
    {
        get => _draftScorerVersion;
        set => Set(ref _draftScorerVersion, value);
    }

    public PolicyChoice DraftActionCeiling
    {
        get => _draftActionCeiling;
        set => Set(ref _draftActionCeiling, value);
    }

    public PolicyChoice DraftCaptureMode
    {
        get => _draftCaptureMode;
        set => Set(ref _draftCaptureMode, value);
    }

    public string DraftCaptureStatement
    {
        get => _draftCaptureStatement;
        set => Set(ref _draftCaptureStatement, value);
    }

    public string DraftCaptureMaxCharacters
    {
        get => _draftCaptureMaxCharacters;
        set => Set(ref _draftCaptureMaxCharacters, value);
    }

    public string DraftCaptureRetentionDays
    {
        get => _draftCaptureRetentionDays;
        set => Set(ref _draftCaptureRetentionDays, value);
    }

    public string DraftSupersedes
    {
        get => _draftSupersedes;
        set => Set(ref _draftSupersedes, value);
    }

    public PolicyTemplate SelectedTemplate
    {
        get => _selectedTemplate;
        set => Set(ref _selectedTemplate, value);
    }

    public string DraftError
    {
        get => _draftError;
        private set
        {
            if (Set(ref _draftError, value)) Raise(nameof(HasDraftError));
        }
    }

    public string DraftStatus
    {
        get => _draftStatus;
        private set => Set(ref _draftStatus, value);
    }

    public bool DraftBusy
    {
        get => _draftBusy;
        private set => Set(ref _draftBusy, value);
    }

    public bool HasDraftError => !string.IsNullOrWhiteSpace(DraftError);

    // ---------------------------------------------------------------------------------------------
    // Pack action
    // ---------------------------------------------------------------------------------------------

    public bool IsPackActionOpen
    {
        get => _isPackActionOpen;
        private set => Set(ref _isPackActionOpen, value);
    }

    public string PackAction
    {
        get => _packAction;
        private set
        {
            if (!Set(ref _packAction, value)) return;
            Raise(nameof(IsPackActionApprove));
            Raise(nameof(IsPackActionActivate));
            Raise(nameof(IsPackActionWithdraw));
            Raise(nameof(IsPackActionDeactivate));
        }
    }

    public bool IsPackActionApprove => PackAction == "approve";

    public bool IsPackActionActivate => PackAction == "activate";

    public bool IsPackActionWithdraw => PackAction == "withdraw";

    public bool IsPackActionDeactivate => PackAction == "deactivate";

    public string PackActionTitle
    {
        get => _packActionTitle;
        private set => Set(ref _packActionTitle, value);
    }

    public string PackActionDetail
    {
        get => _packActionDetail;
        private set => Set(ref _packActionDetail, value);
    }

    public string PackActionDatesLabel => _selectedPack?.DatesLabel ?? "Dates are shown on the pack row.";

    public string SecondApprover
    {
        get => _secondApprover;
        set => Set(ref _secondApprover, value);
    }

    public string PackActionReason
    {
        get => _packActionReason;
        set => Set(ref _packActionReason, value);
    }

    public bool PackActionConfirmed
    {
        get => _packActionConfirmed;
        set => Set(ref _packActionConfirmed, value);
    }

    public string PackActionError
    {
        get => _packActionError;
        private set
        {
            if (Set(ref _packActionError, value)) Raise(nameof(HasPackActionError));
        }
    }

    public string PackActionStatus
    {
        get => _packActionStatus;
        private set => Set(ref _packActionStatus, value);
    }

    public bool PackActionBusy
    {
        get => _packActionBusy;
        private set => Set(ref _packActionBusy, value);
    }

    public bool HasPackActionError => !string.IsNullOrWhiteSpace(PackActionError);

    // ---------------------------------------------------------------------------------------------
    // Binding editor and revocation
    // ---------------------------------------------------------------------------------------------

    public bool IsBindingEditorOpen
    {
        get => _isBindingEditorOpen;
        private set => Set(ref _isBindingEditorOpen, value);
    }

    public PolicyChoice BindingScopeKind
    {
        get => _bindingScopeKind;
        set
        {
            if (!Set(ref _bindingScopeKind, value)) return;
            Raise(nameof(BindingScopeHint));
            UpdateBindingConsequence();
        }
    }

    public string BindingScopeValue
    {
        get => _bindingScopeValue;
        set
        {
            if (!Set(ref _bindingScopeValue, value)) return;
            UpdateBindingConsequence();
        }
    }

    public PolicyChoice BindingAction
    {
        get => _bindingAction;
        set
        {
            if (!Set(ref _bindingAction, value)) return;
            Raise(nameof(BindingNeedsCategory));
            Raise(nameof(BindingNeedsTargetLevel));
            Raise(nameof(BindingNeedsApprover));
            UpdateBindingConsequence();
        }
    }

    public PolicyChoice BindingCategory
    {
        get => _bindingCategory;
        set
        {
            if (!Set(ref _bindingCategory, value)) return;
            Raise(nameof(BindingTargetLevels));
            if (!BindingTargetLevels.Any(choice => choice.Id == _bindingTargetLevel.Id))
            {
                _bindingTargetLevel = BindingTargetLevels[0];
                Raise(nameof(BindingTargetLevel));
            }
            UpdateBindingConsequence();
        }
    }

    public PolicyChoice BindingTargetLevel
    {
        get => _bindingTargetLevel;
        set
        {
            if (!Set(ref _bindingTargetLevel, value)) return;
            UpdateBindingConsequence();
        }
    }

    public string BindingPackId
    {
        get => _bindingPackId;
        set
        {
            if (Set(ref _bindingPackId, value)) UpdateBindingConsequence();
        }
    }

    public string BindingPackVersion
    {
        get => _bindingPackVersion;
        set
        {
            if (Set(ref _bindingPackVersion, value)) UpdateBindingConsequence();
        }
    }

    public string BindingReason
    {
        get => _bindingReason;
        set => Set(ref _bindingReason, value);
    }

    public DateTimeOffset? BindingStartDate
    {
        get => _bindingStartDate;
        set => Set(ref _bindingStartDate, value);
    }

    public DateTimeOffset? BindingExpiryDate
    {
        get => _bindingExpiryDate;
        set => Set(ref _bindingExpiryDate, value);
    }

    public DateTimeOffset? BindingReviewDate
    {
        get => _bindingReviewDate;
        set => Set(ref _bindingReviewDate, value);
    }

    public PolicyChoice BindingOnExpiry
    {
        get => _bindingOnExpiry;
        set
        {
            if (Set(ref _bindingOnExpiry, value)) UpdateBindingConsequence();
        }
    }

    public string BindingApprovedBy
    {
        get => _bindingApprovedBy;
        set => Set(ref _bindingApprovedBy, value);
    }

    public bool BindingConfirmed
    {
        get => _bindingConfirmed;
        set => Set(ref _bindingConfirmed, value);
    }

    public string BindingError
    {
        get => _bindingError;
        private set
        {
            if (Set(ref _bindingError, value)) Raise(nameof(HasBindingError));
        }
    }

    public string BindingStatus
    {
        get => _bindingStatus;
        private set => Set(ref _bindingStatus, value);
    }

    public bool BindingBusy
    {
        get => _bindingBusy;
        private set => Set(ref _bindingBusy, value);
    }

    public bool HasBindingError => !string.IsNullOrWhiteSpace(BindingError);

    public bool BindingNeedsCategory =>
        BindingAction.Id is "RaiseTolerance" or "LowerTolerance";

    public bool BindingNeedsTargetLevel => BindingNeedsCategory;

    public bool BindingNeedsApprover =>
        BindingAction.Id is "CaptureText" or "LowerTolerance";

    public string BindingScopeHint => BindingScopeKind.Id switch
    {
        "Tenant" => "Every conversation in this tenant. The widest scope.",
        "Company" => "Everyone filed under one company. Narrower than the whole service.",
        "Domain" => "Everyone at one email domain. Narrower than a company.",
        "Entity" => "One person. Narrower than a domain.",
        "Conversation" => "One conversation. The narrowest scope.",
        _ => "Choose the smallest scope that fits the exception.",
    };

    public string BindingConsequence
    {
        get => _bindingConsequence;
        private set => Set(ref _bindingConsequence, value);
    }

    public string BindingPrecedence
    {
        get => _bindingPrecedence;
        private set => Set(ref _bindingPrecedence, value);
    }

    public bool IsBindingRevokeOpen
    {
        get => _isBindingRevokeOpen;
        private set => Set(ref _isBindingRevokeOpen, value);
    }

    public string BindingRevokeTitle
    {
        get => _selectedBinding is null ? "Revoke exception" : "Revoke " + _selectedBinding.Title;
    }

    public string BindingRevokeReason
    {
        get => _bindingRevokeReason;
        set => Set(ref _bindingRevokeReason, value);
    }

    public bool BindingRevokeConfirmed
    {
        get => _bindingRevokeConfirmed;
        set => Set(ref _bindingRevokeConfirmed, value);
    }

    public string BindingRevokeError
    {
        get => _bindingRevokeError;
        private set
        {
            if (Set(ref _bindingRevokeError, value)) Raise(nameof(HasBindingRevokeError));
        }
    }

    public string BindingRevokeStatus
    {
        get => _bindingRevokeStatus;
        private set => Set(ref _bindingRevokeStatus, value);
    }

    public bool BindingRevokeBusy
    {
        get => _bindingRevokeBusy;
        private set => Set(ref _bindingRevokeBusy, value);
    }

    public bool HasBindingRevokeError => !string.IsNullOrWhiteSpace(BindingRevokeError);

    public void SetClient(StyloMailApiClient? client) => _client = client;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_isLoading) return;

        _isLoading = true;
        _sectionFailed = false;
        TechnicalLines.Clear();
        TechnicalJson = "{}";
        Tolerances.Clear();
        Packs.Clear();
        Bindings.Clear();
        HasActivePack = false;
        _status = null;
        _effectivePackId = null;
        _effectivePackVersion = null;

        try
        {
            if (_client is null)
            {
                ProtectionModeLabel = "Not connected";
                ProtectionMeaningLabel = "The console is not connected to a Host, so policy cannot be read.";
                StatusLabel = "The policy surface is unavailable until the console is connected to a Host.";
                return;
            }

            PolicyStatusResponse? status = null;
            try
            {
                status = await _client.GetPolicyStatusAsync(cancellationToken).ConfigureAwait(true);
                _status = status;
                ApplyStatus(status);
            }
            catch (StyloMailApiException failure)
            {
                _sectionFailed = true;
                ProtectionModeLabel = "Not read";
                ProtectionMeaningLabel = "The Host did not answer the policy status read.";
                StatusLabel = "Policy status could not be read. " + Describe(failure);
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
                StatusLabel = "Policy packs could not be read. " + Describe(failure);
            }

            if (status?.EffectivePackId is { } effectiveId)
            {
                _effectivePackId = effectiveId;
                _effectivePackVersion = status.EffectivePackVersion;
                try
                {
                    var pack = await _client.GetPolicyPackAsync(effectiveId, status.EffectivePackVersion, cancellationToken).ConfigureAwait(true);
                    ApplyActivePack(pack);
                }
                catch (StyloMailApiException failure)
                {
                    _sectionFailed = true;
                    StatusLabel = "The effective pack could not be read. " + Describe(failure);
                }
            }
            else
            {
                ActivePackTitle = "No active policy pack.";
                ActivePackContext = "No pack is in force for this tenant. A draft is not protection.";
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
                StatusLabel = "Policy bindings could not be read. " + Describe(failure);
            }

            TechnicalLines.Add(
                "Routes read: GET /v1/policy/status; GET /v1/policy/packs; GET /v1/policy/packs/{id}?version=; GET /v1/policy/bindings.");
            TechnicalLines.Add(
                "Routes written: POST /v1/policy/packs; POST /v1/policy/packs/{id}/{version}/approve; "
                + "POST /v1/policy/packs/{id}/{version}/activate; POST /v1/policy/packs/{id}/{version}/withdraw; "
                + "POST /v1/policy/packs/{id}/{version}/deactivate; POST /v1/policy/bindings; "
                + "POST /v1/policy/bindings/{id}/revoke.");
            LastUpdatedLabel = "Updated " + DateTimeOffset.UtcNow.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            TechnicalJson = BuildTechnicalJson();
            StatusLabel = _sectionFailed
                ? "Some policy data could not be read. See the section notes."
                : HasActivePack
                    ? "The effective pack and its tolerances are shown below. Changes are consequential and audited."
                    : HasPacks
                        ? "No effective pack is in force. Draft and approved versions are listed below."
                        : "No active policy pack is configured. A draft can be created below, but it is not protection until it is approved and activated.";
        }
        finally
        {
            _isLoading = false;
            Raise(nameof(IsLoading));
        }
    }

    private void ApplyStatus(PolicyStatusResponse status)
    {
        var authoritative = status.AuthoritativeEnabled;
        var shadow = status.ShadowEnabled;

        ProtectionLabel = authoritative ? "Protection is on." : "Protection is off.";
        ShadowLabel = shadow
            ? "Shadow mode is on. Decisions are recorded but nothing is enforced."
            : "Shadow mode is off.";

        ProtectionModeLabel = authoritative ? "Authoritative" : shadow ? "Shadow" : "Off";
        ProtectionMeaningLabel = authoritative
            ? (shadow
                ? "Policy is evaluating mail and can hold, review or stop it. Shadow recording is also on; it does not change enforcement."
                : "Policy is evaluating mail and can hold, review or stop it.")
            : shadow
                ? "Policy is scoring mail and recording what it would do, but nothing is enforced."
                : "Policy is not evaluating mail. Delivery behaves as it did before policy packs.";

        AuthoritativeStateLabel = authoritative
            ? "Authoritative evaluation is on for this tenant."
            : "Authoritative evaluation is off for this tenant.";

        JurisdictionLabel = string.IsNullOrWhiteSpace(status.Jurisdiction)
            ? "No jurisdiction is declared."
            : "Jurisdiction: " + status.Jurisdiction + ".";
        EffectivePackLabel = status.EffectivePackId is null
            ? "No effective pack is selected."
            : "Effective pack: " + status.EffectivePackId
                + (status.EffectivePackVersion is null ? string.Empty : " v" + status.EffectivePackVersion) + ".";

        TechnicalLines.Add(
            $"Status: authoritative {status.AuthoritativeEnabled}; shadow {status.ShadowEnabled}; "
            + $"jurisdiction {status.Jurisdiction ?? "not declared"}; effective {status.EffectivePackId ?? "none"} "
            + $"v{status.EffectivePackVersion ?? "-"}; review state {status.ReviewStateAvailable}; cache age {status.CacheAge ?? "not reported"}.");
    }

    private void ApplyActivePack(PolicyPackResponse pack)
    {
        HasActivePack = true;
        ActivePackTitle = "Pack " + (pack.Id ?? "unknown") + " v" + (pack.Version ?? "-") + ".";
        ActivePackContext = DescribeContext(pack.Context);
        ActivePackCeiling = "The furthest automatic action is " + VerdictLabel(pack.ActionCeiling) + ".";
        ActivePackCapture = pack.CaptureText is { } capture
            ? (string.IsNullOrWhiteSpace(capture.Statement)
                ? "Text capture mode: " + PolicyChoices.Label(PolicyChoices.CaptureModes, capture.Mode) + "."
                : capture.Statement)
            : "Text capture is not described by this pack.";
        ActivePackRetention = pack.EvidenceRetentionDays is { } days
            ? $"Evidence is retained for {days:N0} days. Review deadline {pack.ReviewDeadline ?? "not reported"}."
            : "Evidence retention is not reported.";

        Tolerances.Clear();
        var levels = pack.CategoryLevels ?? new Dictionary<string, string>();
        foreach (var category in PolicyCategoryCatalog.All)
        {
            var declared = levels.TryGetValue(category.Id, out var level);
            Tolerances.Add(new PolicyToleranceRow(
                category.Label,
                declared ? PolicyChoices.LevelLabel(level) : "Not declared",
                category.Id,
                category.Description,
                category.IsHardFloor,
                category.IsHardFloor ? PolicyCategoryCatalog.HardFloorNote : string.Empty));
        }

        foreach (var (categoryId, level) in levels
                     .Where(pair => PolicyCategoryCatalog.Find(pair.Key) is null)
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Tolerances.Add(new PolicyToleranceRow(
                Humanize(categoryId),
                PolicyChoices.LevelLabel(level),
                categoryId,
                "The Host reported a category this build does not describe. Treat it as unknown, not safe.",
                false,
                string.Empty));
        }

        TechnicalLines.Add(
            $"Effective pack {pack.Id ?? "none"} v{pack.Version ?? "-"}: state {pack.State ?? "not reported"}; "
            + $"context {pack.Context?.Vertical ?? "not reported"}/{pack.Context?.Audience ?? "not reported"}/{pack.Context?.Jurisdiction ?? "not reported"}; "
            + $"ceiling {pack.ActionCeiling ?? "not reported"}; capture {pack.CaptureText?.Mode ?? "not reported"}; "
            + $"retention {pack.EvidenceRetentionDays?.ToString(CultureInfo.InvariantCulture) ?? "not reported"}; "
            + $"rules {pack.RuleSetVersion ?? "not reported"}; scorer {pack.ScorerVersion ?? "not reported"}; "
            + $"hash {pack.ContentSha256 ?? "not reported"}.");
    }

    // ---------------------------------------------------------------------------------------------
    // Draft lifecycle
    // ---------------------------------------------------------------------------------------------

    public async Task OpenNewDraftAsync(CancellationToken cancellationToken = default)
    {
        IsEditingDraft = false;
        DraftEditorTitle = "New policy pack draft";
        DraftId = "pack";
        DraftVersion = SuggestVersion();
        DraftJurisdiction = _status?.Jurisdiction ?? string.Empty;
        DraftVertical = PolicyChoices.Find(PolicyChoices.Verticals, "Commerce", "Unknown");
        DraftAudience = PolicyChoices.Find(PolicyChoices.Audiences, "Adults", "Unknown");
        DraftEffectiveDate = LocalDate(DateTimeOffset.Now.AddDays(1));
        DraftReviewDate = LocalDate(DateTimeOffset.Now.AddDays(91));
        DraftEvidenceRetentionDays = "30";
        DraftReviewDeadlineHours = "24";
        DraftRuleSetVersion = string.Empty;
        DraftScorerVersion = string.Empty;
        DraftActionCeiling = PolicyChoices.Find(PolicyChoices.ActionCeilings, "Review", "Review");
        DraftCaptureMode = PolicyChoices.Find(PolicyChoices.CaptureModes, "None", "None");
        DraftCaptureStatement = "No message content is retained.";
        DraftCaptureMaxCharacters = "0";
        DraftCaptureRetentionDays = "0";
        DraftSupersedes = string.Empty;
        SelectedTemplate = PolicyTemplates.All[0];
        DraftError = string.Empty;
        DraftStatus = string.Empty;
        FillDraftTolerances(PolicyTemplates.All[0].Levels);
        ApplyTemplate();

        // Prefill provenance from the most recent pack that could govern a successor. A current
        // draft is not a predecessor; it is another proposal.
        var predecessor = Packs.FirstOrDefault(pack => pack.StateId == "Effective")
            ?? Packs.FirstOrDefault(pack => pack.StateId == "Approved");
        if (predecessor is not null && _client is not null)
        {
            try
            {
                var pack = await _client.GetPolicyPackAsync(predecessor.Id, predecessor.Version, cancellationToken).ConfigureAwait(true);
                DraftRuleSetVersion = pack.RuleSetVersion ?? string.Empty;
                DraftScorerVersion = pack.ScorerVersion ?? string.Empty;
                DraftEvidenceRetentionDays = (pack.EvidenceRetentionDays ?? 30).ToString(CultureInfo.InvariantCulture);
                DraftActionCeiling = PolicyChoices.Find(PolicyChoices.ActionCeilings, pack.ActionCeiling, "Review");
                DraftCaptureMode = PolicyChoices.Find(PolicyChoices.CaptureModes, pack.CaptureText?.Mode, "None");
                DraftCaptureStatement = pack.CaptureText?.Statement ?? string.Empty;
                DraftCaptureMaxCharacters = (pack.CaptureText?.MaxCharacters ?? 0).ToString(CultureInfo.InvariantCulture);
                DraftCaptureRetentionDays = (pack.CaptureText?.RetentionDays ?? 0).ToString(CultureInfo.InvariantCulture);
                DraftSupersedes = predecessor.Id + "@" + predecessor.Version;
                if (pack.Context is not null)
                {
                    DraftVertical = PolicyChoices.Find(PolicyChoices.Verticals, pack.Context.Vertical, "Unknown");
                    DraftAudience = PolicyChoices.Find(PolicyChoices.Audiences, pack.Context.Audience, "Unknown");
                }
            }
            catch (StyloMailApiException)
            {
                // Provenance is helpful, not required. The operator can fill the fields and the
                // Host validates them at activation.
            }
        }

        IsDraftEditorOpen = true;
    }

    public async Task EditDraftAsync(PolicyPackListItem item, CancellationToken cancellationToken = default)
    {
        if (_client is null) return;

        DraftError = string.Empty;
        DraftStatus = string.Empty;
        try
        {
            var pack = await _client.GetPolicyPackAsync(item.Id, item.Version, cancellationToken).ConfigureAwait(true);
            IsEditingDraft = true;
            DraftEditorTitle = "Edit draft " + item.Id + " v" + item.Version;
            DraftId = pack.Id ?? item.Id;
            DraftVersion = pack.Version ?? item.Version;
            DraftJurisdiction = pack.Context?.Jurisdiction ?? string.Empty;
            DraftVertical = PolicyChoices.Find(PolicyChoices.Verticals, pack.Context?.Vertical, "Unknown");
            DraftAudience = PolicyChoices.Find(PolicyChoices.Audiences, pack.Context?.Audience, "Unknown");
            DraftEffectiveDate = pack.EffectiveFrom?.ToLocalTime();
            DraftReviewDate = pack.ReviewDue?.ToLocalTime();
            DraftEvidenceRetentionDays = (pack.EvidenceRetentionDays ?? 30).ToString(CultureInfo.InvariantCulture);
            DraftReviewDeadlineHours = ParseDeadlineHours(pack.ReviewDeadline).ToString(CultureInfo.InvariantCulture);
            DraftRuleSetVersion = pack.RuleSetVersion ?? string.Empty;
            DraftScorerVersion = pack.ScorerVersion ?? string.Empty;
            DraftActionCeiling = PolicyChoices.Find(PolicyChoices.ActionCeilings, pack.ActionCeiling, "Review");
            DraftCaptureMode = PolicyChoices.Find(PolicyChoices.CaptureModes, pack.CaptureText?.Mode, "None");
            DraftCaptureStatement = pack.CaptureText?.Statement ?? string.Empty;
            DraftCaptureMaxCharacters = (pack.CaptureText?.MaxCharacters ?? 0).ToString(CultureInfo.InvariantCulture);
            DraftCaptureRetentionDays = (pack.CaptureText?.RetentionDays ?? 0).ToString(CultureInfo.InvariantCulture);
            DraftSupersedes = pack.Supersedes ?? string.Empty;
            SelectedTemplate = PolicyTemplates.All[0];
            FillDraftTolerances(pack.CategoryLevels);
            IsDraftEditorOpen = true;
        }
        catch (StyloMailApiException failure)
        {
            ActionFailed = true;
            ActionMessage = "The draft could not be opened. " + Describe(failure);
        }
    }

    public void ApplyTemplate()
    {
        if (SelectedTemplate is null) return;

        foreach (var row in DraftTolerances)
        {
            if (SelectedTemplate.Levels.TryGetValue(row.CategoryId, out var level))
            {
                row.SelectedLevel = row.LevelOptions.FirstOrDefault(choice => choice.Id == level)
                    ?? row.LevelOptions[0];
            }
        }

        DraftActionCeiling = PolicyChoices.Find(PolicyChoices.ActionCeilings, SelectedTemplate.ActionCeiling, "Review");
        DraftStatus = "Starting point applied. Review every level before approval.";
    }

    public async Task SaveDraftAsync(CancellationToken cancellationToken = default)
    {
        if (_client is null || DraftBusy) return;

        DraftError = string.Empty;
        DraftStatus = string.Empty;

        if (string.IsNullOrWhiteSpace(DraftId)
            || string.IsNullOrWhiteSpace(DraftVersion)
            || string.IsNullOrWhiteSpace(DraftJurisdiction)
            || DraftEffectiveDate is null
            || string.IsNullOrWhiteSpace(DraftRuleSetVersion)
            || string.IsNullOrWhiteSpace(DraftScorerVersion))
        {
            DraftError = "Pack id, version, jurisdiction, effective date, rule-set version and scorer version are required.";
            return;
        }

        if (DraftEffectiveDate is { } effectiveDate && DraftReviewDate is { } reviewDate && reviewDate <= effectiveDate)
        {
            DraftError = "The review date must fall after the effective date.";
            return;
        }

        if (!int.TryParse(DraftEvidenceRetentionDays, NumberStyles.Integer, CultureInfo.InvariantCulture, out var retention)
            || retention <= 0)
        {
            DraftError = "Evidence retention must be a positive number of days.";
            return;
        }

        if (!int.TryParse(DraftReviewDeadlineHours, NumberStyles.Integer, CultureInfo.InvariantCulture, out var deadlineHours)
            || deadlineHours is < 1 or > 168)
        {
            DraftError = "The review deadline must be between 1 and 168 hours.";
            return;
        }

        var captureMode = DraftCaptureMode.Id;
        var captureMax = 0;
        var captureRetention = 0;
        var captureStatement = DraftCaptureStatement.Trim();
        if (captureMode != "None")
        {
            if (string.IsNullOrWhiteSpace(captureStatement))
            {
                DraftError = "Capturing content needs a statement of what is stored and for how long.";
                return;
            }

            if (!int.TryParse(DraftCaptureMaxCharacters, NumberStyles.Integer, CultureInfo.InvariantCulture, out captureMax)
                || captureMax <= 0)
            {
                DraftError = "Capturing content needs a positive character bound.";
                return;
            }

            if (!int.TryParse(DraftCaptureRetentionDays, NumberStyles.Integer, CultureInfo.InvariantCulture, out captureRetention)
                || captureRetention <= 0)
            {
                DraftError = "Capturing content needs a positive retention clock.";
                return;
            }
        }
        else
        {
            captureMax = 0;
            captureRetention = 0;
            if (string.IsNullOrWhiteSpace(captureStatement))
            {
                captureStatement = "No message content is retained.";
            }
        }

        var levels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in DraftTolerances)
        {
            if (row.Category.IsHardFloor && row.SelectedLevel.Id is ("Off" or "Observe"))
            {
                DraftError = "'" + row.Category.Label + "' is a hard floor and cannot be set below Strict.";
                return;
            }

            if (!string.IsNullOrEmpty(row.SelectedLevel.Id))
            {
                levels[row.CategoryId] = row.SelectedLevel.Id;
            }
        }

        var requestEffectiveFrom = DraftEffectiveDate!.Value;
        var request = new PolicyPackDraftRequest
        {
            Id = DraftId.Trim(),
            Version = DraftVersion.Trim(),
            Context = new PolicyPackContextRow
            {
                Vertical = DraftVertical.Id,
                Audience = DraftAudience.Id,
                Jurisdiction = DraftJurisdiction.Trim(),
            },
            CategoryLevels = levels,
            ActionCeiling = DraftActionCeiling.Id,
            CaptureText = new TextCaptureRulesRow
            {
                Mode = captureMode,
                MaxCharacters = captureMax,
                RetentionDays = captureRetention,
                Statement = captureStatement,
            },
            EvidenceRetentionDays = retention,
            ReviewDeadline = TimeSpan.FromHours(deadlineHours).ToString("c", CultureInfo.InvariantCulture),
            RuleSetVersion = DraftRuleSetVersion.Trim(),
            ScorerVersion = DraftScorerVersion.Trim(),
            EffectiveFrom = requestEffectiveFrom,
            ReviewDue = DraftReviewDate,
            Supersedes = string.IsNullOrWhiteSpace(DraftSupersedes) ? null : DraftSupersedes.Trim(),
        };

        DraftBusy = true;
        try
        {
            var saved = await _client.CreatePolicyDraftAsync(request, cancellationToken).ConfigureAwait(true);
            IsDraftEditorOpen = false;
            ActionFailed = false;
            ActionMessage = "Draft " + (saved.Id ?? request.Id) + " v" + (saved.Version ?? request.Version) + " saved. It is not protection until it is approved and activated.";
            await LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (StyloMailApiException failure)
        {
            DraftError = Describe(failure);
        }
        finally
        {
            DraftBusy = false;
        }
    }

    public void CancelDraft()
    {
        IsDraftEditorOpen = false;
        DraftError = string.Empty;
        DraftStatus = string.Empty;
    }

    // ---------------------------------------------------------------------------------------------
    // Pack lifecycle actions
    // ---------------------------------------------------------------------------------------------

    public void BeginPackAction(PolicyPackListItem item, string action)
    {
        _selectedPack = item;
        PackAction = action;
        IsPackActionOpen = true;
        PackActionError = string.Empty;
        PackActionStatus = string.Empty;
        PackActionConfirmed = false;
        PackActionReason = string.Empty;
        SecondApprover = string.Empty;

        Raise(nameof(PackActionDatesLabel));

        (PackActionTitle, PackActionDetail) = action switch
        {
            "approve" => (
                "Approve " + item.Title,
                "Approval records you as the approver. If this version lowers a tolerance or widens content capture, name a second administrator. The Host checks that the second person holds Administer and is not you, and refuses the approval otherwise."),
            "activate" => (
                "Activate " + item.Title,
                "Activation changes what policy does for real mail. The effective and review dates were set on the draft; a future effective date is scheduled rather than applied now. This console does not enable authoritative evaluation."),
            "withdraw" => (
                "Withdraw " + item.Title,
                "Withdrawing a draft or approved version cannot be undone. The reason is the audit record."),
            "deactivate" => (
                "Deactivate " + item.Title,
                "Deactivation takes the effective pack off the policy path and returns the tenant to the legacy engine. The reason is the audit record."),
            _ => ("Policy action", "Choose an action."),
        };
    }

    public async Task ConfirmPackActionAsync(CancellationToken cancellationToken = default)
    {
        if (_client is null || _selectedPack is null || PackActionBusy) return;

        PackActionError = string.Empty;
        PackActionStatus = string.Empty;

        if (!PackActionConfirmed)
        {
            PackActionError = "Confirm that you have reviewed this change before applying it.";
            return;
        }

        if (PackAction is ("withdraw" or "deactivate") && string.IsNullOrWhiteSpace(PackActionReason))
        {
            PackActionError = "Give the audit reason for this change.";
            return;
        }

        PackActionBusy = true;
        try
        {
            switch (PackAction)
            {
                case "approve":
                    await _client.ApprovePolicyPackAsync(
                        _selectedPack.Id,
                        _selectedPack.Version,
                        string.IsNullOrWhiteSpace(SecondApprover) ? null : SecondApprover.Trim(),
                        cancellationToken).ConfigureAwait(true);
                    ActionMessage = "Approved " + _selectedPack.Reference + ". Activate it when the effective date is right.";
                    break;

                case "activate":
                    await _client.ActivatePolicyPackAsync(_selectedPack.Id, _selectedPack.Version, cancellationToken).ConfigureAwait(true);
                    ActionMessage = "Activation requested for " + _selectedPack.Reference + ". The Host reports whether it is effective now or scheduled.";
                    break;

                case "withdraw":
                    await _client.WithdrawPolicyPackAsync(_selectedPack.Id, _selectedPack.Version, PackActionReason.Trim(), cancellationToken).ConfigureAwait(true);
                    ActionMessage = "Withdrawn " + _selectedPack.Reference + ".";
                    break;

                case "deactivate":
                    await _client.DeactivatePolicyPackAsync(_selectedPack.Id, _selectedPack.Version, PackActionReason.Trim(), cancellationToken).ConfigureAwait(true);
                    ActionMessage = "Deactivated " + _selectedPack.Reference + ". The tenant is back on the legacy path.";
                    break;
            }

            IsPackActionOpen = false;
            ActionFailed = false;
            await LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (StyloMailApiException failure)
        {
            PackActionError = Describe(failure);
        }
        finally
        {
            PackActionBusy = false;
        }
    }

    public void CancelPackAction()
    {
        IsPackActionOpen = false;
        PackActionError = string.Empty;
        PackActionStatus = string.Empty;
    }

    // ---------------------------------------------------------------------------------------------
    // Bindings
    // ---------------------------------------------------------------------------------------------

    public void OpenNewBinding()
    {
        BindingError = string.Empty;
        BindingStatus = string.Empty;
        BindingConfirmed = false;
        BindingScopeKind = PolicyChoices.Find(PolicyChoices.ScopeKinds, "Domain", "Domain");
        BindingScopeValue = string.Empty;
        BindingAction = PolicyChoices.Find(PolicyChoices.BindingActions, "Permit", "Permit");
        BindingCategory = PolicyChoices.Find(PolicyChoices.Categories, "promotion", "promotion");
        BindingTargetLevel = PolicyChoices.Find(PolicyChoices.Levels, "Standard", "Standard");
        BindingReason = string.Empty;
        BindingStartDate = LocalDate(DateTimeOffset.Now);
        BindingExpiryDate = LocalDate(DateTimeOffset.Now.AddDays(30));
        BindingReviewDate = LocalDate(DateTimeOffset.Now.AddDays(14));
        BindingOnExpiry = PolicyChoices.Find(PolicyChoices.OnExpiry, "RevertToPack", "RevertToPack");
        BindingApprovedBy = string.Empty;

        var pack = Packs.FirstOrDefault(item => item.Id == _effectivePackId && item.Version == _effectivePackVersion)
            ?? Packs.FirstOrDefault(item => item.StateId == "Effective")
            ?? Packs.FirstOrDefault();
        BindingPackId = pack?.Id ?? _effectivePackId ?? string.Empty;
        BindingPackVersion = pack?.Version ?? _effectivePackVersion ?? string.Empty;

        UpdateBindingConsequence();
        IsBindingEditorOpen = true;
    }

    public async Task SaveBindingAsync(CancellationToken cancellationToken = default)
    {
        if (_client is null || BindingBusy) return;

        BindingError = string.Empty;
        BindingStatus = string.Empty;

        if (string.IsNullOrWhiteSpace(BindingScopeValue)
            || string.IsNullOrWhiteSpace(BindingPackId)
            || string.IsNullOrWhiteSpace(BindingPackVersion)
            || string.IsNullOrWhiteSpace(BindingReason)
            || BindingStartDate is null
            || BindingExpiryDate is null
            || BindingReviewDate is null)
        {
            BindingError = "Scope value, pack version, reason and all three dates are required.";
            return;
        }

        if (BindingExpiryDate <= BindingStartDate)
        {
            BindingError = "The expiry date must fall after the start date. There is no unbounded exception.";
            return;
        }

        if (BindingReviewDate < BindingStartDate || BindingReviewDate > BindingExpiryDate)
        {
            BindingError = "The review date must fall inside the exception's own window.";
            return;
        }

        if (BindingNeedsCategory)
        {
            if (!BindingNeedsTargetLevel)
            {
                BindingError = "Choose the category this tolerance change applies to.";
                return;
            }

            if (PolicyCategoryCatalog.IsHardFloor(BindingCategory.Id)
                && BindingTargetLevel.Id is ("Off" or "Observe"))
            {
                BindingError = "'" + BindingCategory.Label + "' is a hard floor. An exception cannot lower it below Strict.";
                return;
            }
        }

        if (BindingNeedsApprover && string.IsNullOrWhiteSpace(BindingApprovedBy))
        {
            BindingError = "This action needs a second named administrator.";
            return;
        }

        if (!BindingConfirmed)
        {
            BindingError = "Review the consequence and confirm before applying the exception.";
            return;
        }

        var requestStartsAt = BindingStartDate!.Value;
        var requestExpiresAt = BindingExpiryDate!.Value;
        var requestReviewBy = BindingReviewDate!.Value;
        var request = new PolicyBindingRequest
        {
            ScopeKind = BindingScopeKind.Id,
            ScopeValue = BindingScopeValue.Trim(),
            Action = BindingAction.Id,
            CategoryId = BindingNeedsCategory ? BindingCategory.Id : null,
            TargetLevel = BindingNeedsTargetLevel ? BindingTargetLevel.Id : null,
            PackId = BindingPackId.Trim(),
            PackVersion = BindingPackVersion.Trim(),
            ApprovedBy = string.IsNullOrWhiteSpace(BindingApprovedBy) ? null : BindingApprovedBy.Trim(),
            Reason = BindingReason.Trim(),
            StartsAt = requestStartsAt,
            ExpiresAt = requestExpiresAt,
            ReviewBy = requestReviewBy,
            OnExpiry = BindingOnExpiry.Id,
        };

        BindingBusy = true;
        try
        {
            var saved = await _client.CreatePolicyBindingAsync(request, cancellationToken).ConfigureAwait(true);
            IsBindingEditorOpen = false;
            ActionFailed = false;
            ActionMessage = "Exception " + (saved.Id ?? "created") + " applied. It expires on " + FormatDate(BindingExpiryDate) + ".";
            await LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (StyloMailApiException failure)
        {
            BindingError = Describe(failure);
        }
        finally
        {
            BindingBusy = false;
        }
    }

    public void CancelBindingEditor()
    {
        IsBindingEditorOpen = false;
        BindingError = string.Empty;
        BindingStatus = string.Empty;
    }

    public void BeginRevokeBinding(PolicyBindingItem item)
    {
        _selectedBinding = item;
        BindingRevokeReason = string.Empty;
        BindingRevokeConfirmed = false;
        BindingRevokeError = string.Empty;
        BindingRevokeStatus = string.Empty;
        IsBindingRevokeOpen = true;
        Raise(nameof(BindingRevokeTitle));
    }

    public async Task ConfirmRevokeBindingAsync(CancellationToken cancellationToken = default)
    {
        if (_client is null || _selectedBinding is null || BindingRevokeBusy) return;

        BindingRevokeError = string.Empty;
        BindingRevokeStatus = string.Empty;

        if (!BindingRevokeConfirmed)
        {
            BindingRevokeError = "Confirm that you want to revoke this exception.";
            return;
        }

        if (string.IsNullOrWhiteSpace(BindingRevokeReason))
        {
            BindingRevokeError = "Give the audit reason for revoking this exception.";
            return;
        }

        BindingRevokeBusy = true;
        try
        {
            await _client.RevokePolicyBindingAsync(_selectedBinding.Id, BindingRevokeReason.Trim(), cancellationToken).ConfigureAwait(true);
            IsBindingRevokeOpen = false;
            ActionFailed = false;
            ActionMessage = "Revoked the exception for " + _selectedBinding.Scope + ".";
            await LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (StyloMailApiException failure)
        {
            BindingRevokeError = Describe(failure);
        }
        finally
        {
            BindingRevokeBusy = false;
        }
    }

    public void CancelRevokeBinding()
    {
        IsBindingRevokeOpen = false;
        BindingRevokeError = string.Empty;
        BindingRevokeStatus = string.Empty;
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private void FillDraftTolerances(IReadOnlyDictionary<string, string>? levels)
    {
        DraftTolerances.Clear();
        foreach (var category in PolicyCategoryCatalog.All)
        {
            string? level = null;
            var declared = levels is not null && levels.TryGetValue(category.Id, out level);
            DraftTolerances.Add(new PolicyToleranceEditorRow(category, declared ? level : null));
        }
    }

    private void UpdateBindingConsequence()
    {
        var scope = BindingScopeKind.Label.ToLowerInvariant();
        var where = string.IsNullOrWhiteSpace(BindingScopeValue)
            ? "the " + scope + " scope"
            : "the " + scope + " '" + BindingScopeValue.Trim() + "'";

        BindingPrecedence =
            "Source: this exception, against pack " + (string.IsNullOrWhiteSpace(BindingPackId) ? "not chosen" : BindingPackId + " v" + BindingPackVersion)
            + ". When several exceptions apply, the more protective target wins, then the narrower scope, then the later start. "
            + "On expiry the scope " + (BindingOnExpiry.Id == "HoldForReview" ? "stays held for a person to review." : "returns to the pack.");

        BindingConsequence = BindingAction.Id switch
        {
            "Permit" => "For " + where + ", mail that the pack would otherwise hold or review will be permitted. Hard floors still apply; this cannot permit a hard-floor category. Revocation is an audit event, not a deletion.",
            "Observe" => "For " + where + ", mail is still scored and shown, but no automatic action is taken. The pack's hard floors still escalate.",
            "CaptureText" => "For " + where + ", content capture is allowed within the pack's declared mode. A second administrator is required, and the pack must already permit some capture.",
            "RaiseTolerance" => "For " + where + ", " + BindingCategory.Label.ToLowerInvariant() + " is set to " + BindingTargetLevel.Label + ". Raising tolerance cannot make the deployment less protective than the pack.",
            "LowerTolerance" => "For " + where + ", " + BindingCategory.Label.ToLowerInvariant() + " is set to " + BindingTargetLevel.Label + ". This is recorded as a lowering; a second administrator is required, and a hard floor cannot go below Strict.",
            _ => "Choose an action to see its consequence.",
        };
    }

    private string SuggestVersion()
    {
        var versions = Packs
            .Where(pack => string.Equals(pack.Id, "pack", StringComparison.Ordinal))
            .Select(pack => int.TryParse(pack.Version, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0)
            .DefaultIfEmpty(0)
            .Max();
        return (versions + 1).ToString(CultureInfo.InvariantCulture);
    }

    private string BuildTechnicalJson()
    {
        var snapshot = new
        {
            status = _status,
            tolerances = Tolerances.Select(row => new { category = row.CategoryId, level = row.Level }),
            packs = Packs.Select(pack => new
            {
                id = pack.Id,
                version = pack.Version,
                state = pack.StateId,
                detail = pack.Detail,
                hash = pack.HashLabel,
                approver = pack.ApproverLabel,
            }),
            bindings = Bindings.Select(binding => new
            {
                id = binding.Id,
                state = binding.StateId,
                scope = binding.Scope,
                action = binding.Action,
                effect = binding.Effect,
                pack = binding.Pack,
                dates = binding.Dates,
                author = binding.Author,
                approver = binding.Approver,
                reason = binding.Reason,
            }),
        };

        return JsonSerializer.Serialize(snapshot, TechnicalJsonOptions);
    }

    private static string DescribeContext(PolicyPackContextRow? context)
    {
        if (context is null) return "Pack context is not reported.";

        return "Context: " + PolicyChoices.Label(PolicyChoices.Verticals, context.Vertical)
            + ", " + PolicyChoices.Label(PolicyChoices.Audiences, context.Audience)
            + ", " + (string.IsNullOrWhiteSpace(context.Jurisdiction) ? "jurisdiction not declared" : context.Jurisdiction) + ".";
    }

    private static string Describe(StyloMailApiException failure)
    {
        var detail = string.IsNullOrWhiteSpace(failure.Detail) ? failure.Code : failure.Detail;
        return failure.Failure switch
        {
            StyloMailApiFailure.ApiKeyNotConfigured =>
                "No API key is configured, so nothing was sent.",
            StyloMailApiFailure.Unreachable =>
                "The Host could not be reached. Check the address and that it is running.",
            StyloMailApiFailure.UnreadableResponse =>
                "The Host's answer could not be read by this build. The console and the Host may be different versions.",
            _ when string.Equals(failure.Code, "pack.second_approver_must_differ", StringComparison.Ordinal) =>
                "Choose a second administrator who is a different person from the approver.",
            _ when string.Equals(failure.Code, "pack.level_lowered_without_second_approver", StringComparison.Ordinal) =>
                "This version lowers a category, so it needs a second administrator before approval.",
            _ when string.Equals(failure.Code, "pack.capture_text_full_requires_second_approver", StringComparison.Ordinal) =>
                "Full-body capture needs a second administrator at approval.",
            _ when string.Equals(failure.Code, "pack.rule_set_not_live", StringComparison.Ordinal) =>
                "The rule-set version is not live on this Host, so activation was refused.",
            _ when string.Equals(failure.Code, "pack.effective_before_approval", StringComparison.Ordinal) =>
                "The effective date is before the approval. Set a future date on the draft.",
            _ => string.IsNullOrWhiteSpace(detail)
                ? "The Host refused the change."
                : detail,
        };
    }

    private static string Humanize(string id)
        => string.Join(' ', id.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

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

    private static DateTimeOffset LocalDate(DateTimeOffset value)
        => new(value.Date, value.Offset);

    private static string FormatDate(DateTimeOffset? value)
        => value is null ? "not set" : value.Value.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private static double ParseDeadlineHours(string? deadline)
    {
        if (TimeSpan.TryParse(deadline, CultureInfo.InvariantCulture, out var parsed))
        {
            return Math.Clamp(parsed.TotalHours, 1, 168);
        }

        return 24;
    }
}

public sealed record PolicyToleranceRow(
    string Category,
    string Level,
    string CategoryId = "",
    string Description = "",
    bool IsHardFloor = false,
    string FloorNote = "");

/// <summary>One category in the draft editor: the level, the plain description and the floor lock.</summary>
public sealed class PolicyToleranceEditorRow : ObservableObject
{
    private PolicyChoice _selectedLevel;

    public PolicyToleranceEditorRow(PolicyCategoryInfo category, string? selectedLevel)
    {
        Category = category;
        LevelOptions = category.IsHardFloor
            ? PolicyChoices.HardFloorLevels
            : [PolicyChoices.NotDeclared, .. PolicyChoices.Levels];

        _selectedLevel = LevelOptions.FirstOrDefault(choice => string.Equals(choice.Id, selectedLevel, StringComparison.Ordinal))
            ?? (category.IsHardFloor
                ? LevelOptions[0]
                : PolicyChoices.NotDeclared);
    }

    public PolicyCategoryInfo Category { get; }

    public string CategoryId => Category.Id;

    public string Label => Category.Label;

    public string Description => Category.Description;

    public bool IsHardFloor => Category.IsHardFloor;

    public string FloorNote => IsHardFloor ? PolicyCategoryCatalog.HardFloorNote : string.Empty;

    public IReadOnlyList<PolicyChoice> LevelOptions { get; }

    public PolicyChoice SelectedLevel
    {
        get => _selectedLevel;
        set => Set(ref _selectedLevel, value);
    }
}

public sealed class PolicyPackListItem
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public required string StateId { get; init; }

    public required string State { get; init; }

    public required string Title { get; init; }

    public required string Detail { get; init; }

    public required string HashLabel { get; init; }

    public required string ApproverLabel { get; init; }

    public required string DatesLabel { get; init; }

    public required string ContextLabel { get; init; }

    public required string CeilingLabel { get; init; }

    public required string CaptureLabel { get; init; }

    public string Reference => Id + "@" + Version;

    public bool CanEditDraft => StateId == "Draft";

    public bool CanApprove => StateId == "Draft";

    public bool CanActivate => StateId == "Approved";

    public bool CanWithdraw => StateId is "Draft" or "Approved";

    public bool CanDeactivate => StateId == "Effective";

    public bool CanManage => CanEditDraft || CanApprove || CanActivate || CanWithdraw || CanDeactivate;

    public static PolicyPackListItem From(PolicyPackSummaryRow pack)
    {
        var id = pack.Id ?? "unknown";
        var version = pack.Version ?? "-";
        var stateId = pack.State ?? string.Empty;
        var state = PackStateLabel(stateId);
        var jurisdiction = pack.Jurisdiction ?? "Jurisdiction not reported";
        var effective = pack.EffectiveFrom is { } effectiveFrom
            ? effectiveFrom.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture)
            : "not set";
        var review = pack.ReviewDue is { } reviewDue
            ? reviewDue.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture)
            : "not set";
        var hash = string.IsNullOrWhiteSpace(pack.ContentSha256) ? "not reported" : pack.ContentSha256;
        var approver = string.IsNullOrWhiteSpace(pack.Approver) ? "nobody yet" : pack.Approver;
        var second = string.IsNullOrWhiteSpace(pack.SecondApprover) ? "no second approver" : "second approver " + pack.SecondApprover;

        return new PolicyPackListItem
        {
            Id = id,
            Version = version,
            StateId = stateId,
            State = state,
            Title = "Pack " + id + " v" + version + " · " + state,
            Detail = jurisdiction + " · effective " + effective + " · review due " + review + ".",
            HashLabel = "Content hash " + hash + ".",
            ApproverLabel = "Approved by " + approver + ", " + second + ".",
            DatesLabel = "Effective " + effective + ", review due " + review + ".",
            ContextLabel = jurisdiction,
            CeilingLabel = state,
            CaptureLabel = "",
        };
    }

    public static string PackStateLabel(string? state) => state switch
    {
        "Draft" => "Draft",
        "Approved" => "Approved, not yet active",
        "Effective" => "In effect",
        "Superseded" => "Superseded",
        "Withdrawn" => "Withdrawn",
        null or "" => "State not reported",
        _ => state,
    };
}

public sealed class PolicyBindingItem
{
    public required string Id { get; init; }

    public required string StateId { get; init; }

    public required string State { get; init; }

    public required string Title { get; init; }

    public required string Detail { get; init; }

    public required string Scope { get; init; }

    public required string Action { get; init; }

    public required string Category { get; init; }

    public required string TargetLevel { get; init; }

    public required string Effect { get; init; }

    public required string Pack { get; init; }

    public required string Dates { get; init; }

    public required string Author { get; init; }

    public required string Approver { get; init; }

    public required string Reason { get; init; }

    public required string Precedence { get; init; }

    public bool CanRevoke => StateId is "Active" or "Pending";

    public static PolicyBindingItem From(PolicyBindingRow binding)
    {
        var scope = ScopeLabel(binding.ScopeKind) + ": " + (binding.ScopeValue ?? "not reported");
        var action = PolicyChoices.Label(PolicyChoices.BindingActions, binding.Action);
        var category = string.IsNullOrWhiteSpace(binding.CategoryId)
            ? "No category"
            : PolicyCategoryCatalog.LabelFor(binding.CategoryId);
        var target = string.IsNullOrWhiteSpace(binding.TargetLevel)
            ? "No level"
            : PolicyChoices.LevelLabel(binding.TargetLevel);
        var effect = binding.Action is "RaiseTolerance" or "LowerTolerance"
            ? category + " set to " + target
            : "No category level change";
        var pack = "Pack " + (binding.PackId ?? "not reported") + " v" + (binding.PackVersion ?? "-");
        var dates = "Starts " + Format(binding.StartsAt)
            + ", expires " + Format(binding.ExpiresAt)
            + ", review by " + Format(binding.ReviewBy) + ".";
        var stateId = binding.State ?? string.Empty;
        var state = BindingStateLabel(stateId);
        var author = string.IsNullOrWhiteSpace(binding.Author) ? "Author not reported" : "By " + binding.Author;
        var approver = string.IsNullOrWhiteSpace(binding.ApprovedBy)
            ? "No second approver"
            : "Second approver " + binding.ApprovedBy;
        var onExpiry = PolicyChoices.Label(PolicyChoices.OnExpiry, binding.OnExpiry);
        var reason = string.IsNullOrWhiteSpace(binding.Reason) ? "Reason not reported." : binding.Reason;

        return new PolicyBindingItem
        {
            Id = binding.Id ?? string.Empty,
            StateId = stateId,
            State = state,
            Title = scope + " · " + action,
            Detail = pack + " · " + dates + " · " + state + ".",
            Scope = scope,
            Action = action,
            Category = category,
            TargetLevel = target,
            Effect = effect,
            Pack = pack,
            Dates = dates,
            Author = author,
            Approver = approver,
            Reason = reason,
            Precedence = "On expiry: " + onExpiry + ". More protective targets win over narrower scopes.",
        };
    }

    private static string BindingStateLabel(string state) => state switch
    {
        "Active" => "Active",
        "Pending" => "Pending",
        "Expired" => "Expired",
        "Revoked" => "Revoked",
        null or "" => "State not reported",
        _ => state,
    };

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

    private static string Format(DateTimeOffset? value)
        => value is null ? "not set" : value.Value.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}
