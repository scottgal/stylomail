using System.Collections.ObjectModel;
using System.Globalization;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.ViewModels;

/// <summary>
/// The service page: whether mail is moving, who can connect, and what the proxy is doing.
/// </summary>
/// <remarks>
/// The default surface uses service words. The raw protocol state, account material and route names
/// live in the Advanced expander. Actions are ask-then-confirm; the proxy routes record the actor but
/// do not accept a reason field in this build, and the confirm panel says so rather than implying the
/// note is stored.
/// </remarks>
public sealed class ServiceViewModel : ObservableObject
{
    private StyloMailApiClient? _client;
    private bool _isLoading;
    private bool _isApplying;
    private string _serviceState = "unknown";
    private string _serviceHeadline = "Checking the mail service.";
    private string _acceptingLabel = "Accepting mail: checking.";
    private string _feedLabel = "Live feed: checking.";
    private string _lastCheckedLabel = string.Empty;
    private string _hostAddress = string.Empty;
    private string _statusLabel = "The service has not been read yet.";
    private string _lastUpdatedLabel = "Not read yet.";
    private string _sessionsNote = "Live sessions are not loaded.";
    private string _operationsLabel = "Operations are not loaded.";
    private string _operationsCoverage = string.Empty;
    private string _queueLabel = "Queue counts are not loaded.";
    private string _storageLabel = "Storage is not loaded.";
    private ServiceAction? _pendingAction;
    private string _pendingReason = string.Empty;
    private string? _lastResult;
    private bool _lastFailed;
    private bool _sectionFailed;

    public ServiceViewModel(StyloMailApiClient? client = null) => _client = client;

    public ObservableCollection<ServiceAccountRow> Accounts { get; } = [];

    public ObservableCollection<ServiceListenerRow> Listeners { get; } = [];

    public ObservableCollection<ServiceSessionRow> Sessions { get; } = [];

    public ObservableCollection<string> TechnicalLines { get; } = [];

    public bool HasAccounts => Accounts.Count > 0;

    public bool HasListeners => Listeners.Count > 0;

