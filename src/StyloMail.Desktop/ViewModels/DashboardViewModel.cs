using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.ViewModels;

/// <summary>
/// The dashboard: what the service is doing, what needs a person, and what is missing.
/// </summary>
/// <remarks>
/// <para>
/// Every card loads independently and carries its own state. One failed request renders as one failed
/// card, never as a blank page or a zero, because an operator reading a dashboard has to be able to
/// tell "nothing happened" from "the console could not ask".
/// </para>
/// <para>
/// The model holds no Avalonia types, so the cards can be tested without a display. The view owns the
/// dispatcher; this type only fills properties.
/// </para>
/// </remarks>
public sealed class DashboardViewModel : ObservableObject
{
    private const int DecisionPageSize = 100;
    private const int DecisionPageCap = 5;
    private static readonly TimeSpan AssessmentBucket = TimeSpan.FromHours(2);
    private static readonly TimeSpan ConnectionBucket = TimeSpan.FromMinutes(30);

    private readonly Func<DateTimeOffset> _clock;
    private readonly HashSet<string> _dismissedWarnings = [];

    private StyloMailApiClient? _client;
    private ProxyConversationClient? _conversations;

    private bool _isLoading;
    private string _lastUpdatedLabel = "Not read yet";

    private string _serviceState = "unknown";
    private string _serviceHeadline = "Checking the mail service.";
    private string _serviceDetail = "The console has not read the Host yet.";
    private string _serviceUpdatedLabel = string.Empty;
    private string _serviceAcceptingLabel = "Accepting mail: checking.";
    private string _serviceFeedLabel = "Live feed: checking.";

    private string _flowState = "loading";
    private TimeChart? _flowChart;
    private string _flowNote = "Reading the mail flow summary.";
    private string _flowTotalsLabel = string.Empty;
    private string _flowWindowLabel = string.Empty;
    private string _flowBasisLabel = string.Empty;
    private string _flowAsOfLabel = string.Empty;

    private string _assessmentsState = "loading";
    private TimeChart? _assessmentsChart;
    private CategoryChart? _outcomeChart;
    private string _assessmentsNote = "Reading the decision ledger.";

    private string _connectionsState = "loading";
    private TimeChart? _connectionsChart;
    private string _connectionsNote = "Reading client connection outcomes.";
    private long _connectionFailureTotal;

    private string _queueState = "loading";
    private string _queueCoverageLabel = "Reading queue counts.";
    private string _queueAsOfLabel = string.Empty;
    private string _queueRelationLabel = string.Empty;

    private string _storageState = "loading";
    private string _storageHeadline = "Reading storage.";
    private string _storageDetail = "The Host has not answered yet.";
    private string _storageCoverageLabel = string.Empty;

    private string _peopleState = "loading";
    private string _peopleNote = "Reading sending accounts.";

    private string _conversationsState = "loading";
    private string _conversationsNote = "Reading recent conversations.";
    private string _conversationsWindowLabel = string.Empty;

    private int _unavailableCards;
    private long? _reviewCount;

    public DashboardViewModel(
        StyloMailApiClient? client = null,
        ProxyConversationClient? conversations = null,
        Func<DateTimeOffset>? clock = null)
    {
        _client = client;
        _conversations = conversations;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        QueueCounts.CollectionChanged += (_, _) => Raise(nameof(HasQueueCounts));
        SendingAccounts.CollectionChanged += (_, _) => Raise(nameof(HasSendingAccounts));
        Conversations.CollectionChanged += (_, _) => Raise(nameof(HasConversations));
    }

    /// <summary>Raised when a warning's action asks the window to open another destination.</summary>
    public event EventHandler<string>? NavigationRequested;

    public ObservableCollection<DashboardWarning> Warnings { get; } = [];

    public ObservableCollection<QueueCountRow> QueueCounts { get; } = [];

    public ObservableCollection<SenderAttentionRow> SendingAccounts { get; } = [];

