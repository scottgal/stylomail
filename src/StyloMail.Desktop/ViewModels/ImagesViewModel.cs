using System.Collections.ObjectModel;
using System.Globalization;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.ViewModels;

/// <summary>
/// Image observations for a conversation. It never renders or fetches image bytes.
/// </summary>
public sealed class ImagesViewModel : ObservableObject
{
    private ProxyConversationClient? _client;
    private bool _isLoading;
    private string _statusLabel = "Image observations have not been read yet.";
    private string _conversationNote = "Conversations have not been read yet.";
    private string _windowLabel = string.Empty;
    private string _provenanceLabel = string.Empty;
    private string _retentionLabel = string.Empty;
    private ImageConversationRow? _selectedConversation;

    public ImagesViewModel(ProxyConversationClient? client = null) => _client = client;

    public void SetClient(ProxyConversationClient? client) => _client = client;

    public ObservableCollection<ImageConversationRow> Conversations { get; } = [];

    public ObservableCollection<ImageMessageView> Messages { get; } = [];

    public ObservableCollection<string> TechnicalLines { get; } = [];

    public bool HasConversations => Conversations.Count > 0;

    public bool HasMessages => Messages.Count > 0;

    public bool ShowEmpty => !IsLoading && !HasMessages;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (Set(ref _isLoading, value)) Raise(nameof(ShowEmpty));
        }
    }

    public string StatusLabel
    {
        get => _statusLabel;
        private set => Set(ref _statusLabel, value);
    }

    public string ConversationNote
    {
        get => _conversationNote;
        private set => Set(ref _conversationNote, value);
    }

    public string WindowLabel
    {
        get => _windowLabel;
        private set => Set(ref _windowLabel, value);
    }

    public string ProvenanceLabel
    {
        get => _provenanceLabel;
        private set => Set(ref _provenanceLabel, value);
    }

    public string RetentionLabel
    {
        get => _retentionLabel;
        private set => Set(ref _retentionLabel, value);
    }

    public ImageConversationRow? SelectedConversation
    {
        get => _selectedConversation;
        set => Set(ref _selectedConversation, value);
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            Conversations.Clear();
            Messages.Clear();
            TechnicalLines.Clear();

            if (_client is null)
            {
                StatusLabel = "Image observations are unavailable until the console is connected to a Host.";
                ConversationNote = "No Host is connected.";
                return;
            }

            var listing = await _client.GetConversationsAsync(limit: 50, cancellationToken: cancellationToken).ConfigureAwait(true);

            if (string.Equals(listing.Availability, "not_produced", StringComparison.OrdinalIgnoreCase))
            {
                ConversationNote = "This Host does not record conversation threads yet.";
                StatusLabel = "No conversation is available for image inspection.";
                return;
            }

            foreach (var conversation in listing.Conversations)
            {
                Conversations.Add(ImageConversationRow.From(conversation));
            }

            ConversationNote = Conversations.Count == 0
                ? "No conversations were active in the last 24 hours."
                : $"{Conversations.Count:N0} conversation{(Conversations.Count == 1 ? string.Empty : "s")} in the last 24 hours.";

            if (Conversations.Count > 0)
            {
                await SelectAsync(Conversations[0], cancellationToken).ConfigureAwait(true);
            }
            else
            {
                StatusLabel = "Select a conversation to see its image observations.";
            }
        }
        catch (Exception ex) when (ex is StyloMailApiException or HttpRequestException)
        {
            StatusLabel = "Conversations could not be read. " + Reason(ex);
        }
        finally
        {
            IsLoading = false;
            Raise(nameof(HasConversations));
            Raise(nameof(HasMessages));
            Raise(nameof(ShowEmpty));
        }
    }

    public async Task SelectAsync(ImageConversationRow row, CancellationToken cancellationToken = default)
    {
        SelectedConversation = row;
        Messages.Clear();
        TechnicalLines.Clear();

        if (_client is null) return;

        IsLoading = true;

        try
        {
            var images = await _client.GetImagesAsync(row.ThreadToken, cancellationToken).ConfigureAwait(true);
            WindowLabel = "Window: " + images.WindowStart.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture)
                + " to " + images.WindowEnd.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture) + ".";
            ProvenanceLabel = "Source: " + images.Provenance + ".";
            RetentionLabel = $"Observations are retained for {images.RetentionDays:N0} days.";

            if (!images.ImagesEnabled)
            {
                StatusLabel = "Image inspection is off on this Host.";
                TechnicalLines.Add($"Thread {row.ThreadToken}: images enabled false; retention {images.RetentionDays} days; provenance {images.Provenance}.");
                return;
            }

            foreach (var message in images.Messages)
            {
                var view = ImageMessageView.From(message);
                Messages.Add(view);
                TechnicalLines.Add(view.TechnicalDetail);
            }

            StatusLabel = Messages.Count == 0
                ? "No images were observed in this conversation in the last 24 hours."
                : $"{Messages.Count:N0} message{(Messages.Count == 1 ? " with" : "s with")} image observations.";
        }
        catch (Exception ex) when (ex is StyloMailApiException or HttpRequestException)
        {
            StatusLabel = "Image observations could not be read. " + Reason(ex);
        }
        finally
        {
            IsLoading = false;
            Raise(nameof(HasMessages));
            Raise(nameof(ShowEmpty));
        }
    }

    private static string Reason(Exception ex) => ex is StyloMailApiException api
        ? api.Detail ?? api.Code ?? api.Message
        : "The Host could not be reached.";
}

