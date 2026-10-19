using System.Collections.ObjectModel;
using System.Globalization;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.ViewModels;

/// <summary>
/// The people directory: correspondents, sending accounts and companies.
/// </summary>
/// <remarks>
/// A directory row is a structural record, not a mailbox. A recipient name is a keyed hash when no
/// label was stored, and the page shows that hash shortened rather than reconstructing an address.
/// Evidence is deliberately not computed per row; the profile action is where it lives.
/// </remarks>
public sealed class RecipientsViewModel : ObservableObject
{
    public const int PageSize = 50;
    public const int MaximumQueryLength = 128;

    private StyloMailApiClient? _client;
    private bool _isLoading;
    private bool _hasLoaded;
    private bool _hasMore;
    private bool _failed;
    private string? _nextCursor;
    private string _kind = "recipient";
    private string _query = string.Empty;
    private string _statusLabel = "People are not loaded.";
    private string _emptyLabel = "No people yet.";
    private string _coverageLabel = string.Empty;

    public RecipientsViewModel(StyloMailApiClient? client = null) => _client = client;

    /// <summary>Raised when a row asks for its profile. The window owns the dialog.</summary>
    public event EventHandler<RecipientRowView>? ProfileRequested;

    public ObservableCollection<RecipientRowView> Rows { get; } = [];

    public string Kind => _kind;

    public bool IsPeopleKind => _kind == "recipient";

    public bool IsSenderKind => _kind == "sender";

    public bool IsCompanyKind => _kind == "company";

    public string SearchText
    {
        get => _query;
        set => Set(ref _query, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (Set(ref _isLoading, value))
            {
                Raise(nameof(ShowEmpty));
                Raise(nameof(ShowLoadMore));
            }
        }
    }

    public bool HasRows => Rows.Count > 0;

    public bool ShowEmpty => _hasLoaded && !_failed && !IsLoading && Rows.Count == 0;

    public bool ShowLoadMore => !IsLoading && !_failed && _hasMore;

    public string StatusLabel
    {
        get => _statusLabel;
        private set => Set(ref _statusLabel, value);
    }

    public string EmptyLabel
    {
        get => _emptyLabel;
        private set => Set(ref _emptyLabel, value);
    }

    public string CoverageLabel
    {
        get => _coverageLabel;
        private set => Set(ref _coverageLabel, value);
    }

    /// <summary>Replaces the client after a reconnect.</summary>
    public void SetClient(StyloMailApiClient? client) => _client = client;

