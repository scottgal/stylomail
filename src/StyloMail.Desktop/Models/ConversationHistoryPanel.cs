using System.Collections.ObjectModel;
using System.Globalization;
using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Models;

/// <summary>The real Host identifiers needed to read assessed Slack history.</summary>
public sealed record ConversationHistoryScope(string WorkspaceId, string ChannelId, string? ThreadId);

/// <summary>
/// Observable state for the review-only assessed-history section of one open
/// decision. The Host returns original assessments; this panel does not derive
/// conversation aggregates.
/// </summary>
public sealed class ConversationHistoryPanel : ObservableObject
{
    private bool _isLoading;
    private bool _hasLoaded;
    private bool _hasMore;
    private string? _nextCursor;
    private string _statusLabel;
    private string? _skippedLabel;

    private ConversationHistoryPanel(
        ConversationHistoryScope? scope,
        string statusLabel)
    {
        Scope = scope;
        _statusLabel = statusLabel;
        Decisions.CollectionChanged += (_, _) =>
        {
            Raise(nameof(HasDecisions));
            Raise(nameof(ShowInitialLoadButton));
        };
    }

    public ConversationHistoryScope? Scope { get; }

    public string ScopeLabel => Scope is { } scope
        ? $"Slack · workspace {scope.WorkspaceId} · channel {scope.ChannelId}"
            + (string.IsNullOrWhiteSpace(scope.ThreadId) ? " · all threads" : $" · thread {scope.ThreadId}")
        : string.Empty;

    public bool IsSupported => Scope is not null;

    public string UnavailableLabel => Scope is not null
        ? string.Empty
        : "Cross-message history is unavailable: this decision has no supported Slack workspace/channel keys. Email is not grouped by sender or recipient.";

    public ObservableCollection<ConversationHistoryDecisionView> Decisions { get; } = [];

    public bool HasDecisions => Decisions.Count > 0;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!Set(ref _isLoading, value)) return;
            Raise(nameof(ShowInitialLoadButton));
            Raise(nameof(ShowLoadMoreButton));
        }
    }

    public bool ShowInitialLoadButton => IsSupported && !_isLoading && !_hasLoaded;

    public bool ShowLoadMoreButton => IsSupported && !_isLoading && _hasLoaded && _hasMore;

    public bool HasLoaded => _hasLoaded;

    public bool HasMore => _hasMore;

    public string StatusLabel
    {
        get => _statusLabel;
        private set => Set(ref _statusLabel, value);
    }

    public string? SkippedLabel
    {
        get => _skippedLabel;
        private set
        {
            if (!Set(ref _skippedLabel, value)) return;
            Raise(nameof(HasSkipped));
        }
    }

    public bool HasSkipped => SkippedLabel is not null;

    public string? NextCursor => _nextCursor;

    public static ConversationHistoryPanel For(DecisionResponse decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var channel = decision.Channel;
        if (channel.Kind != ChannelKind.Slack
            || string.IsNullOrWhiteSpace(channel.WorkspaceId)
            || string.IsNullOrWhiteSpace(channel.ChannelId))
        {
            return new ConversationHistoryPanel(
                scope: null,
                "No supported Slack conversation key is available for this decision.");
        }

        return new ConversationHistoryPanel(
            new ConversationHistoryScope(channel.WorkspaceId, channel.ChannelId, channel.ThreadId),
            "History is not loaded. Load the latest assessed records for this Slack conversation.");
    }

    public void BeginLoad(bool append)
    {
        if (!IsSupported || IsLoading) return;
        if (!append)
        {
            Decisions.Clear();
            _nextCursor = null;
            _hasMore = false;
            _hasLoaded = false;
            SkippedLabel = null;
        }

        IsLoading = true;
        StatusLabel = append
            ? "Loading older assessed records…"
            : "Loading assessed conversation history…";
    }

    public void ApplyPage(ConversationHistoryResponse response, bool append)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (Scope is not { } scope
            || !string.Equals(response.WorkspaceId, scope.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(response.ChannelId, scope.ChannelId, StringComparison.Ordinal)
            || !string.Equals(response.ThreadId, scope.ThreadId, StringComparison.Ordinal))
        {
            Fail("The Host returned history for a different conversation. No additional records were shown.", append);
            return;
        }

        if (response.HasMore && string.IsNullOrEmpty(response.NextCursor))
        {
            Fail("The Host reported more history without a continuation cursor. No page was appended.", append);
            return;
        }

        if (response.Decisions.Any(decision =>
                decision.Channel.Kind != ChannelKind.Slack
                || !string.Equals(decision.Channel.WorkspaceId, scope.WorkspaceId, StringComparison.Ordinal)
                || !string.Equals(decision.Channel.ChannelId, scope.ChannelId, StringComparison.Ordinal)
                || (scope.ThreadId is not null
                    && !string.Equals(decision.Channel.ThreadId, scope.ThreadId, StringComparison.Ordinal))))
        {
            Fail("The Host returned a decision without matching Slack conversation keys. No additional records were shown.", append);
            return;
        }

        if (!append) Decisions.Clear();
        foreach (var decision in response.Decisions)
        {
            Decisions.Add(ConversationHistoryDecisionView.From(decision));
        }

        _nextCursor = response.NextCursor;
        _hasMore = response.HasMore && !string.IsNullOrEmpty(response.NextCursor);
        _hasLoaded = true;
        IsLoading = false;
        StatusLabel = Decisions.Count == 0
            ? "No assessed records were returned for this Slack conversation."
            : $"Showing {Decisions.Count.ToString(CultureInfo.InvariantCulture)} assessed record(s) in this review.";
        SkippedLabel = response.SkippedCount > 0
            ? $"The Host skipped {response.SkippedCount.ToString(CultureInfo.InvariantCulture)} record(s) with invalid stored timestamps."
            : null;
        Raise(nameof(ShowInitialLoadButton));
        Raise(nameof(ShowLoadMoreButton));
    }

    public void Fail(
        string message = "Conversation history could not be loaded. Retry the read.",
        bool preservePageState = false)
    {
        IsLoading = false;
        if (!preservePageState)
        {
            _hasLoaded = false;
            _nextCursor = null;
            _hasMore = false;
        }
        StatusLabel = message;
        Raise(nameof(ShowInitialLoadButton));
        Raise(nameof(ShowLoadMoreButton));
    }
}

/// <summary>A single original decision and its unaggregated windowed observations.</summary>
public sealed class ConversationHistoryDecisionView
{
    private ConversationHistoryDecisionView() { }

    public required string AssessmentId { get; init; }

    public required string InternalMessageId { get; init; }

    public required string AssessedAtLabel { get; init; }

    public required string ActionLabel { get; init; }

    public required IReadOnlyList<EvidenceView> TrendObservations { get; init; }

    public bool HasTrendObservations => TrendObservations.Count > 0;

    public string NoTrendObservationsLabel => HasTrendObservations
        ? string.Empty
        : "No windowed observations were returned with this assessment.";

    public static ConversationHistoryDecisionView From(DecisionResponse decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return new ConversationHistoryDecisionView
        {
            AssessmentId = decision.AssessmentId,
            InternalMessageId = decision.InternalMessageId,
            AssessedAtLabel = "Assessed at " + decision.AssessedAt.ToString(
                "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            ActionLabel = decision.Action.ToString(),
            TrendObservations = [.. decision.Evidence
                .Where(row => !string.IsNullOrWhiteSpace(row.Window))
                .Select(EvidenceView.From)],
        };
    }
}
