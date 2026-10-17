using System.Collections.ObjectModel;
using System.Globalization;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.ViewModels;

/// <summary>
/// The conversation list: structural thread rows for one named window.
/// </summary>
/// <remarks>
/// <b>This screen is not a mailbox.</b> Every value it renders comes from the frozen conversation
/// contract, which carries opaque thread and participant tokens rather than subjects, addresses or
/// content. The view model therefore has no decoding step at all: a token is shortened for display
/// and passed back unchanged when a thread is selected. If a row ever carried something that is not
/// a token, the page is refused rather than rendered, because the one thing this screen must never
/// do is put a login or an address on a screen.
///
/// <para>
/// The window is named and its UTC bounds are part of the screen, so an empty list can be read as
/// "nothing happened in this window" rather than "this feature is broken".
/// </para>
/// </remarks>
public sealed class ProxyConversationListViewModel : ObservableObject
{
    /// <summary>Requested page size. The Host remains free to clamp it.</summary>
    public const int PageSize = 50;

    private readonly ProxyConversationClient? _client;
    private bool _isLoading;
    private bool _hasLoaded;
    private bool _hasMore;
    private bool _failed;
    private string? _nextCursor;
    private string _statusLabel = "Conversations are not loaded.";
    private string _provenanceLine = "No window has been read yet.";
    private string _emptyLabel = string.Empty;
    private ProxyConversationRowView? _selected;

    public ProxyConversationListViewModel(ProxyConversationClient? client = null) => _client = client;

    /// <summary>
    /// Raised when the operator selects a row. The page hosting this view model uses it to load the
    /// selected thread's signals; nothing else is implied by a selection.
    /// </summary>
    public event EventHandler<ProxyConversationRowView?>? ConversationSelected;

    public ObservableCollection<ProxyConversationRowView> Conversations { get; } = [];

    public ProxyConversationRowView? SelectedConversation
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                ConversationSelected?.Invoke(this, value);
            }
        }
    }

    public bool HasLoaded => _hasLoaded;

    public bool HasConversations => Conversations.Count > 0;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (Set(ref _isLoading, value))
            {
                Raise(nameof(ShowLoadMoreButton));
            }
        }
    }

    /// <summary>Empty is a readable answer, so this is only true for a successful empty page.</summary>
    public bool ShowEmpty => _hasLoaded && !_failed && Conversations.Count == 0;

    public bool ShowLoadMoreButton => !_isLoading && !_failed && _hasMore;

    /// <summary>The named window with its UTC bounds, plus the row availability for that read.</summary>
    public string ProvenanceLine
    {
        get => _provenanceLine;
        private set => Set(ref _provenanceLine, value);
    }

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

    /// <summary>
    /// Reads the first page, or the next one when <paramref name="append"/> is true.
    /// </summary>
    /// <remarks>
    /// Expected failures become a sentence rather than an exception, because this screen has to
    /// render something in every one of them and an exception in a UI event handler renders
    /// nothing. Cancellation still propagates: a cancelled read produced no answer about the Host.
    /// </remarks>
    public async Task LoadAsync(bool append = false, CancellationToken cancellationToken = default)
    {
        if (_isLoading) return;

        if (_client is null)
        {
            Fail("The conversation list read is unavailable until the console is connected to a Host.");
            return;
        }

        if (append && string.IsNullOrEmpty(_nextCursor))
        {
            StatusLabel = "There is no further page to read.";
            return;
        }

        IsLoading = true;
        StatusLabel = append
            ? "Reading the next page of conversations for " + ProxyConversationClient.Last24Hours + "..."
            : "Reading conversations for " + ProxyConversationClient.Last24Hours + "...";

        try
        {
            var response = await _client.GetConversationsAsync(
                PageSize,
                append ? _nextCursor : null,
                cancellationToken);

            Apply(response, append);
        }
        catch (StyloMailApiException error)
        {
            Fail("The conversation list read failed: " + ProxyConversationText.Describe(error) + ".");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Apply(ProxyConversationsResponse response, bool append)
    {
        if (response.HasMore && string.IsNullOrEmpty(response.NextCursor))
        {
            Fail("The conversation list read returned more pages without a continuation cursor. No page was shown.");
            return;
        }

        if (!AllTokensAreOpaque(response.Conversations))
        {
            Fail("The conversation list read returned a value that is not an opaque token. No page was shown, because this screen does not render subjects, addresses or logins.");
            return;
        }

        if (!append)
        {
            Conversations.Clear();
        }

        var seen = new HashSet<string>(Conversations.Select(row => row.ThreadToken), StringComparer.Ordinal);
        foreach (var conversation in response.Conversations)
        {
            if (seen.Add(conversation.ThreadToken))
            {
                Conversations.Add(ProxyConversationRowView.From(conversation));
            }
        }

        _nextCursor = response.NextCursor;
        _hasMore = response.HasMore && !string.IsNullOrEmpty(response.NextCursor);
        _hasLoaded = true;
        _failed = false;
        var available = ProxyConversationText.IsAvailable(response.Availability);

        ProvenanceLine = ProxyConversationText.WindowLine(
            response.Window, response.WindowStart, response.WindowEnd, response.Availability);

        // An empty window and an unproduced telemetry read are different facts, and neither is an
        // error. The reason text is what keeps a blank pane from reading as a broken one.
        if (!available)
        {
            EmptyLabel = "Conversation telemetry was not produced for " + response.Window
                + ". An empty list here is not a claim that no conversations exist.";
            StatusLabel = EmptyLabel;
        }
        else if (Conversations.Count == 0)
        {
            EmptyLabel = "No structural conversations were recorded in " + response.Window
                + ". An empty conversation list is not an error.";
            StatusLabel = EmptyLabel;
        }
        else
        {
            EmptyLabel = string.Empty;
            StatusLabel = "Showing " + Conversations.Count.ToString(CultureInfo.InvariantCulture)
                + " structural conversation(s) in " + response.Window
                + ". Tokens only: no subject, address, login or message content is rendered.";
        }

        Raise(nameof(HasConversations));
        Raise(nameof(ShowEmpty));
        Raise(nameof(ShowLoadMoreButton));
        Raise(nameof(HasLoaded));
    }

    private void Fail(string message)
    {
        _failed = true;
        StatusLabel = message;
        Raise(nameof(ShowEmpty));
        Raise(nameof(ShowLoadMoreButton));
    }

    private static bool AllTokensAreOpaque(IReadOnlyList<ProxyConversationResponse> conversations)
    {
        foreach (var conversation in conversations)
        {
            if (!ProxyConversationClient.IsThreadToken(conversation.ThreadToken))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(conversation.AccountId)
                && !ProxyConversationClient.IsOpaqueToken(conversation.AccountId))
            {
                return false;
            }

            foreach (var participant in conversation.ParticipantTokens)
            {
                if (!ProxyConversationClient.IsOpaqueToken(participant))
                {
                    return false;
                }
            }
        }

        return true;
    }
}