public sealed record ImageConversationRow(string ThreadToken, string Reference, string LastActivity)
{
    public static ImageConversationRow From(ProxyConversationResponse conversation)
        => new(
            conversation.ThreadToken,
            conversation.ThreadToken.Length <= 10 ? conversation.ThreadToken : conversation.ThreadToken[..8] + "...",
            "Last active " + conversation.LastActivityAt.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture) + ".");
}

public sealed record ImageMessageView
{
    public required string Title { get; init; }
    public required string ObservedLabel { get; init; }
    public required string StateLabel { get; init; }
    public required string TrafficLabel { get; init; }
    public required string CountsLabel { get; init; }
    public required IReadOnlyList<ImageObservationView> Observations { get; init; }
    public required string TechnicalDetail { get; init; }

    public static ImageMessageView From(ImageMessageRow message)
    {
        var counts = message.Observations.Select(observation => observation.Counts).FirstOrDefault(value => value is not null);

        return new ImageMessageView
        {
            Title = "Message " + Shorten(message.MessageToken),
            ObservedLabel = "Observed " + message.ObservedAt.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture) + ".",
            StateLabel = StateLabelFor(message.State),
            TrafficLabel = "Traffic: " + Humanize(message.TrafficKind) + ".",
            CountsLabel = counts is null
                ? "Counts not reported."
                : $"{counts.ImageParts} image parts, {counts.ImagesProduced} produced, {counts.ImagesNotProduced} not produced, {counts.ImagesOverBudget} over budget.",
            Observations = [.. message.Observations.Select(ImageObservationView.From)],
            TechnicalDetail =
                $"Message {message.MessageToken}: account {message.AccountId}; traffic {message.TrafficKind}; state {message.State}; "
                + $"observations {message.Observations.Count}; overflow {message.ChildrenOverflowCount}; children cursor {message.ChildrenNext ?? "none"}.",
        };
    }

    private static string StateLabelFor(string state) => state switch
    {
        "disabled" => "Inspection off",
        "no_observation" => "No observation",
        "produced" or "available" => "Inspected",
        "failed" => "Inspection failed",
        _ => Humanize(state),
    };

    private static string Shorten(string value)
        => value.Length <= 12 ? value : value[..10] + "...";

    private static string Humanize(string value)
        => string.Join(' ', value.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]));
}

public sealed record ImageObservationView
{
    public required string Title { get; init; }
    public required string StateLabel { get; init; }
    public required string AvailabilityLabel { get; init; }
    public required IReadOnlyList<ImagePartView> Parts { get; init; }

    public static ImageObservationView From(ImageObservationRow observation)
        => new()
        {
            Title = "Observation " + observation.ObservationKey,
            StateLabel = observation.State,
            AvailabilityLabel = observation.AvailabilityReason is { Length: > 0 } reason
                ? "Not inspected: " + Humanize(reason) + "."
                : "Availability: " + (observation.Availability ?? "not reported") + ".",
            Parts = [.. observation.Parts.Select(ImagePartView.From)],
        };

    private static string Humanize(string value)
        => string.Join(' ', value.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]));
}

public sealed record ImagePartView
{
    public required string Title { get; init; }
    public required string Detail { get; init; }

    public static ImagePartView From(ImagePartRow part)
    {
        var dimensions = part.Width is { } width && part.Height is { } height
            ? $"{width}x{height}"
            : "dimensions not reported";

        var declared = string.IsNullOrWhiteSpace(part.DeclaredFormat)
            ? string.Empty
            : $" Declared {part.DeclaredFormat}.";
        var detected = string.IsNullOrWhiteSpace(part.Format)
            ? "Actual type not reported."
            : $" Detected {part.Format}.";
        var trailing = part.TrailingByteCount is { } trailingBytes
            ? $" Trailing bytes {trailingBytes}."
            : string.Empty;
        var findings = part.Findings.Count == 0
            ? "No findings."
            : string.Join(" ", part.Findings.Select(finding => finding.Code ?? "finding"));

        return new ImagePartView
        {
            Title = "Part " + part.Ordinal + " · " + (part.Format ?? "type not reported"),
            Detail = dimensions + declared + detected + trailing + " " + findings,
        };
    }
}
