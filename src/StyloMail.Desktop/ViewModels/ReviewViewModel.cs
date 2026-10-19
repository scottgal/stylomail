using System.Collections.ObjectModel;
using System.Globalization;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.ViewModels;

/// <summary>
/// The human review queue: the messages whose delivery is withheld until a person decides.
/// </summary>
/// <remarks>
/// The review list carries the opening decision's reason, pack and rule, so a card can explain itself
/// without a second read. Held and stopped items are summarized and linked back to Mail: their
/// listing carries no decision summary, and inventing one here would be a second, weaker source.
/// </remarks>
public sealed class ReviewViewModel : ObservableObject
{
    private StyloMailApiClient? _client;
    private bool _isLoading;
    private bool _isApplying;
    private bool _loaded;
    private bool _failed;
    private string _statusLabel = "The review queue has not been read yet.";
    private string _lastUpdatedLabel = "Not read yet.";
    private long? _heldCount;
    private long? _stoppedCount;
    private ReviewActionRequest? _pendingAction;
    private string _pendingReason = string.Empty;
    private string? _lastResult;
    private bool _lastFailed;

    public ReviewViewModel(StyloMailApiClient? client = null) => _client = client;

    public event EventHandler<string>? NavigationRequested;

    public ObservableCollection<ReviewCardView> Reviews { get; } = [];

    public ObservableCollection<string> TechnicalLines { get; } = [];

    public bool HasReviews => Reviews.Count > 0;