    /// <summary>Switches kind and reads the first page.</summary>
    public async Task LoadKindAsync(string kind, CancellationToken cancellationToken = default)
    {
        if (_kind == kind && _hasLoaded) return;

        _kind = kind;
        _hasLoaded = false;
        _hasMore = false;
        _nextCursor = null;
        Rows.Clear();
        OnKindChanged();
        OnRowsChanged();
        await LoadAsync(append: false, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Reads the current kind, or the next page when <paramref name="append"/> is true.</summary>
    public async Task LoadAsync(bool append = false, CancellationToken cancellationToken = default)
    {
        if (IsLoading) return;
        if (append && !_hasMore) return;

        if (SearchText.Length > MaximumQueryLength)
        {
            _failed = true;
            StatusLabel = $"Search is limited to {MaximumQueryLength} characters.";
            EmptyLabel = "Shorten the search and try again.";
            OnRowsChanged();
            return;
        }

        if (_client is null)
        {
            _failed = true;
            StatusLabel = "The directory is unavailable until the console is connected to a Host.";
            EmptyLabel = "No Host is connected.";
            _hasLoaded = true;
            OnRowsChanged();
            return;
        }

        IsLoading = true;
        _failed = false;

        try
        {
            var page = await _client
                .GetEntityDirectoryAsync(_kind, SearchText, PageSize, append ? _nextCursor : null, cancellationToken)
                .ConfigureAwait(true);

            if (string.Equals(page.Availability, "not_produced", StringComparison.OrdinalIgnoreCase))
            {
                Rows.Clear();
                _hasMore = false;
                _nextCursor = null;
                _hasLoaded = true;
                StatusLabel = "Not produced: " + DescribeAbsences(page.NotProduced);
                EmptyLabel = "This Host did not produce a directory.";
                CoverageLabel = string.Empty;
                OnRowsChanged();
                return;
            }

            var paused = await LoadPausedSendersAsync(cancellationToken).ConfigureAwait(true);
            var rows = page.Entities.Select(row => RecipientRowView.From(row, paused)).ToArray();

            if (!append) Rows.Clear();
            foreach (var row in rows) Rows.Add(row);

            _nextCursor = page.NextCursor;
            _hasMore = page.HasMore;
            _hasLoaded = true;

            StatusLabel = DescribeStatus(kind: _kind, count: Rows.Count, hasMore: _hasMore, query: SearchText);
            EmptyLabel = DescribeEmpty(_kind, SearchText);
            CoverageLabel = DescribeCoverage(page);

            OnRowsChanged();
        }
        catch (StyloMailApiException failure)
        {
            _failed = true;
            _hasLoaded = true;
            StatusLabel = failure.Code == "query_too_long"
                ? $"Search is limited to {MaximumQueryLength} characters."
                : "The directory could not be read. " + Reason(failure);
            EmptyLabel = "The directory could not be read.";
            OnRowsChanged();
        }
        catch (HttpRequestException)
        {
            _failed = true;
            _hasLoaded = true;
            StatusLabel = "The directory could not be read. The Host could not be reached.";
            EmptyLabel = "The directory could not be read.";
            OnRowsChanged();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Applies the search box and reads the first page.</summary>
    public Task SearchAsync(CancellationToken cancellationToken = default)
    {
        _hasLoaded = false;
        _hasMore = false;
        _nextCursor = null;
        Rows.Clear();
        OnRowsChanged();
        return LoadAsync(append: false, cancellationToken);
    }

    /// <summary>Clears the search and reads the first page.</summary>
    public Task ClearSearchAsync(CancellationToken cancellationToken = default)
    {
        SearchText = string.Empty;
        return SearchAsync(cancellationToken);
    }

    /// <summary>Asks the window to open a row's profile.</summary>
    public void RequestProfile(RecipientRowView row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ProfileRequested?.Invoke(this, row);
    }

    private async Task<PausedSenders> LoadPausedSendersAsync(CancellationToken cancellationToken)
    {
        if (_kind != "sender" || _client is null) return PausedSenders.NotApplicable;

        try
        {
            var listing = await _client.GetSendersAsync(cancellationToken).ConfigureAwait(true);
            return new PausedSenders(
                true,
                listing.Senders.ToDictionary(
                    sender => sender.PrincipalId,
                    sender => sender.Control.Paused,
                    StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is StyloMailApiException or HttpRequestException)
        {
            // The directory is still useful without pause state. The row says
            // the state was not read rather than showing every sender as active.
            return new PausedSenders(false, new Dictionary<string, bool>());
        }
    }



    private void OnKindChanged()
    {
        Raise(nameof(Kind));
        Raise(nameof(IsPeopleKind));
        Raise(nameof(IsSenderKind));
        Raise(nameof(IsCompanyKind));
    }

    private void OnRowsChanged()
    {
        Raise(nameof(HasRows));
        Raise(nameof(ShowEmpty));
        Raise(nameof(ShowLoadMore));
    }

    private static string DescribeStatus(string kind, int count, bool hasMore, string query)
    {
        var noun = kind switch
        {
            "sender" => count == 1 ? "sending account" : "sending accounts",
            "company" => count == 1 ? "company" : "companies",
            _ => count == 1 ? "person" : "people",
        };

        var sentence = count == 0
            ? "No rows."
            : $"{count:N0} {noun}" + (hasMore ? ", more available." : ".");

        return string.IsNullOrWhiteSpace(query) ? sentence : sentence + $" Search: \"{query}\".";
    }

    private static string DescribeEmpty(string kind, string query)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            return $"No match for \"{query}\".";
        }

        return kind switch
        {
            "sender" => "No sending accounts yet.",
            "company" => "No companies yet.",
            _ => "No people yet.",
        };
    }

    private static string DescribeCoverage(EntityDirectoryResponse page)
    {
        if (!string.IsNullOrWhiteSpace(page.Coverage.Note))
        {
            return page.Coverage.Note!;
        }

        return string.Equals(page.Availability, "partial", StringComparison.OrdinalIgnoreCase)
            ? "Some rows were not produced. The page shows the rows the Host could read."
            : "A bounded page from the Host. Search is limited to 128 characters.";
    }

    private static string DescribeAbsences(IReadOnlyList<SummaryAbsence> absences)
    {
        if (absences.Count == 0) return "the Host did not name a reason.";

        return string.Join(", ", absences.Select(absence => absence.Series + " (" + absence.Reason + ")"));
    }

    private static string Reason(StyloMailApiException failure) => failure.Failure switch
    {
        StyloMailApiFailure.ApiKeyNotConfigured => "No API key is configured.",
        StyloMailApiFailure.Unreachable => "The Host could not be reached.",
        StyloMailApiFailure.UnreadableResponse => "The Host's answer could not be read by this build.",
        _ => failure.Detail ?? failure.Code ?? "The Host refused the request.",
    };
}

/// <summary>One directory row, in the operator's words.</summary>
public sealed class RecipientRowView
{
    public required string ScopeId { get; init; }

    /// <summary>The scope kind the profile route accepts: entity, domain or company.</summary>
    public required string ProfileKind { get; init; }

    public string? CompanyId { get; init; }

    public required string Title { get; init; }

    public required string KindLabel { get; init; }

    public required string DirectionLabel { get; init; }

    public required string Detail { get; init; }

    public required string VolumeLabel { get; init; }

    public required string LastContactLabel { get; init; }

    public required string EvidenceLabel { get; init; }

    public string? WarningLabel { get; init; }

    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningLabel);

    public bool IsPaused { get; init; }

    public bool PauseStateAvailable { get; init; }

    public string PauseStateLabel => !PauseStateAvailable
        ? "Pause state not read"
        : IsPaused ? "Sending paused" : "Sending active";

    public bool IsSender { get; init; }

    public bool IsCompany { get; init; }

    public bool IsDomain { get; init; }

    public bool IsRecipient { get; init; }

    public string ProfileAutomationId => "directory-profile-" + Shorten(ScopeId);

    public static RecipientRowView From(EntityDirectoryRow row, PausedSenders paused)
    {
        var kindLabel = row.Kind switch
        {
            "sender" => "Sending account",
            "company" => "Company",
            "domain" => "Domain",
            _ => "Person",
        };

        var profileKind = row.Kind switch
        {
            "company" => "company",
            "domain" => "domain",
            _ => "entity",
        };

        var label = string.IsNullOrWhiteSpace(row.DisplayLabel)
            ? Shorten(row.ScopeId)
            : row.DisplayLabel!;

        var direction = row.Direction switch
        {
            "Inbound" => "Writes to you.",
            "Outbound" => "You write to them.",
            _ => "Direction not recorded for this scope.",
        };

        var detailParts = new List<string> { kindLabel, direction };
        if (!string.IsNullOrWhiteSpace(row.CompanyName)) detailParts.Add(row.CompanyName!);
        else if (!string.IsNullOrWhiteSpace(row.CompanyId)) detailParts.Add("company " + Shorten(row.CompanyId));
        if (!string.IsNullOrWhiteSpace(row.Domain)) detailParts.Add(row.Domain!);

        var isSender = row.Kind == "sender";
        var pauseStateAvailable = isSender && paused.Available;
        var isPaused = pauseStateAvailable
            && paused.States.TryGetValue(row.ScopeId, out var value)
            && value;

        var warning = isPaused
            ? "Sending is paused."
            : isSender && !paused.Available
                ? "Pause state could not be read."
                : null;

        var evidence = row.Evidence;
        var evidenceUnavailable = evidence is null
            || new[] { evidence.Reply, evidence.Pressure, evidence.Identity }
                .Any(member => member is null
                    || string.Equals(member.Availability, "not_produced", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(member.Reason));

        var evidenceLabel = evidenceUnavailable
            ? "Open the profile for its evidence measures."
            : "Profile evidence is available in the profile.";

        return new RecipientRowView
        {
            ScopeId = row.ScopeId,
            ProfileKind = profileKind,
            CompanyId = row.CompanyId,
            Title = label,
            KindLabel = kindLabel,
            DirectionLabel = direction,
            Detail = string.Join(" · ", detailParts),
            VolumeLabel = DescribeVolume(row.Volume),
            LastContactLabel = DescribeLastContact(row),
            EvidenceLabel = evidenceLabel,
            WarningLabel = warning,
            IsPaused = isPaused,
            PauseStateAvailable = pauseStateAvailable,
            IsSender = isSender,
            IsRecipient = row.Kind is "recipient",
            IsCompany = row.Kind == "company",
            IsDomain = row.Kind == "domain",
        };
    }

    private static string DescribeVolume(EntityVolume? volume)
    {
        if (volume is null || !volume.Messages.HasValue)
        {
            return volume?.Reason is { Length: > 0 } reason
                ? "Volume not measured: " + reason + "."
                : "Volume not measured.";
        }

        var suffix = string.Equals(volume.Basis, "lifetime_profile_observations", StringComparison.OrdinalIgnoreCase)
            ? " (lifetime)"
            : string.Empty;

        return $"{volume.Messages.Value:N0} observation{(volume.Messages.Value == 1 ? string.Empty : "s")}{suffix}.";
    }

    private static string DescribeLastContact(EntityDirectoryRow row)
    {
        if (row.LastContactAt is { } last)
        {
            return "Last contact " + last.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture) + ".";
        }

        return row.LastContactReason is { Length: > 0 } reason
            ? "Last contact not recorded: " + reason + "."
            : "Last contact not recorded.";
    }

    private static string Shorten(string value)
        => value.Length <= 12 ? value : value[..10] + "...";
}

/// <summary>
/// The paused state joined from the senders listing, or a statement that the join was not read.
/// </summary>
public readonly record struct PausedSenders(bool Available, IReadOnlyDictionary<string, bool> States)
{
    public static PausedSenders NotApplicable => new(false, new Dictionary<string, bool>());
}