    public ObservableCollection<DashboardConversationRow> Conversations { get; } = [];

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (Set(ref _isLoading, value)) Raise(nameof(RefreshLabel));
        }
    }

    public string RefreshLabel => IsLoading ? "Refreshing" : "Refresh";

    public string LastUpdatedLabel
    {
        get => _lastUpdatedLabel;
        private set => Set(ref _lastUpdatedLabel, value);
    }

    public string ServiceState
    {
        get => _serviceState;
        private set
        {
            if (Set(ref _serviceState, value)) Raise(nameof(IsServiceDegraded));
        }
    }

    public string ServiceHeadline
    {
        get => _serviceHeadline;
        private set => Set(ref _serviceHeadline, value);
    }

    public string ServiceDetail
    {
        get => _serviceDetail;
        private set => Set(ref _serviceDetail, value);
    }

    public string ServiceUpdatedLabel
    {
        get => _serviceUpdatedLabel;
        private set => Set(ref _serviceUpdatedLabel, value);
    }

    /// <summary>The specific fact the Service card carries, instead of repeating the page headline.</summary>
    public string ServiceAcceptingLabel
    {
        get => _serviceAcceptingLabel;
        private set => Set(ref _serviceAcceptingLabel, value);
    }

    /// <summary>The live-feed state, supplied by the window because the feed belongs to it.</summary>
    public string ServiceFeedLabel
    {
        get => _serviceFeedLabel;
        private set => Set(ref _serviceFeedLabel, value);
    }

    /// <summary>Whether the Service card should also show the failure detail.</summary>
    public bool IsServiceDegraded => ServiceState is not "good";

    public string FlowState
    {
        get => _flowState;
        private set => Set(ref _flowState, value);
    }

    public TimeChart? FlowChart
    {
        get => _flowChart;
        private set
        {
            if (Set(ref _flowChart, value)) Raise(nameof(HasFlowChart));
        }
    }

    public bool HasFlowChart => FlowChart is not null;

    public string FlowNote
    {
        get => _flowNote;
        private set => Set(ref _flowNote, value);
    }

    public string FlowTotalsLabel
    {
        get => _flowTotalsLabel;
        private set => Set(ref _flowTotalsLabel, value);
    }

    public string FlowWindowLabel
    {
        get => _flowWindowLabel;
        private set => Set(ref _flowWindowLabel, value);
    }

    /// <summary>
    /// The basis sentence. The flow counts messages accepted in the window and shows their current
    /// disposition, so it is not a delivery timeline and the card says so.
    /// </summary>
    public string FlowBasisLabel
    {
        get => _flowBasisLabel;
        private set => Set(ref _flowBasisLabel, value);
    }

    public string FlowAsOfLabel
    {
        get => _flowAsOfLabel;
        private set => Set(ref _flowAsOfLabel, value);
    }

    public string AssessmentsState
    {
        get => _assessmentsState;
        private set => Set(ref _assessmentsState, value);
    }

    public TimeChart? AssessmentsChart
    {
        get => _assessmentsChart;
        private set
        {
            if (Set(ref _assessmentsChart, value)) Raise(nameof(HasAssessmentsChart));
        }
    }

    public bool HasAssessmentsChart => AssessmentsChart is not null;

    public CategoryChart? OutcomeChart
    {
        get => _outcomeChart;
        private set
        {
            if (Set(ref _outcomeChart, value)) Raise(nameof(HasOutcomeChart));
        }
    }

    public bool HasOutcomeChart => OutcomeChart is not null;

    public string AssessmentsNote
    {
        get => _assessmentsNote;
        private set => Set(ref _assessmentsNote, value);
    }

    public string ConnectionsState
    {
        get => _connectionsState;
        private set => Set(ref _connectionsState, value);
    }

    public TimeChart? ConnectionsChart
    {
        get => _connectionsChart;
        private set
        {
            if (Set(ref _connectionsChart, value)) Raise(nameof(HasConnectionsChart));
        }
    }

    public bool HasConnectionsChart => ConnectionsChart is not null;

    public string ConnectionsNote
    {
        get => _connectionsNote;
        private set => Set(ref _connectionsNote, value);
    }

    public string QueueState
    {
        get => _queueState;
        private set => Set(ref _queueState, value);
    }

    public string QueueCoverageLabel
    {
        get => _queueCoverageLabel;
        private set => Set(ref _queueCoverageLabel, value);
    }

    public string QueueAsOfLabel
    {
        get => _queueAsOfLabel;
        private set => Set(ref _queueAsOfLabel, value);
    }

    /// <summary>
    /// Says whether the rows overlap. <c>awaitingDecision</c> contains the other three, so a reader
    /// must not add them up or stack them.
    /// </summary>
    public string QueueRelationLabel
    {
        get => _queueRelationLabel;
        private set => Set(ref _queueRelationLabel, value);
    }

    public string StorageState
    {
        get => _storageState;
        private set => Set(ref _storageState, value);
    }

    public string StorageHeadline
    {
        get => _storageHeadline;
        private set => Set(ref _storageHeadline, value);
    }

    public string StorageDetail
    {
        get => _storageDetail;
        private set => Set(ref _storageDetail, value);
    }

    public string StorageCoverageLabel
    {
        get => _storageCoverageLabel;
        private set => Set(ref _storageCoverageLabel, value);
    }

    public string PeopleState
    {
        get => _peopleState;
        private set => Set(ref _peopleState, value);
    }

    public string PeopleNote
    {
        get => _peopleNote;
        private set => Set(ref _peopleNote, value);
    }

    public string ConversationsState
    {
        get => _conversationsState;
        private set => Set(ref _conversationsState, value);
    }

    public string ConversationsNote
    {
        get => _conversationsNote;
        private set => Set(ref _conversationsNote, value);
    }

    public string ConversationsWindowLabel
    {
        get => _conversationsWindowLabel;
        private set => Set(ref _conversationsWindowLabel, value);
    }

    public bool HasWarnings => Warnings.Count > 0;

    public bool HasQueueCounts => QueueCounts.Count > 0;

    public bool HasSendingAccounts => SendingAccounts.Count > 0;

    public bool HasConversations => Conversations.Count > 0;

    public string WarningSummary => Warnings.Count == 0
        ? "Nothing needs your attention."
        : $"{Warnings.Count} item{(Warnings.Count == 1 ? string.Empty : "s")} need your attention.";

    public bool HasCriticalWarnings => Warnings.Any(warning => warning.IsCritical);

    /// <summary>Replaces the clients after a reconnect, so the next refresh reads the new Host.</summary>
    public void SetClients(StyloMailApiClient? client, ProxyConversationClient? conversations)
    {
        _client = client;
        _conversations = conversations;
    }

    /// <summary>
    /// Takes the window's live-feed headline for the Service card.
    /// </summary>
    /// <remarks>
    /// The feed belongs to the window: it owns the subscription, the retry ticket and the state that
    /// says the screen may be stale. The dashboard renders the headline rather than opening a second
    /// subscription of its own.
    /// </remarks>
    public void SetFeedStatus(string headline)
    {
        if (string.IsNullOrWhiteSpace(headline))
        {
            ServiceFeedLabel = "Live feed: unknown.";
            return;
        }

        ServiceFeedLabel = string.Equals(headline, "Live", StringComparison.OrdinalIgnoreCase)
            ? "Live feed: connected."
            : "Live feed: " + headline;
    }

    /// <summary>Reads every card. A failure in one card does not stop the others.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading) return;

        IsLoading = true;
        _unavailableCards = 0;

        try
        {
            if (_client is null)
            {
                ShowNoHost();
                RebuildWarnings();
                return;
            }

            await Task.WhenAll(
                LoadServiceAsync(cancellationToken),
                LoadFlowAsync(cancellationToken),
                LoadAssessmentsAsync(cancellationToken),
                LoadConnectionsAsync(cancellationToken),
                LoadQueuesAsync(cancellationToken),
                LoadStorageAsync(cancellationToken),
                LoadSendersAsync(cancellationToken),
                LoadConversationsAsync(cancellationToken));

            LastUpdatedLabel = "Updated " + _clock().ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            RebuildWarnings();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Dismisses an informational warning for this session only.</summary>
    public void Dismiss(DashboardWarning warning)
    {
        ArgumentNullException.ThrowIfNull(warning);

        if (!warning.CanDismiss) return;

        _dismissedWarnings.Add(warning.Id);
        Warnings.Remove(warning);
        WarningsChanged();
    }

    /// <summary>Asks the window to open a destination named by a card button.</summary>
    public void RequestNavigation(string destination)
    {
        if (!string.IsNullOrEmpty(destination))
        {
            NavigationRequested?.Invoke(this, destination);
        }
    }

    /// <summary>Runs a warning's action, or dismisses it when it has none.</summary>
    public void ActivateWarning(DashboardWarning warning)
    {
        ArgumentNullException.ThrowIfNull(warning);

        if (warning.CanDismiss && string.IsNullOrEmpty(warning.ActionDestination))
        {
            Dismiss(warning);
            return;
        }

        if (!string.IsNullOrEmpty(warning.ActionDestination))
        {
            NavigationRequested?.Invoke(this, warning.ActionDestination);
        }
    }

    private void ShowNoHost()
    {
        ServiceState = "unknown";
        ServiceHeadline = "This console is not connected to a Host.";
        ServiceDetail = "Enter a Host address and key in Settings, then refresh.";
        ServiceUpdatedLabel = string.Empty;
        ServiceAcceptingLabel = "Accepting mail: unknown.";
        ServiceFeedLabel = "Live feed: not connected.";

        FlowState = "unavailable";
        FlowChart = null;
        FlowNote = "No Host is connected.";

        AssessmentsState = "unavailable";
        AssessmentsChart = null;
        OutcomeChart = null;
        AssessmentsNote = "No Host is connected.";

        ConnectionsState = "unavailable";
        ConnectionsChart = null;
        ConnectionsNote = "No Host is connected.";

        QueueState = "unavailable";
        QueueCounts.Clear();
        QueueCoverageLabel = "No Host is connected.";

        StorageState = "unavailable";
        StorageHeadline = "Storage is not available.";
        StorageDetail = "No Host is connected.";
        StorageCoverageLabel = string.Empty;

        PeopleState = "unavailable";
        SendingAccounts.Clear();
        PeopleNote = "No Host is connected.";

        ConversationsState = "unavailable";
        Conversations.Clear();
        ConversationsNote = "No Host is connected.";

        _unavailableCards = 6;
    }

    private async Task LoadServiceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var readiness = await _client!.GetReadinessAsync(cancellationToken);
            var failed = readiness.FailedChecks ?? [];

            if (readiness.Ready)
            {
                ServiceState = "good";
                ServiceHeadline = "Mail is flowing normally.";
                ServiceDetail = "The Host is accepting mail.";
                ServiceAcceptingLabel = "Accepting mail: yes.";
            }
            else
            {
                ServiceState = "critical";
                ServiceHeadline = "Mail is stopped.";
                ServiceDetail = failed.Count == 0
                    ? "The Host is not accepting mail, and did not name the checks that failed."
                    : "The Host is not accepting mail. Failed checks: " + string.Join(", ", failed) + ".";
                ServiceAcceptingLabel = "Accepting mail: no.";
            }

            ServiceUpdatedLabel = "Last checked " + _clock().ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is StyloMailApiException or HttpRequestException)
        {
            ServiceState = "critical";
            ServiceHeadline = "The console cannot reach the Host.";
            ServiceDetail = ex is StyloMailApiException api
                ? api.Failure == StyloMailApiFailure.ApiKeyNotConfigured
                    ? "No API key is configured, so nothing was read."
                    : api.Message
                : "No answer was received at the configured address.";
            ServiceAcceptingLabel = "Accepting mail: unknown.";
            ServiceUpdatedLabel = string.Empty;
            _unavailableCards++;
        }
    }

    private async Task LoadFlowAsync(CancellationToken cancellationToken)
    {
        try
        {
            var flow = await _client!.GetFlowAsync(cancellationToken);
            FlowWindowLabel = WindowLabel(flow.WindowStart, flow.WindowEnd);
            FlowBasisLabel = "Accepted in the last 24 hours, current state";
            FlowAsOfLabel = flow.AsOf is { } asOf
                ? "State as of " + asOf.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture) + "."
                : "The Host did not report the instant its state was read.";
            FlowTotalsLabel = DescribeTotals(flow.Totals, flow.RejectedBasis);

            // A complete window with no messages is an empty state, not three
            // flat zero lines. The zero line stays when any series has real
            // data: a held message or a refused decision is still something the
            // operator should see against the window.
            if (!HasAnyFlowValue(flow))
            {
                if (!HasAnyProducedFlowValue(flow))
                {
                    FlowState = "not_produced";
                    FlowChart = null;
                    FlowNote = string.IsNullOrWhiteSpace(flow.Coverage.Note)
                        ? "The Host did not produce a flow series for this window."
                        : flow.Coverage.Note!;
                }
                else
                {
                    FlowState = "empty";
                    FlowChart = null;
                    FlowNote = "No messages in this window.";
                }

                return;
            }

            var lines = new List<ChartLine>();
            var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var absence in flow.NotProduced) missing.Add(absence.Series);

            // The primary chart is the three series the operator asked for. The other disposition
            // series stay in the totals line and the queue card, which is where a reader compares
            // them; drawing seven overlapping lines would make the three that matter unreadable.
            AddFlowLine(lines, missing, "Inbound", flow.Buckets.Select(bucket => new ChartPoint(bucket.BucketStart, bucket.Inbound)));
            AddFlowLine(lines, missing, "Outbound", flow.Buckets.Select(bucket => new ChartPoint(bucket.BucketStart, bucket.Outbound)));
            AddFlowLine(lines, missing, "Delivered", flow.Buckets.Select(bucket => new ChartPoint(bucket.BucketStart, bucket.Delivered)));
            AddFlowLine(lines, missing, "Held", flow.Buckets.Select(bucket => new ChartPoint(bucket.BucketStart, bucket.Held)));

            if (lines.Count == 0)
            {
                FlowState = "not_produced";
                FlowChart = null;
                FlowNote = string.IsNullOrWhiteSpace(flow.Coverage.Note)
                    ? "The Host did not produce a flow series for this window."
                    : flow.Coverage.Note!;
                return;
            }

            FlowState = "ready";
            FlowChart = new TimeChart
            {
                Title = "Mail flow",
                Unit = "messages",
                WindowLabel = FlowWindowLabel,
                CoverageLabel = DescribeFlowCoverage(flow.Coverage),
                Lines = lines,
                Shape = ChartShape.Line,
                EmptyLabel = "No data for this window",
            };

            var note = DescribeFlowCoverage(flow.Coverage);

            if (flow.Coverage.DirectionNotRecoverable is > 0 and { } unrecoverable)
            {
                note += $" {unrecoverable:N0} rejected message{(unrecoverable == 1 ? " has" : "s have")} no recoverable direction and are counted once.";
            }

            FlowNote = missing.Count == 0
                ? note
                : note + " Not produced: " + string.Join(", ", missing.Order(StringComparer.OrdinalIgnoreCase)) + ".";
        }
        catch (StyloMailApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            FlowState = "pending";
            FlowChart = null;
            FlowWindowLabel = WindowLabel(_clock().AddHours(-24), _clock());
            FlowBasisLabel = "Accepted in the last 24 hours, current state";
            FlowAsOfLabel = string.Empty;
            FlowNote = "Waiting for the Host to provide mail flow counts.";
        }
        catch (Exception ex) when (ex is StyloMailApiException or HttpRequestException)
        {
            FlowState = "unavailable";
            FlowChart = null;
            FlowNote = "The mail flow summary could not be read. " + Reason(ex);
            _unavailableCards++;
        }
    }

    private async Task LoadAssessmentsAsync(CancellationToken cancellationToken)
    {
        var windowEnd = _clock();
        var windowStart = windowEnd.AddHours(-24);

        try
        {
            var rows = new List<DecisionSummaryResponse>();
            string? cursor = null;
            var complete = false;

            for (var page = 0; page < DecisionPageCap; page++)
            {
                var listing = await _client!
                    .GetDecisionsAsync(limit: DecisionPageSize, cursor: cursor, cancellationToken: cancellationToken)
                    ;

                foreach (var decision in listing.Decisions)
                {
                    if (decision.AssessedAt >= windowStart) rows.Add(decision);
                }

                var oldest = listing.Decisions.Count == 0
                    ? (DateTimeOffset?)null
                    : listing.Decisions.Min(decision => decision.AssessedAt);

                if (!listing.HasMore || listing.NextCursor is null || (oldest is { } value && value < windowStart))
                {
                    complete = true;
                    break;
                }

                cursor = listing.NextCursor;
            }

            var coverage = complete
                ? "Complete for this window."
                : $"Newest {rows.Count} assessments; this is not a complete count.";

            if (rows.Count == 0 && complete)
            {
                AssessmentsState = "empty";
                AssessmentsChart = null;
                OutcomeChart = null;
                AssessmentsNote = "No assessments were recorded in this window.";
                return;
            }

            var bucketStarts = Buckets(windowStart, windowEnd, AssessmentBucket);
            var oldestScanned = rows.Count == 0 ? (DateTimeOffset?)null : rows.Min(row => row.AssessedAt);

            var points = bucketStarts
                .Select(start =>
                {
                    var end = start + AssessmentBucket;
                    if (!complete && oldestScanned is { } oldest && end <= oldest)
                    {
                        return new ChartPoint(start, null);
                    }

                    return new ChartPoint(start, rows.Count(row => row.AssessedAt >= start && row.AssessedAt < end));
                })
                .ToArray();

            AssessmentsState = "ready";
            AssessmentsChart = new TimeChart
            {
                Title = "Assessments per 2 hours",
                Unit = "assessments",
                WindowLabel = WindowLabel(windowStart, windowEnd),
                CoverageLabel = coverage,
                Lines = [new ChartLine("Assessments", points)],
                Shape = ChartShape.Bar,
                EmptyLabel = "No data for this window",
            };

            var outcomeCounts = new (string Label, int Count)[]
            {
                ("Allowed", rows.Count(row => row.Action == MailAction.Allow)),
                ("Held", rows.Count(row => row.Action == MailAction.Hold)),
                ("Stopped before delivery", rows.Count(row => row.Action == MailAction.Quarantine)),
                ("Awaiting human review", rows.Count(row => row.Action == MailAction.Review)),
                ("Declined", rows.Count(row => row.Action == MailAction.Reject)),
                ("Deferred", rows.Count(row => row.Action == MailAction.Defer)),
            };

            var maximum = Math.Max(1, outcomeCounts.Max(item => item.Count));
            OutcomeChart = new CategoryChart
            {
                Title = "What the decisions did",
                WindowLabel = WindowLabel(windowStart, windowEnd),
                CoverageLabel = coverage,
                Bars = [.. outcomeCounts.Select(item => new CategoryBar(
                    item.Label,
                    item.Count,
                    item.Count.ToString("N0", CultureInfo.InvariantCulture),
                    (double)item.Count / maximum))],
            };

            AssessmentsNote = coverage;
        }
        catch (Exception ex) when (ex is StyloMailApiException or HttpRequestException)
        {
            AssessmentsState = "unavailable";
            AssessmentsChart = null;
            OutcomeChart = null;
            AssessmentsNote = "The decision ledger could not be read. " + Reason(ex);
            _unavailableCards++;
        }
    }

    private async Task LoadConnectionsAsync(CancellationToken cancellationToken)
    {
        var through = StyloMailApiClient.AlignToBucket(_clock());
        var from = StyloMailApiClient.AlignToBucket(_clock().AddHours(-24));

        try
        {
            var response = await _client!.GetConnectionOutcomesAsync(from, through, cancellationToken)
                ;

            if (response.Buckets.Count == 0)
            {
                ConnectionsState = "empty";
                ConnectionsChart = null;
                ConnectionsNote = "No client connection failures were recorded in this window.";
                _connectionFailureTotal = 0;
                return;
            }

            var starts = Buckets(from, through, ConnectionBucket);
            var rejected = new List<ChartPoint>();
            var timedOut = new List<ChartPoint>();

            foreach (var start in starts)
            {
                var end = start + ConnectionBucket;
                var inBucket = response.Buckets
                    .Where(bucket => bucket.BucketStart >= start && bucket.BucketStart < end)
                    .ToArray();

                rejected.Add(new ChartPoint(start, inBucket
                    .Where(bucket => string.Equals(bucket.Reason, "GlobalSessionLimitReached", StringComparison.OrdinalIgnoreCase))
                    .Sum(bucket => bucket.Count)));

                timedOut.Add(new ChartPoint(start, inBucket
                    .Where(bucket => string.Equals(bucket.Reason, "TlsHandshakeTimeout", StringComparison.OrdinalIgnoreCase))
                    .Sum(bucket => bucket.Count)));
            }

            _connectionFailureTotal = response.Buckets.Sum(bucket => bucket.Count);
            ConnectionsState = "ready";
            ConnectionsChart = new TimeChart
            {
                Title = "Client connections refused or timed out",
                Unit = "connections",
                WindowLabel = WindowLabel(from, through),
                CoverageLabel = "Counted from the Host's aligned five-minute buckets; empty buckets are measured zero.",
                Lines =
                [
                    new ChartLine("Admission rejected", rejected),
                    new ChartLine("TLS handshake timed out", timedOut),
                ],
                Shape = ChartShape.Line,
                EmptyLabel = "No data for this window",
            };
            ConnectionsNote = _connectionFailureTotal == 0
                ? "No client connection failures were recorded in this window."
                : $"{_connectionFailureTotal:N0} connection failures were recorded in this window.";
        }
        catch (Exception ex) when (ex is StyloMailApiException or HttpRequestException)
        {
            ConnectionsState = "unavailable";
            ConnectionsChart = null;
            ConnectionsNote = "Client connection outcomes could not be read. " + Reason(ex);
            _unavailableCards++;
        }
    }

    private async Task LoadQueuesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var summary = await _client!.GetQueueSummaryAsync(cancellationToken);

            if (string.Equals(summary.Availability, "not_produced", StringComparison.OrdinalIgnoreCase))
            {
                QueueState = "not_produced";
                QueueCounts.Clear();
                QueueCoverageLabel = string.IsNullOrWhiteSpace(summary.Coverage.Note)
                    ? "The Host did not produce queue counts."
                    : summary.Coverage.Note!;
                QueueAsOfLabel = "Counted at " + summary.AsOf.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
                QueueRelationLabel = string.Empty;
                _reviewCount = summary.Counts.Review;
                return;
            }

            QueueState = "ready";
            QueueRelationLabel = summary.Overlapping
                ? "In the delivery queue includes the other three states. The rows are not a sum."
                : "Each row is a separate count.";
            var largest = new[]
            {
                summary.Counts.AwaitingDecision,
                summary.Counts.Held,
                summary.Counts.Quarantined,
                summary.Counts.Review,
            }
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(0)
            .Max();

            double Width(long? value) => largest <= 0 || value is null ? 0 : 140.0 * value.Value / largest;

            QueueCounts.Clear();
            QueueCounts.Add(new QueueCountRow(
                "In the delivery queue",
                FormatCount(summary.Counts.AwaitingDecision),
                "Includes the three states below.",
                "delivery-queue",
                "queue-delivery")
            { BarWidth = Width(summary.Counts.AwaitingDecision) });
            QueueCounts.Add(new QueueCountRow(
                "Being re-checked before delivery",
                FormatCount(summary.Counts.Held),
                "Held for a scheduled re-check.",
                "held",
                "queue-held")
            { BarWidth = Width(summary.Counts.Held) });
            QueueCounts.Add(new QueueCountRow(
                "Stopped before delivery",
                FormatCount(summary.Counts.Quarantined),
                "Retained for review and not delivered.",
                "quarantined",
                "queue-quarantined")
            { BarWidth = Width(summary.Counts.Quarantined) });
            QueueCounts.Add(new QueueCountRow(
                "Awaiting human review",
                FormatCount(summary.Counts.Review),
                "Withheld for an authenticated human decision.",
                "review",
                "queue-review")
            { BarWidth = Width(summary.Counts.Review) });

            _reviewCount = summary.Counts.Review;
            QueueCoverageLabel = string.Equals(summary.Availability, "partial", StringComparison.OrdinalIgnoreCase)
                ? "Some queue counts were not produced. The rows shown are the counts the Host did produce."
                : summary.Coverage.Complete
                    ? "A complete count at the time shown."
                    : string.IsNullOrWhiteSpace(summary.Coverage.Note)
                        ? "This count is not complete."
                        : summary.Coverage.Note!;
            QueueAsOfLabel = "Counted at " + summary.AsOf.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        catch (StyloMailApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            await LoadQueueFallbackAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is StyloMailApiException or HttpRequestException)
        {
            QueueState = "unavailable";
            QueueCounts.Clear();
            QueueCoverageLabel = "Queue counts could not be read. " + Reason(ex);
            QueueAsOfLabel = string.Empty;
            QueueRelationLabel = string.Empty;
            _unavailableCards++;
        }
    }

    /// <summary>
    /// Counts the first page of each queue while the summary route is pending.
    /// </summary>
    /// <remarks>
    /// The first page is enough to answer "is anything waiting" and a plus sign is honest about the
    /// bound. The table is not allowed to print the page size as an exact total.
    /// </remarks>
    private async Task LoadQueueFallbackAsync(CancellationToken cancellationToken)
    {
        var awaiting = await CountMessagesAsync(MessageListState.AwaitingDecision, cancellationToken);
        var held = await CountMessagesAsync(MessageListState.Held, cancellationToken);
        var quarantined = await CountMessagesAsync(MessageListState.Quarantined, cancellationToken);
        var review = await CountReviewsAsync(cancellationToken);

        QueueState = "fallback";
        QueueRelationLabel = "In the delivery queue includes the other three states. The rows are not a sum.";
        var largest = new[] { awaiting.Count, held.Count, quarantined.Count, review.Count }
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(0)
            .Max();

        double Width(long? value) => largest <= 0 || value is null ? 0 : 140.0 * value.Value / largest;

        QueueCounts.Clear();
        QueueCounts.Add(new QueueCountRow("In the delivery queue", FormatBounded(awaiting), "Includes the three states below.", "delivery-queue", "queue-delivery") { BarWidth = Width(awaiting.Count) });
        QueueCounts.Add(new QueueCountRow("Being re-checked before delivery", FormatBounded(held), "Held for a scheduled re-check.", "held", "queue-held") { BarWidth = Width(held.Count) });
        QueueCounts.Add(new QueueCountRow("Stopped before delivery", FormatBounded(quarantined), "Retained for review and not delivered.", "quarantined", "queue-quarantined") { BarWidth = Width(quarantined.Count) });
        QueueCounts.Add(new QueueCountRow("Awaiting human review", FormatBounded(review), "Withheld for an authenticated human decision.", "review", "queue-review") { BarWidth = Width(review.Count) });

        _reviewCount = review.Count;
        QueueCoverageLabel = "First page of each queue. A plus means there are more.";
        QueueAsOfLabel = "Counted at " + _clock().ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private async Task<(long? Count, bool Capped)> CountMessagesAsync(
        MessageListState state,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = await _client!.GetMessagesAsync(state, limit: DecisionPageSize, cancellationToken: cancellationToken)
                ;
            return (page.Messages.Count, page.HasMore);
        }
        catch (Exception ex) when (ex is StyloMailApiException or HttpRequestException)
        {
            return (null, false);
        }
    }

    private async Task<(long? Count, bool Capped)> CountReviewsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var page = await _client!.GetOpenReviewsAsync(limit: DecisionPageSize, cancellationToken: cancellationToken)
                ;
            return (page.Items.Count, page.HasMore);
        }
        catch (Exception ex) when (ex is StyloMailApiException or HttpRequestException)
        {
            return (null, false);
        }
    }

    private async Task LoadStorageAsync(CancellationToken cancellationToken)
    {
        try
        {
            var storage = await _client!.GetStorageSummaryAsync(cancellationToken);

            if (string.Equals(storage.Availability, "not_produced", StringComparison.OrdinalIgnoreCase))
            {
                StorageState = "not_produced";
                StorageHeadline = "Storage is not measured.";
                StorageDetail = string.IsNullOrWhiteSpace(storage.Coverage.Note)
                    ? "The Host did not produce a storage measurement."
                    : storage.Coverage.Note!;
                StorageCoverageLabel = string.Empty;
                return;
            }

            StorageState = string.Equals(storage.Availability, "partial", StringComparison.OrdinalIgnoreCase)
                ? "partial"
                : "ready";
            var measured = new List<string>();

            if (storage.DatabaseBytes is { } database) measured.Add("Database " + FormatBytes(database));
            if (storage.WriteAheadLogBytes is { } wal) measured.Add("Write-ahead log " + FormatBytes(wal));
            if (storage.PayloadBytes is { } payload) measured.Add("Payloads " + FormatBytes(payload));
            if (storage.SpoolBytes is { } spool)
            {
                // A truncated walk is a lower bound. Saying "at least" is the
                // difference between a measurement and a floor.
                var lowerBound = storage.SpoolTruncated == true ? "at least " : string.Empty;

                measured.Add(storage.SpoolFileCount is { } files
                    ? $"Spool {lowerBound}{FormatBytes(spool)} in {lowerBound}{files:N0} file{(files == 1 ? string.Empty : "s")}"
                    : "Spool " + lowerBound + FormatBytes(spool));
            }

            StorageHeadline = measured.Count == 0
                ? "Storage is not measured."
                : "Stored: " + string.Join(", ", measured) + ".";

            var sentences = new List<string>();

            if (storage.CapacityBytes is { } capacity && storage.DatabaseBytes is { } used && capacity > 0)
            {
                sentences.Add($"{(double)used / capacity:P1} of the disk is used by the database.");
            }

            if (storage.VolumeFreeBytes is { } free)
            {
                sentences.Add(FormatBytes(free) + " free on the disk.");
            }

            var readableTables = storage.RowCounts.Where(row => row.Count.HasValue).ToArray();
            var unreadableTables = storage.RowCounts.Count(row => !row.Count.HasValue);

            if (readableTables.Length > 0)
            {
                var stored = readableTables.Sum(row => row.Count!.Value);
                var capped = readableTables.Any(row => row.Truncated);
                sentences.Add((capped ? "At least " : string.Empty)
                    + stored.ToString("N0", CultureInfo.InvariantCulture)
                    + " stored records across "
                    + readableTables.Length.ToString("N0", CultureInfo.InvariantCulture)
                    + " tables.");
            }

            if (unreadableTables > 0)
            {
                // A table that could not be read is not a table with no rows.
                sentences.Add(unreadableTables.ToString("N0", CultureInfo.InvariantCulture)
                    + " table" + (unreadableTables == 1 ? " could" : "s could")
                    + " not be read, so " + (unreadableTables == 1 ? "its" : "their")
                    + " row count is not measured.");
            }

            if (string.Equals(storage.Availability, "partial", StringComparison.OrdinalIgnoreCase))
            {
                sentences.Add("Some storage measures were not produced.");
            }

            if (storage.RetentionHorizon is { } horizon)
            {
                sentences.Add("Mail is retained until " + horizon.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture) + ".");
            }
            else if (!string.IsNullOrWhiteSpace(storage.RetentionReason))
            {
                sentences.Add("The Host did not record a retention horizon.");
            }

            if (sentences.Count == 0)
            {
                sentences.Add("The Host did not publish a capacity, free space, row count or retention horizon.");
            }

            StorageDetail = string.Join(" ", sentences);
            StorageCoverageLabel = storage.Coverage.Measured.Count == 0
                ? "Measured at " + storage.AsOf.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + "."
                : "Measured at " + storage.AsOf.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                    + ". " + string.Join(", ", storage.Coverage.Measured) + ".";
        }
        catch (StyloMailApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            StorageState = "pending";
            StorageHeadline = "Waiting for the Host to provide a storage summary.";
            StorageDetail = "The console does not infer storage from cumulative counters.";
            StorageCoverageLabel = string.Empty;
        }
        catch (Exception ex) when (ex is StyloMailApiException or HttpRequestException)
        {
            StorageState = "unavailable";
            StorageHeadline = "Storage could not be read.";
            StorageDetail = Reason(ex);
            StorageCoverageLabel = string.Empty;
            _unavailableCards++;
        }
    }

    private async Task LoadSendersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var listing = await _client!.GetSendersAsync(cancellationToken);
            SendingAccounts.Clear();

            var paused = listing.Senders.Where(sender => sender.Control.Paused).ToArray();
            var ordered = paused
                .Concat(listing.Senders.Where(sender => !sender.Control.Paused))
                .Take(4)
                .ToArray();

            foreach (var sender in ordered)
            {
                var label = string.IsNullOrWhiteSpace(sender.Label) ? sender.PrincipalId : sender.Label!;
                var state = sender.Control.Paused ? "Paused" : "Running";
                var detail = sender.Control.Paused
                    ? string.IsNullOrWhiteSpace(sender.Control.Reason)
                        ? "Paused. No reason was recorded."
                        : "Paused: " + sender.Control.Reason
                    : sender.Control.ResumedAt is not null
                        ? "Resumed by " + (sender.Control.ResumedBy ?? "an operator") + "."
                        : "Active.";

                SendingAccounts.Add(new SenderAttentionRow(
                    label,
                    state,
                    detail,
                    sender.PrincipalId,
                    "sender-" + sender.PrincipalId));
            }

            PeopleState = "ready";
            PeopleNote = paused.Length == 0
                ? $"{listing.Senders.Count} sending account{(listing.Senders.Count == 1 ? string.Empty : "s")}, none paused."
                : $"{listing.Senders.Count} sending accounts, {paused.Length} paused.";
        }
        catch (Exception ex) when (ex is StyloMailApiException or HttpRequestException)
        {
            PeopleState = "unavailable";
            SendingAccounts.Clear();
            PeopleNote = "Sending accounts could not be read. " + Reason(ex);
            _unavailableCards++;
        }
    }

    private async Task LoadConversationsAsync(CancellationToken cancellationToken)
    {
        if (_conversations is null)
        {
            ConversationsState = "unavailable";
            Conversations.Clear();
            ConversationsNote = "The conversation read is unavailable until the console is connected to a Host.";
            return;
        }

        try
        {
            var listing = await _conversations.GetConversationsAsync(limit: 5, cancellationToken: cancellationToken)
                ;

            ConversationsWindowLabel = WindowLabel(listing.WindowStart, listing.WindowEnd);
            Conversations.Clear();

            if (string.Equals(listing.Availability, "not_produced", StringComparison.OrdinalIgnoreCase))
            {
                ConversationsState = "not_produced";
                ConversationsNote = "This Host does not record conversation threads yet.";
                return;
            }

            foreach (var conversation in listing.Conversations.Take(5))
            {
                var participants = conversation.ParticipantTokens.Count;
                var depth = conversation.ReferenceDepth is { } value ? value.ToString(CultureInfo.InvariantCulture) : "not recorded";
                Conversations.Add(new DashboardConversationRow(
                    Shorten(conversation.ThreadToken),
                    Relative(conversation.LastActivityAt),
                    $"{participants} participant{(participants == 1 ? string.Empty : "s")}, depth {depth}.",
                    "conversation-" + conversation.ThreadToken));
            }

            ConversationsState = Conversations.Count == 0 ? "empty" : "ready";
            ConversationsNote = Conversations.Count == 0
                ? "No conversations were active in the last 24 hours."
                : $"{Conversations.Count} recent{(listing.HasMore ? ", more available" : string.Empty)}.";
        }
        catch (Exception ex) when (ex is StyloMailApiException or HttpRequestException)
        {
            ConversationsState = "unavailable";
            Conversations.Clear();
            ConversationsNote = "Recent conversations could not be read. " + Reason(ex);
            _unavailableCards++;
        }
    }

    private void RebuildWarnings()
    {
        Warnings.Clear();

        if (ServiceState == "critical")
        {
            Add(new DashboardWarning(
                "service-critical",
                "critical",
                ServiceHeadline,
                "Mail service",
                ServiceDetail,
                "Open Host detail",
                "Host"));
        }

        if (_unavailableCards > 0)
        {
            Add(new DashboardWarning(
                "cards-unavailable",
                "warning",
                $"{_unavailableCards} dashboard section{(_unavailableCards == 1 ? string.Empty : "s")} could not be read.",
                "Host",
                "Press Refresh. If the refusal continues, the status bar carries the reason.",
                "Refresh",
                "Refresh"));
        }

        var pending = new List<string>();
        if (FlowState == "pending") pending.Add("mail flow");
        if (QueueState == "fallback") pending.Add("queue counts");
        if (StorageState == "pending") pending.Add("storage");

        if (pending.Count > 0)
        {
            Add(new DashboardWarning(
                "summary-pending",
                "info",
                $"{pending.Count} dashboard summar{(pending.Count == 1 ? "y is" : "ies are")} still being prepared.",
                "Host",
                "No action is needed. The console will fill the cards when the Host answers: "
                    + string.Join(", ", pending) + ".",
                "Dismiss",
                string.Empty,
                canDismiss: true));
        }

        if (PeopleState == "ready" && SendingAccounts.Any(row => row.StateLabel == "Paused"))
        {
            var paused = SendingAccounts.Where(row => row.StateLabel == "Paused").Select(row => row.Label).ToArray();
            Add(new DashboardWarning(
                "senders-paused",
                "info",
                $"{paused.Length} sending account{(paused.Length == 1 ? " is" : "s are")} paused.",
                "Sending accounts",
                "Open People to see the account and the recorded reason: " + string.Join(", ", paused) + ".",
                "Open People",
                "Recipients",
                canDismiss: true));
        }

        if (_reviewCount is > 0)
        {
            Add(new DashboardWarning(
                "review-waiting",
                "warning",
                $"{_reviewCount:N0} message{(_reviewCount == 1 ? " is" : "s are")} awaiting human review.",
                "Review",
                "Open Review to release, keep stopped or reject them.",
                "Open Review",
                "Decisions"));
        }

        if (_connectionFailureTotal > 0)
        {
            Add(new DashboardWarning(
                "connections-refused",
                "warning",
                $"{_connectionFailureTotal:N0} client connection{(_connectionFailureTotal == 1 ? " was" : "s were")} refused or timed out in the last 24 hours.",
                "Access proxy",
                "Open the Host detail to inspect the listeners and the connection detail.",
                "Open Host detail",
                "Host"));
        }

        WarningsChanged();
    }

    private void Add(DashboardWarning warning)
    {
        if (_dismissedWarnings.Contains(warning.Id)) return;
        Warnings.Add(warning);
    }

    private void WarningsChanged()
    {
        Raise(nameof(HasWarnings));
        Raise(nameof(WarningSummary));
        Raise(nameof(HasCriticalWarnings));
    }

    private static bool HasAnyFlowValue(FlowResponse flow)
    {
        var totals = flow.Totals;

        if (totals.Inbound > 0 || totals.Outbound > 0 || totals.Delivered > 0
            || totals.Held > 0 || totals.Review > 0 || totals.Quarantined > 0
            || totals.Other > 0 || totals.Rejected > 0)
        {
            return true;
        }

        return flow.Buckets.Any(bucket =>
            bucket.Inbound > 0 || bucket.Outbound > 0 || bucket.Delivered > 0
            || bucket.Held > 0 || bucket.Review > 0 || bucket.Quarantined > 0
            || bucket.Other > 0 || bucket.Rejected > 0);
    }

    private static bool HasAnyProducedFlowValue(FlowResponse flow)
    {
        var totals = flow.Totals;

        if (totals.Inbound.HasValue || totals.Outbound.HasValue || totals.Delivered.HasValue
            || totals.Held.HasValue || totals.Review.HasValue || totals.Quarantined.HasValue
            || totals.Other.HasValue || totals.Rejected.HasValue)
        {
            return true;
        }

        return flow.Buckets.Any(bucket =>
            bucket.Inbound.HasValue || bucket.Outbound.HasValue || bucket.Delivered.HasValue
            || bucket.Held.HasValue || bucket.Review.HasValue || bucket.Quarantined.HasValue
            || bucket.Other.HasValue || bucket.Rejected.HasValue);
    }

    private static void AddFlowLine(
        ICollection<ChartLine> lines,
        ICollection<string> missing,
        string name,
        IEnumerable<ChartPoint> points)
    {
        var materialised = points.ToArray();

        if (materialised.All(point => !point.Value.HasValue))
        {
            missing.Add(name.ToLowerInvariant());
            return;
        }

        lines.Add(new ChartLine(name, materialised));
    }

    private static string DescribeFlowCoverage(FlowCoverage coverage)
    {
        var counted = coverage.MessagesCounted is { } value
            ? $"{value:N0} message{(value == 1 ? string.Empty : "s")} counted."
            : "The Host did not report how many messages were counted.";

        var skipped = coverage.MessagesSkipped is > 0 and { } count
            ? $" {count:N0} could not be counted."
            : string.Empty;

        var completeness = coverage.Complete
            ? "The whole window was counted."
            : "This is a bounded sample, not the whole window.";

        return counted + skipped + " " + completeness;
    }

    private static string DescribeTotals(FlowTotals totals, string? rejectedBasis)
    {
        var parts = new List<string>();
        if (totals.Inbound is { } inbound) parts.Add("Inbound " + inbound.ToString("N0", CultureInfo.InvariantCulture));
        if (totals.Outbound is { } outbound) parts.Add("Outbound " + outbound.ToString("N0", CultureInfo.InvariantCulture));
        if (totals.Delivered is { } delivered) parts.Add("Delivered " + delivered.ToString("N0", CultureInfo.InvariantCulture));
        if (totals.Held is { } held) parts.Add("Held " + held.ToString("N0", CultureInfo.InvariantCulture));
        if (totals.Review is { } review) parts.Add("Awaiting review " + review.ToString("N0", CultureInfo.InvariantCulture));
        if (totals.Quarantined is { } quarantined) parts.Add("Stopped " + quarantined.ToString("N0", CultureInfo.InvariantCulture));
        if (totals.Other is { } other) parts.Add("Other " + other.ToString("N0", CultureInfo.InvariantCulture));

        if (totals.Rejected is { } rejected)
        {
            // Refused decisions are a separate population from the accepted-message series. The
            // label follows the served basis rather than calling them messages.
            var label = string.Equals(rejectedBasis, "rejected_decisions_in_bucket", StringComparison.OrdinalIgnoreCase)
                ? "Refused decisions "
                : "Rejected ";

            parts.Add(label + rejected.ToString("N0", CultureInfo.InvariantCulture));
        }

        return parts.Count == 0
            ? "The Host did not produce totals for this window."
            : string.Join(", ", parts) + ".";
    }

    private static IReadOnlyList<DateTimeOffset> Buckets(DateTimeOffset start, DateTimeOffset end, TimeSpan size)
    {
        var result = new List<DateTimeOffset>();
        var floor = FloorTo(start, size);

        for (var current = floor; current < end; current += size)
        {
            result.Add(current);
        }

        if (result.Count == 0) result.Add(floor);
        return result;
    }

    private static DateTimeOffset FloorTo(DateTimeOffset value, TimeSpan size)
    {
        var utc = value.ToUniversalTime();
        var ticks = utc.UtcTicks - (utc.UtcTicks % size.Ticks);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static string FormatCount(long? value)
        => value is { } count ? count.ToString("N0", CultureInfo.InvariantCulture) : "Not counted";

    private static string FormatBounded((long? Count, bool Capped) value)
    {
        if (value.Count is not { } count) return "Not counted";
        return value.Capped ? count.ToString("N0", CultureInfo.InvariantCulture) + "+" : count.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)value;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return size.ToString(size >= 100 ? "N0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static string WindowLabel(DateTimeOffset start, DateTimeOffset end)
        => "Last 24 hours, "
            + start.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)
            + " to "
            + end.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)
            + " (local time)";

    private string Relative(DateTimeOffset value)
    {
        var delta = _clock() - value;

        if (delta < TimeSpan.FromMinutes(1)) return "just now";
        if (delta < TimeSpan.FromHours(1)) return $"{(int)delta.TotalMinutes} minutes ago";
        if (delta < TimeSpan.FromDays(1)) return $"{(int)delta.TotalHours} hours ago";
        return $"{(int)delta.TotalDays} days ago";
    }

    private static string Shorten(string token)
        => token.Length <= 10 ? token : token[..8] + "...";

    private static string Reason(Exception ex) => ex is StyloMailApiException api
        ? api.Failure switch
        {
            StyloMailApiFailure.ApiKeyNotConfigured => "No API key is configured.",
            StyloMailApiFailure.Unreachable => "The Host could not be reached.",
            StyloMailApiFailure.UnreadableResponse => "The Host's answer could not be read by this build.",
            _ => api.Detail ?? api.Code ?? "The Host refused the request.",
        }
        : "No answer was received.";
}