    public bool ShowEmpty => _loaded && !_failed && !IsLoading && Reviews.Count == 0;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (Set(ref _isLoading, value)) Raise(nameof(RefreshLabel));
        }
    }

    public string RefreshLabel => IsLoading ? "Reading" : "Refresh";

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

    public string HeldCountLabel => FormatCount(_heldCount);

    public string StoppedCountLabel => FormatCount(_stoppedCount);

    public ReviewActionRequest? PendingAction
    {
        get => _pendingAction;
        private set
        {
            if (Set(ref _pendingAction, value))
            {
                Raise(nameof(HasPendingAction));
                Raise(nameof(CanConfirm));
            }
        }
    }

    public bool HasPendingAction => PendingAction is not null;

    public string PendingReason
    {
        get => _pendingReason;
        set
        {
            if (Set(ref _pendingReason, value)) Raise(nameof(CanConfirm));
        }
    }

    public bool CanConfirm => PendingAction is not null
        && !IsApplying
        && !string.IsNullOrWhiteSpace(PendingReason);

    public bool IsApplying
    {
        get => _isApplying;
        private set
        {
            if (Set(ref _isApplying, value)) Raise(nameof(CanConfirm));
        }
    }

    public string? LastResult
    {
        get => _lastResult;
        private set
        {
            if (Set(ref _lastResult, value)) Raise(nameof(HasLastResult));
        }
    }

    public bool HasLastResult => !string.IsNullOrWhiteSpace(LastResult);

    public bool LastFailed
    {
        get => _lastFailed;
        private set => Set(ref _lastFailed, value);
    }

    public void SetClient(StyloMailApiClient? client) => _client = client;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading) return;

        IsLoading = true;
        _failed = false;
        _loaded = false;
        TechnicalLines.Clear();

        try
        {
            if (_client is null)
            {
                Reviews.Clear();
                StatusLabel = "The review queue is unavailable until the console is connected to a Host.";
                _loaded = true;
                return;
            }

            try
            {
                var reviews = await _client.GetOpenReviewsAsync(limit: 100, cancellationToken: cancellationToken).ConfigureAwait(true);
                Reviews.Clear();

                foreach (var item in reviews.Items)
                {
                    var card = ReviewCardView.From(item);
                    Reviews.Add(card);
                    TechnicalLines.Add(card.TechnicalDetail);
                }

                StatusLabel = Reviews.Count == 0
                    ? "No messages are awaiting human review."
                    : $"{Reviews.Count:N0} message{(Reviews.Count == 1 ? " is" : "s are")} awaiting human review.";
            }
            catch (StyloMailApiException failure)
            {
                _failed = true;
                Reviews.Clear();
                StatusLabel = failure.Code == "review_state_unavailable"
                    ? "This Host does not expose the review queue."
                    : "The review queue could not be read. " + (failure.Detail ?? failure.Code ?? failure.Message);
            }

            try
            {
                var queues = await _client.GetQueueSummaryAsync(cancellationToken).ConfigureAwait(true);
                _heldCount = queues.Counts.Held;
                _stoppedCount = queues.Counts.Quarantined;
                Raise(nameof(HeldCountLabel));
                Raise(nameof(StoppedCountLabel));
                TechnicalLines.Add(
                    $"Queues: availability {queues.Availability}; held {FormatCount(queues.Counts.Held)}; "
                    + $"stopped {FormatCount(queues.Counts.Quarantined)}; basis {queues.Basis ?? "not reported"}.");
            }
            catch (Exception ex) when (ex is StyloMailApiException or HttpRequestException)
            {
                _heldCount = null;
                _stoppedCount = null;
                Raise(nameof(HeldCountLabel));
                Raise(nameof(StoppedCountLabel));
            }

            TechnicalLines.Add("Routes read: GET /v1/policy/reviews?state=open; GET /v1/queues/summary.");
            LastUpdatedLabel = "Updated " + DateTimeOffset.UtcNow.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            _loaded = true;
        }
        finally
        {
            IsLoading = false;
            Raise(nameof(HasReviews));
            Raise(nameof(ShowEmpty));
        }
    }

    public void AskRelease(ReviewCardView card)
        => Ask(new ReviewActionRequest(
            ReviewActionKind.Release,
            card.QueueId,
            "Release this message",
            "The message will be delivered. The decision and your reason are recorded in the review audit.",
            "Release to delivery"));

    public void AskKeepStopped(ReviewCardView card)
        => Ask(new ReviewActionRequest(
            ReviewActionKind.KeepStopped,
            card.QueueId,
            "Keep this message stopped",
            "The message remains stopped before delivery and is not sent. Your reason is recorded.",
            "Keep stopped"));

    public void AskReject(ReviewCardView card)
        => Ask(new ReviewActionRequest(
            ReviewActionKind.Reject,
            card.QueueId,
            "Reject this message permanently",
            "Responsibility is declined permanently and the message is not delivered. This needs the administer privilege.",
            "Reject permanently"));

    public void CancelPending()
    {
        PendingAction = null;
        PendingReason = string.Empty;
    }

    public void DismissResult() => LastResult = null;

    public void RequestNavigation(string destination)
    {
        if (!string.IsNullOrEmpty(destination))
        {
            NavigationRequested?.Invoke(this, destination);
        }
    }

    public async Task ConfirmAsync(CancellationToken cancellationToken = default)
    {
        if (!CanConfirm || _client is null || PendingAction is not { } action) return;

        IsApplying = true;
        LastFailed = false;

        try
        {
            switch (action.Kind)
            {
                case ReviewActionKind.Release:
                    await _client.ReleaseReviewAsync(action.QueueId, PendingReason, cancellationToken).ConfigureAwait(true);
                    break;
                case ReviewActionKind.KeepStopped:
                    await _client.QuarantineReviewAsync(action.QueueId, PendingReason, cancellationToken).ConfigureAwait(true);
                    break;
                case ReviewActionKind.Reject:
                    await _client.RejectReviewAsync(action.QueueId, PendingReason, cancellationToken).ConfigureAwait(true);
                    break;
            }

            LastResult = "Applied. The review audit records the actor and your reason.";
            PendingAction = null;
            PendingReason = string.Empty;
            await LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (StyloMailApiException failure)
        {
            LastFailed = true;
            LastResult = "Could not apply the review decision. " + (failure.Detail ?? failure.Code ?? failure.Message);
            PendingAction = null;
            PendingReason = string.Empty;
        }
        catch (HttpRequestException)
        {
            LastFailed = true;
            LastResult = "Could not apply the review decision. The Host could not be reached.";
            PendingAction = null;
            PendingReason = string.Empty;
        }
        finally
        {
            IsApplying = false;
        }
    }

    private void Ask(ReviewActionRequest request)
    {
        PendingAction = request;
        PendingReason = string.Empty;
        LastResult = null;
        LastFailed = false;
    }

    private static string FormatCount(long? value)
        => value is { } count ? count.ToString("N0", CultureInfo.InvariantCulture) : "not counted";
}

public enum ReviewActionKind
{
    Release,
    KeepStopped,
    Reject,
}

public sealed record ReviewActionRequest(
    ReviewActionKind Kind,
    string QueueId,
    string Title,
    string Consequence,
    string ConfirmLabel);