    public bool HasSessions => Sessions.Count > 0;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (Set(ref _isLoading, value)) Raise(nameof(RefreshLabel));
        }
    }

    public string RefreshLabel => IsLoading ? "Reading" : "Refresh";

    public string ServiceState
    {
        get => _serviceState;
        private set => Set(ref _serviceState, value);
    }

    public string ServiceHeadline
    {
        get => _serviceHeadline;
        private set => Set(ref _serviceHeadline, value);
    }

    public string AcceptingLabel
    {
        get => _acceptingLabel;
        private set => Set(ref _acceptingLabel, value);
    }

    public string FeedLabel
    {
        get => _feedLabel;
        private set => Set(ref _feedLabel, value);
    }

    public string LastCheckedLabel
    {
        get => _lastCheckedLabel;
        private set => Set(ref _lastCheckedLabel, value);
    }

    public string HostAddress
    {
        get => _hostAddress;
        private set => Set(ref _hostAddress, value);
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

    public string SessionsNote
    {
        get => _sessionsNote;
        private set => Set(ref _sessionsNote, value);
    }

    public string OperationsLabel
    {
        get => _operationsLabel;
        private set => Set(ref _operationsLabel, value);
    }

    public string OperationsCoverage
    {
        get => _operationsCoverage;
        private set => Set(ref _operationsCoverage, value);
    }

    public string QueueLabel
    {
        get => _queueLabel;
        private set => Set(ref _queueLabel, value);
    }

    public string StorageLabel
    {
        get => _storageLabel;
        private set => Set(ref _storageLabel, value);
    }

    public ServiceAction? PendingAction
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

    /// <summary>Replaces the client after a reconnect.</summary>
    public void SetClient(StyloMailApiClient? client) => _client = client;

    public void SetHostAddress(string address) => HostAddress = address;

    public void SetFeedStatus(string headline)
    {
        FeedLabel = string.IsNullOrWhiteSpace(headline)
            ? "Live feed: unknown."
            : string.Equals(headline, "Live", StringComparison.OrdinalIgnoreCase)
                ? "Live feed: connected."
                : "Live feed: " + headline;
    }

    /// <summary>Reads the service surfaces. One failed section does not blank the others.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading) return;

        IsLoading = true;
        _sectionFailed = false;

        try
        {
            if (_client is null)
            {
                ServiceState = "unknown";
                ServiceHeadline = "This console is not connected to a Host.";
                AcceptingLabel = "Accepting mail: unknown.";
                LastCheckedLabel = string.Empty;
                StatusLabel = "No Host is connected.";
                Accounts.Clear();
                Listeners.Clear();
                Sessions.Clear();
                TechnicalLines.Clear();
                return;
            }

            await LoadHealthAsync(cancellationToken);
            await LoadAccountsAndSessionsAsync(cancellationToken);
            await LoadListenersAsync(cancellationToken);
            await LoadOperationsAsync(cancellationToken);
            await LoadQueueAndStorageAsync(cancellationToken);

            LastUpdatedLabel = "Updated " + DateTimeOffset.UtcNow.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            StatusLabel = _sectionFailed
                ? "Some service data could not be read. See the section notes."
                : "Service data is current. Raw protocol detail is under Advanced.";

            TechnicalLines.Add(
                "Routes read: GET /v1/proxy/accounts; GET /v1/proxy/sessions; GET /v1/proxy/listeners; "
                + "GET /v1/proxy/operations?window=last_24_hours; GET /v1/queues/summary; GET /v1/storage/summary; GET /health/ready.");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void AskEnableAccount(ServiceAccountRow row)
        => Ask(new ServiceAction(
            ServiceActionKind.EnableAccount,
            row.AccountId,
            "Enable " + row.Title,
            "The account will accept new client connections again. Existing sessions are not changed.",
            "Enable account"));

    public void AskPauseAccount(ServiceAccountRow row)
        => Ask(new ServiceAction(
            ServiceActionKind.PauseAccount,
            row.AccountId,
            "Disable " + row.Title,
            "New client connections for this account will be refused. Connected sessions keep running until they finish.",
            "Disable account"));

    public void AskRevokeAccount(ServiceAccountRow row)
        => Ask(new ServiceAction(
            ServiceActionKind.RevokeAccount,
            row.AccountId,
            "Revoke the credential for " + row.Title,
            "The stored backend credential will be revoked and the account cannot connect until it is enrolled again.",
            "Revoke credential"));

    public void AskEnableListener(ServiceListenerRow row)
        => Ask(new ServiceAction(
            ServiceActionKind.EnableListener,
            row.ListenerId,
            "Enable " + row.Title,
            "The listener will accept new client connections on its configured port.",
            "Enable listener"));

    public void AskDrainListener(ServiceListenerRow row)
        => Ask(new ServiceAction(
            ServiceActionKind.DrainListener,
            row.ListenerId,
            "Drain " + row.Title,
            "The listener stops accepting new connections. Connected sessions finish before it stops.",
            "Drain listener"));

    public void CancelPending()
    {
        PendingAction = null;
        PendingReason = string.Empty;
    }

    public void DismissResult() => LastResult = null;

    /// <summary>Applies the pending action after the operator has confirmed it.</summary>
    public async Task ConfirmAsync(CancellationToken cancellationToken = default)
    {
        if (!CanConfirm || _client is null || PendingAction is not { } action) return;

        IsApplying = true;
        LastFailed = false;

        try
        {
            switch (action.Kind)
            {
                case ServiceActionKind.EnableAccount:
                    await _client.SetProxyAccountEnabledAsync(action.TargetId, true, cancellationToken).ConfigureAwait(true);
                    break;
                case ServiceActionKind.PauseAccount:
                    await _client.SetProxyAccountEnabledAsync(action.TargetId, false, cancellationToken).ConfigureAwait(true);
                    break;
                case ServiceActionKind.RevokeAccount:
                    await _client.RevokeProxyCredentialAsync(action.TargetId, cancellationToken).ConfigureAwait(true);
                    break;
                case ServiceActionKind.EnableListener:
                    await _client.SetProxyListenerEnabledAsync(action.TargetId, true, cancellationToken).ConfigureAwait(true);
                    break;
                case ServiceActionKind.DrainListener:
                    await _client.SetProxyListenerEnabledAsync(action.TargetId, false, cancellationToken).ConfigureAwait(true);
                    break;
            }

            var reason = PendingReason;
            LastResult = "Applied. The Host records who applied this change. Your note was: " + reason;
            PendingAction = null;
            PendingReason = string.Empty;
            await LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (StyloMailApiException failure)
        {
            LastFailed = true;
            LastResult = "Could not apply the change. " + (failure.Detail ?? failure.Code ?? failure.Message);
            PendingAction = null;
            PendingReason = string.Empty;
        }
        catch (HttpRequestException)
        {
            LastFailed = true;
            LastResult = "Could not apply the change. The Host could not be reached.";
            PendingAction = null;
            PendingReason = string.Empty;
        }
        finally
        {
            IsApplying = false;
        }
    }

    private void Ask(ServiceAction action)
    {
        PendingAction = action;
        PendingReason = string.Empty;
        LastResult = null;
        LastFailed = false;
    }

    private async Task LoadHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            var readiness = await _client!.GetReadinessAsync(cancellationToken).ConfigureAwait(true);
            ServiceState = readiness.Ready ? "good" : "critical";
            ServiceHeadline = readiness.Ready ? "Mail is flowing normally." : "Mail is stopped.";
            AcceptingLabel = readiness.Ready ? "Accepting mail: yes." : "Accepting mail: no.";
            LastCheckedLabel = "Last checked " + DateTimeOffset.UtcNow.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                + (readiness.Ready ? "." : ". Failed checks: " + string.Join(", ", readiness.FailedChecks ?? []) + ".");
        }
        catch (Exception ex) when (ex is StyloMailApiException or HttpRequestException)
        {
            _sectionFailed = true;
            ServiceState = "critical";
            ServiceHeadline = "The console cannot reach the Host.";
            AcceptingLabel = "Accepting mail: unknown.";
            LastCheckedLabel = string.Empty;
            StatusLabel = ex is StyloMailApiException api ? api.Message : "No answer was received.";
        }
    }

    private async Task LoadAccountsAndSessionsAsync(CancellationToken cancellationToken)
    {
        Accounts.Clear();
        Sessions.Clear();
        TechnicalLines.Clear();

        try
        {
            var accounts = await _client!.GetProxyAccountsAsync(cancellationToken: cancellationToken).ConfigureAwait(true);
            var sessions = await _client.GetLiveProxySessionsAsync(cancellationToken: cancellationToken).ConfigureAwait(true);

            var liveByAccount = sessions.Sessions
                .Where(session => session.AccountId is not null)
                .GroupBy(session => session.AccountId!, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(session => session.StartedAt).First(),
                    StringComparer.Ordinal);

            foreach (var account in accounts.Accounts)
            {
                liveByAccount.TryGetValue(account.AccountId, out var session);
                Accounts.Add(ServiceAccountRow.From(account, session));
                TechnicalLines.Add(
                    $"Account {account.AccountId}: login key {account.LoginKey}; provider {account.ProviderId}; "
                    + $"enrolment {account.EnrollmentState}; credential {account.CredentialDiscriminator}; "
                    + $"created {account.CreatedAt:u}; updated {account.UpdatedAt:u}; revoked {account.RevokedAt:u}.");
            }

            if (string.Equals(sessions.Availability, "not_produced", StringComparison.OrdinalIgnoreCase))
            {
                SessionsNote = "This Host does not report live sessions. An empty list is not a claim of zero sessions.";
            }
            else
            {
                SessionsNote = sessions.Sessions.Count == 0
                    ? "No live sessions."
                    : $"{sessions.Sessions.Count:N0} live session{(sessions.Sessions.Count == 1 ? string.Empty : "s")}.";

                foreach (var session in sessions.Sessions)
                {
                    var account = Accounts.FirstOrDefault(candidate => candidate.AccountId == session.AccountId);
                    Sessions.Add(ServiceSessionRow.From(session, account?.Title));
                    TechnicalLines.Add(
                        $"Session {session.SessionId}: account {session.AccountId ?? "not produced"}; protocol {session.Protocol}; "
                        + $"direction {session.Direction}; outcome {session.Outcome ?? "active"}; started {session.StartedAt:u}; "
                        + $"authenticated {session.AuthenticatedAt:u}; client bytes {Count(session.ClientBytes)}; backend bytes {Count(session.BackendBytes)}; "
                        + $"bound reason {session.BoundReason ?? "none"}.");
                }
            }
        }
        catch (Exception ex) when (ex is StyloMailApiException or HttpRequestException)
        {
            _sectionFailed = true;
            SessionsNote = "Live sessions and accounts could not be read.";
            StatusLabel = "Accounts and sessions could not be read. " + Reason(ex);
        }
    }

    private async Task LoadListenersAsync(CancellationToken cancellationToken)
    {
        Listeners.Clear();

        try
        {
            var listeners = await _client!.GetProxyListenersAsync(cancellationToken).ConfigureAwait(true);

            foreach (var listener in listeners.Listeners)
            {
                Listeners.Add(ServiceListenerRow.From(listener));
                TechnicalLines.Add(
                    $"Listener {listener.ListenerId}: protocol {listener.Protocol}; raw state {listener.State}; "
                    + $"TLS {listener.TlsPolicy}; enabled {listener.Enabled}; draining {listener.Draining}; "
                    + $"port {listener.BoundPort?.ToString(CultureInfo.InvariantCulture) ?? "not reported"}; "
                    + $"fault {listener.FaultReason ?? "none"}; detail {listener.SafeDetail ?? "none"}; "
                    + $"observed {listener.ObservedAt:u}.");
            }
        }
        catch (Exception ex) when (ex is StyloMailApiException or HttpRequestException)
        {
            _sectionFailed = true;
            StatusLabel = "Listeners could not be read. " + Reason(ex);
        }
    }

    private async Task LoadOperationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var operations = await _client!.GetProxyOperationsAsync("last_24_hours", cancellationToken).ConfigureAwait(true);

            if (string.Equals(operations.Availability, "not_produced", StringComparison.OrdinalIgnoreCase))
            {
                OperationsLabel = "Operations were not produced for the last 24 hours.";
                OperationsCoverage = $"Window from {operations.WindowStart.ToLocalTime():d MMM HH:mm} to {operations.WindowEnd.ToLocalTime():d MMM HH:mm}.";
                return;
            }

            OperationsLabel =
                $"Last 24 hours: opened {Count(operations.Counters.Opened)}, closed {Count(operations.Counters.Closed)}, "
                + $"faulted {Count(operations.Counters.Faulted)}, live {Count(operations.Counters.LiveSessions)}.";
            OperationsCoverage =
                $"Window from {operations.WindowStart.ToLocalTime():d MMM HH:mm} to {operations.WindowEnd.ToLocalTime():d MMM HH:mm}. "
                + $"Source {operations.Provenance}.";
            TechnicalLines.Add(
                $"Operations: window {operations.WindowStart:u} to {operations.WindowEnd:u}; availability {operations.Availability}; "
                + $"provenance {operations.Provenance}; opened {Count(operations.Counters.Opened)}; closed {Count(operations.Counters.Closed)}; "
                + $"faulted {Count(operations.Counters.Faulted)}; live {Count(operations.Counters.LiveSessions)}.");
        }
        catch (Exception ex) when (ex is StyloMailApiException or HttpRequestException)
        {
            _sectionFailed = true;
            OperationsLabel = "Operations could not be read. " + Reason(ex);
            OperationsCoverage = string.Empty;
        }
    }

    private async Task LoadQueueAndStorageAsync(CancellationToken cancellationToken)
    {
        try
        {
            var queues = await _client!.GetQueueSummaryAsync(cancellationToken).ConfigureAwait(true);

            if (string.Equals(queues.Availability, "not_produced", StringComparison.OrdinalIgnoreCase))
            {
                QueueLabel = "Queue counts were not produced.";
            }
            else
            {
                QueueLabel =
                    $"In the delivery queue {Count(queues.Counts.AwaitingDecision)}, "
                    + $"being re-checked {Count(queues.Counts.Held)}, "
                    + $"stopped {Count(queues.Counts.Quarantined)}, "
                    + $"awaiting human review {Count(queues.Counts.Review)}. "
                    + "The first count includes the other three; they are not a sum.";
                TechnicalLines.Add(
                    $"Queues: availability {queues.Availability}; basis {queues.Basis ?? "not reported"}; "
                    + $"overlapping {queues.Overlapping}; counts {queues.Counts.AwaitingDecision}/{queues.Counts.Held}/"
                    + $"{queues.Counts.Quarantined}/{queues.Counts.Review}.");
            }
        }
        catch (Exception ex) when (ex is StyloMailApiException or HttpRequestException)
        {
            _sectionFailed = true;
            QueueLabel = "Queue counts could not be read. " + Reason(ex);
        }

        try
        {
            var storage = await _client!.GetStorageSummaryAsync(cancellationToken).ConfigureAwait(true);

            if (string.Equals(storage.Availability, "not_produced", StringComparison.OrdinalIgnoreCase))
            {
                StorageLabel = "Storage was not produced.";
                return;
            }

            var parts = new List<string>();
            if (storage.DatabaseBytes is { } database) parts.Add("database " + FormatBytes(database));
            if (storage.PayloadBytes is { } payload) parts.Add("payloads " + FormatBytes(payload));
            if (storage.SpoolBytes is { } spool) parts.Add("spool " + (storage.SpoolTruncated == true ? "at least " : string.Empty) + FormatBytes(spool));
            if (storage.VolumeFreeBytes is { } free) parts.Add(FormatBytes(free) + " free on disk");

            StorageLabel = parts.Count == 0
                ? "Storage is not measured."
                : "Stored: " + string.Join(", ", parts) + ".";
            TechnicalLines.Add(
                $"Storage: availability {storage.Availability}; database {storage.DatabaseBytes?.ToString(CultureInfo.InvariantCulture) ?? "not reported"}; "
                + $"WAL {storage.WriteAheadLogBytes?.ToString(CultureInfo.InvariantCulture) ?? "not reported"}; "
                + $"spool {storage.SpoolBytes?.ToString(CultureInfo.InvariantCulture) ?? "not reported"} (truncated {storage.SpoolTruncated?.ToString() ?? "not reported"}); "
                + $"payloads {storage.PayloadBytes?.ToString(CultureInfo.InvariantCulture) ?? "not reported"}; "
                + $"capacity {storage.CapacityBytes?.ToString(CultureInfo.InvariantCulture) ?? "not reported"}; "
                + $"free {storage.VolumeFreeBytes?.ToString(CultureInfo.InvariantCulture) ?? "not reported"}; "
                + $"retention reason {storage.RetentionReason ?? "none"}.");
        }
        catch (Exception ex) when (ex is StyloMailApiException or HttpRequestException)
        {
            _sectionFailed = true;
            StorageLabel = "Storage could not be read. " + Reason(ex);
        }
    }

    private static string Count(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "not produced";

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

    private static string Reason(Exception ex) => ex is StyloMailApiException api
        ? api.Detail ?? api.Code ?? api.Message
        : "The Host could not be reached.";
}