/// <summary>One structural thread row, rendered without resolving anything.</summary>
public sealed class ProxyConversationRowView
{
    /// <summary>The opaque token used to read this thread's signals. Never displayed in full.</summary>
    public required string ThreadToken { get; init; }

    public required string ShortThreadToken { get; init; }

    public required string Summary { get; init; }

    public required string Detail { get; init; }

    public required string AvailabilityDetail { get; init; }

    public required string LastActivityLabel { get; init; }

    public static ProxyConversationRowView From(ProxyConversationResponse conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var participants = conversation.ParticipantTokens.Count == 0
            ? "no participant tokens recorded"
            : conversation.ParticipantTokens.Count.ToString(CultureInfo.InvariantCulture)
                + " participant token(s) (opaque): "
                + string.Join(", ", conversation.ParticipantTokens.Select(ProxyConversationText.ShortToken));

        var account = string.IsNullOrEmpty(conversation.AccountId)
            ? string.Empty
            : "; account token " + ProxyConversationText.ShortToken(conversation.AccountId);

        return new ProxyConversationRowView
        {
            ThreadToken = conversation.ThreadToken,
            ShortThreadToken = ProxyConversationText.ShortToken(conversation.ThreadToken),
            Summary = ProxyConversationText.ShortToken(conversation.ThreadToken)
                + " · depth " + (conversation.ReferenceDepth?.ToString(CultureInfo.InvariantCulture) ?? "not recorded")
                + " · " + conversation.ParticipantTokens.Count.ToString(CultureInfo.InvariantCulture)
                + " participant(s)",
            Detail = participants + account,
            AvailabilityDetail = ProxyConversationText.IsAvailable(conversation.Availability)
                ? "structural facts available"
                : "structural facts not produced",
            LastActivityLabel = "last activity " + ProxyConversationText.Utc(conversation.LastActivityAt),
        };
    }
}

/// <summary>
/// Shared rendering vocabulary for the two proxy conversation screens.
/// </summary>
/// <remarks>
/// One place for the words, so the list and the signals view cannot describe the same availability
/// two different ways. Everything here is presentation only: nothing decodes a token or computes a
/// measurement.
/// </remarks>
internal static class ProxyConversationText
{
    internal const string NotProduced = "not_produced";

    internal static bool IsAvailable(string? availability)
        => !string.Equals(availability, NotProduced, StringComparison.OrdinalIgnoreCase);

    internal static string ShortToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return "none";
        return token.Length <= 12 ? token : token[..12] + "...";
    }

    internal static string Utc(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    internal static string WindowLine(
        string window,
        DateTimeOffset start,
        DateTimeOffset end,
        string? availability = null)
    {
        var line = "window " + window + ", " + Utc(start) + " to " + Utc(end);
        return availability is null
            ? line
            : line + ". Availability: " + (IsAvailable(availability) ? "available" : "not produced") + ".";
    }

    /// <summary>
    /// The closed availability-reason vocabulary, in operator language.
    /// </summary>
    /// <remarks>
    /// An unknown reason is preserved rather than mapped to something plausible. A reason this
    /// build does not know is still the Host's answer, and replacing it with "unavailable" would
    /// lose the only fact the operator could take to the Host's owner.
    /// </remarks>
    internal static string ReasonText(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || string.Equals(reason, "None", StringComparison.Ordinal))
        {
            return "no reason was recorded";
        }

        return reason switch
        {
            "NoAnalyserConfigured" => "no analyser is configured",
            "UnreadableMessage" => "the message could not be read",
            "UnsupportedContent" => "the content type is not supported",
            _ => "reason " + reason,
        };
    }

    internal static string Describe(StyloMailApiException error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (!string.IsNullOrWhiteSpace(error.Detail)) return error.Detail;
        if (!string.IsNullOrWhiteSpace(error.Code)) return error.Code;

        return error.Failure switch
        {
            StyloMailApiFailure.ApiKeyNotConfigured => "no API key is configured",
            StyloMailApiFailure.HostRefused => "the Host refused the read",
            StyloMailApiFailure.Unreachable => "the Host could not be reached",
            StyloMailApiFailure.UnreadableResponse => "the Host's answer could not be read by this build",
            _ => "the read failed",
        };
    }
}