public sealed record ReviewCardView
{
    public required string QueueId { get; init; }
    public required string Title { get; init; }
    public required string RecipientsLabel { get; init; }
    public required string Reason { get; init; }
    public required string PolicyLine { get; init; }
    public required string WaitingLabel { get; init; }
    public required string OverdueLabel { get; init; }
    public required bool HasOverdue { get; init; }
    public required string TechnicalDetail { get; init; }

    public static ReviewCardView From(ReviewQueueRow item)
    {
        var recipients = item.Recipients.Select(recipient => recipient.Recipient).ToArray();
        var title = recipients.Length == 0
            ? "Review for message " + Shorten(item.InternalMessageId)
            : "Review for " + string.Join(", ", recipients);

        var opening = item.Opening;
        var policy = item.Policy;

        var reason = opening?.Reason
            ?? policy?.DecidingReason
            ?? "No opening reason was recorded for this review.";

        var policyLine = DescribePolicy(item, opening, policy);

        var created = item.CreatedAt;
        var waiting = created is { } at
            ? "Waiting since " + at.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture) + "."
            : "Waiting time not recorded.";

        var overdueRecipient = item.Recipients.FirstOrDefault(recipient => recipient.Overdue);
        var overdue = overdueRecipient is not null
            ? "A recipient's review deadline has passed."
            : string.Empty;

        var technical = new List<string>
        {
            $"Review {item.QueueId}: message {item.InternalMessageId}; recipients {string.Join(", ", recipients.DefaultIfEmpty("none"))}; "
            + $"opening {opening?.AssessmentId ?? "none"}; source {opening?.Source ?? policy?.Source ?? "not reported"}; "
            + $"pack {opening?.PackId ?? policy?.PackId ?? "none"} v{opening?.PackVersion ?? policy?.PackVersion ?? "-"}; "
            + $"rule {opening?.RuleId ?? policy?.DecidingRuleId ?? "none"} v{opening?.RuleVersion ?? policy?.DecidingRuleVersion ?? 0}; "
            + $"reason code {opening?.ReasonCode ?? policy?.DecidingReasonCode ?? "none"}; "
            + $"policy record missing {item.PolicyRecordMissing}; legacy opening missing {item.LegacyReviewOpeningMissing}.",
        };

        foreach (var recipient in item.Recipients)
        {
            technical.Add(
                $"  recipient {recipient.Recipient}: state {recipient.State ?? "not reported"}; "
                + $"deadline {recipient.ReviewDeadline:u}; overdue {recipient.Overdue}; attempts {recipient.Attempts}; "
                + $"last error {recipient.LastError ?? "none"}.");
        }

        return new ReviewCardView
        {
            QueueId = item.QueueId,
            Title = title,
            RecipientsLabel = recipients.Length == 0 ? "No recipient was recorded." : string.Join(", ", recipients),
            Reason = reason,
            PolicyLine = policyLine,
            WaitingLabel = waiting,
            OverdueLabel = overdue,
            HasOverdue = overdueRecipient is not null,
            TechnicalDetail = string.Join(" ", technical),
        };
    }

    private static string DescribePolicy(ReviewQueueRow item, ReviewOpeningRow? opening, ReviewPolicyRow? policy)
    {
        if (item.LegacyReviewOpeningMissing)
        {
            return "This review predates the opening record; no pack or rule reference is available.";
        }

        if (item.PolicyRecordMissing)
        {
            return "The opening decision can no longer be read. The recorded reference is kept below.";
        }

        if (opening is null)
        {
            return policy is null
                ? "No policy pack was active when this review was opened."
                : "Opened by the policy engine; the opening reference is not available.";
        }

        if (string.Equals(opening.Source, "Fallback", StringComparison.OrdinalIgnoreCase))
        {
            return "Opened by a fallback rule with no pack provenance"
                + (string.IsNullOrWhiteSpace(opening.UnavailableReason) ? "." : ": " + opening.UnavailableReason + ".");
        }

        var pack = opening.PackId is null
            ? "a policy pack"
            : opening.PackId + (opening.PackVersion is null ? string.Empty : " v" + opening.PackVersion);

        var rule = opening.RuleId is null
            ? string.Empty
            : ", rule " + opening.RuleId + (opening.RuleVersion is null ? string.Empty : " v" + opening.RuleVersion);

        return "Opened by " + pack + rule + ".";
    }

    private static string Shorten(string value)
        => value.Length <= 12 ? value : value[..10] + "...";
}