public enum ServiceActionKind
{
    EnableAccount,
    PauseAccount,
    RevokeAccount,
    EnableListener,
    DrainListener,
}

public sealed record ServiceAction(
    ServiceActionKind Kind,
    string TargetId,
    string Title,
    string Consequence,
    string ConfirmLabel);

public sealed record ServiceAccountRow
{
    public required string AccountId { get; init; }
    public required string Title { get; init; }
    public required string StateLabel { get; init; }
    public required string Detail { get; init; }
    public required string LiveLabel { get; init; }
    public required bool CanEnable { get; init; }
    public required bool CanPause { get; init; }
    public required bool CanRevoke { get; init; }
    public bool IsRevoked => StateLabel == "Revoked";

    public static ServiceAccountRow From(ProxyAccountResponse account, ProxySessionResponse? live)
    {
        var state = account.RevokedAt is not null
            ? "Revoked"
            : account.Enabled ? "Enabled" : "Paused";

        var liveLabel = live is null
            ? "No live session."
            : $"In use now: {live.Protocol.ToUpperInvariant()} session started {live.StartedAt.ToLocalTime():HH:mm}.";

        return new ServiceAccountRow
        {
            AccountId = account.AccountId,
            Title = "Account " + Shorten(account.AccountId),
            StateLabel = state,
            Detail = "Provider " + account.ProviderId + ". Last changed " + account.UpdatedAt.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture) + ".",
            LiveLabel = liveLabel,
            CanEnable = !account.Enabled && account.RevokedAt is null,
            CanPause = account.Enabled,
            CanRevoke = account.RevokedAt is null,
        };
    }

    private static string Shorten(string value) => value.Length <= 10 ? value : value[..8] + "...";
}