/// <summary>One warning card. Severity names a state, never a person.</summary>
public sealed class DashboardWarning
{
    public DashboardWarning(
        string id,
        string severity,
        string whatHappened,
        string subject,
        string nextAction,
        string actionLabel,
        string actionDestination,
        bool canDismiss = false)
    {
        Id = id;
        Severity = severity;
        WhatHappened = whatHappened;
        Subject = subject;
        NextAction = nextAction;
        ActionLabel = actionLabel;
        ActionDestination = actionDestination;
        CanDismiss = canDismiss;
    }

    public string Id { get; }

    public string Severity { get; }

    public string WhatHappened { get; }

    public string Subject { get; }

    public string NextAction { get; }

    public string ActionLabel { get; }

    public string ActionDestination { get; }

    public bool CanDismiss { get; }

    public bool IsCritical => Severity == "critical";

    public bool IsWarning => Severity == "warning";

    public bool IsInfo => Severity == "info";

    public string SeverityLabel => Severity switch
    {
        "critical" => "Critical",
        "warning" => "Warning",
        _ => "Information",
    };

    public bool HasAction => !string.IsNullOrEmpty(ActionLabel);

    public string AutomationId => "warning-" + Id;
}

/// <summary>One row in the queue count card.</summary>
public sealed record QueueCountRow(
    string Label,
    string CountLabel,
    string Detail,
    string StateKey,
    string AutomationId)
{
    /// <summary>Bar length in pixels, relative to the largest row. Zero when not counted.</summary>
    public double BarWidth { get; init; }
}

/// <summary>One sending account in the people card.</summary>
public sealed record SenderAttentionRow(
    string Label,
    string StateLabel,
    string Detail,
    string PrincipalId,
    string AutomationId);

/// <summary>One recent conversation in the conversations card.</summary>
public sealed record DashboardConversationRow(
    string Reference,
    string LastActivity,
    string Detail,
    string AutomationId);