public sealed record ServiceListenerRow
{
    public required string ListenerId { get; init; }
    public required string Title { get; init; }
    public required string StateLabel { get; init; }
    public required string BoundLabel { get; init; }
    public required string TlsLabel { get; init; }
    public required string Detail { get; init; }
    public required bool CanEnable { get; init; }
    public required bool CanDrain { get; init; }

    public static ServiceListenerRow From(ProxyListenerResponse listener)
    {
        var state = listener.FaultReason is not null || string.Equals(listener.State, "Faulted", StringComparison.OrdinalIgnoreCase)
            ? "Faulted"
            : listener.Draining
                ? "Draining"
                : !listener.Enabled
                    ? "Stopped"
                    : string.Equals(listener.State, "Listening", StringComparison.OrdinalIgnoreCase)
                        ? "Listening"
                        : "Unknown";

        var tls = listener.TlsPolicy switch
        {
            "Required" => "Encrypted",
            "LoopbackHarnessPlaintext" => "Plaintext, loopback only",
            _ => "TLS mode not recognised",
        };

        var bound = listener.BoundPort is { } port
            ? "Port " + port.ToString(CultureInfo.InvariantCulture)
            : "Port not reported";

        var detail = state switch
        {
            "Draining" => "New connections are refused; connected sessions finish.",
            "Faulted" => "The listener is not accepting connections." + (listener.FaultReason is null ? string.Empty : " " + listener.FaultReason),
            "Stopped" => "The listener is turned off.",
            "Listening" => "The listener is accepting connections.",
            _ => "The Host did not report a recognised state.",
        };

        return new ServiceListenerRow
        {
            ListenerId = listener.ListenerId,
            Title = listener.Protocol.ToUpperInvariant() + " · " + bound,
            StateLabel = state,
            BoundLabel = bound + ". The bind address is not reported by this Host.",
            TlsLabel = tls,
            Detail = detail,
            CanEnable = !listener.Enabled,
            CanDrain = listener.Enabled && !listener.Draining,
        };
    }
}

public sealed record ServiceSessionRow
{
    public required string Title { get; init; }
    public required string AccountLabel { get; init; }
    public required string StartedLabel { get; init; }
    public required string Detail { get; init; }

    public static ServiceSessionRow From(ProxySessionResponse session, string? accountLabel)
        => new()
        {
            Title = session.Protocol.ToUpperInvariant() + " " + session.Direction.ToLowerInvariant(),
            AccountLabel = accountLabel ?? "Account " + Shorten(session.AccountId),
            StartedLabel = "Started " + session.StartedAt.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture),
            Detail = "No message content or protocol commands are shown.",
        };

    private static string Shorten(string? value)
        => value is null ? "not reported" : value.Length <= 10 ? value : value[..8] + "...";
}
