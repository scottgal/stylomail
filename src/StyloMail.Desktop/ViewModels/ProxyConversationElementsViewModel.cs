using System.Collections.ObjectModel;
using System.Globalization;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.ViewModels;

/// <summary>Content-free, per-segment element measurements for the selected conversation.</summary>
public sealed class ProxyConversationElementsViewModel : ObservableObject
{
    private static readonly HashSet<string> CurrentElements = new(StringComparer.Ordinal)
    {
        "credential_request",
        "urgency_pressure",
        "threat",
    };
    private readonly ProxyConversationClient? _client;
    private long _generation;
    private CancellationTokenSource? _cancellation;
    private bool _isLoading;
    private string _threadToken = string.Empty;
    private string _statusLabel = "Element signals are not loaded.";
    private string _provenanceLine = "No window has been read yet.";

    public ProxyConversationElementsViewModel(ProxyConversationClient? client = null) => _client = client;
    public ObservableCollection<ProxyElementPointView> Points { get; } = [];
    public string ThreadToken => _threadToken;
    public string ThreadLabel => string.IsNullOrEmpty(_threadToken) ? "No thread selected" : "Thread " + ProxyConversationText.ShortToken(_threadToken) + " (opaque token)";
    public string StatusLabel { get => _statusLabel; private set => Set(ref _statusLabel, value); }
    public string ProvenanceLine { get => _provenanceLine; private set => Set(ref _provenanceLine, value); }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    public async Task LoadAsync(string? threadToken, CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _generation);
        _cancellation?.Cancel();
        _cancellation = null;
        _threadToken = threadToken ?? string.Empty;
        Raise(nameof(ThreadToken)); Raise(nameof(ThreadLabel));
        Points.Clear();

        if (string.IsNullOrWhiteSpace(threadToken)) { StatusLabel = "No thread is selected, so no element read was attempted."; return; }
        if (_client is null) { StatusLabel = "The element read is unavailable until the console is connected to a Host."; return; }

        IsLoading = true;
        StatusLabel = "Reading element signals for thread " + ProxyConversationText.ShortToken(threadToken) + "...";
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellation = requestCancellation;
        try
        {
            var response = await _client.GetElementsAsync(threadToken, requestCancellation.Token);
            if (!IsCurrent(generation)) return;
            if (!string.Equals(response.ThreadToken, threadToken, StringComparison.Ordinal))
            {
                StatusLabel = "The element read returned a different thread token. No points were shown.";
                return;
            }
            if (!response.Points.All(point => ProxyConversationClient.IsOpaqueToken(point.TurnToken)))
            {
                StatusLabel = "The element read returned a non-opaque turn token. No points were shown.";
                return;
            }
            var orderedPoints = response.Points.OrderBy(x => x.ObservedAt).ThenBy(x => x.TrafficKind, StringComparer.Ordinal).ThenBy(x => x.Ordinal).ToArray();
            if (!orderedPoints.All(ValidateVocabulary))
            {
                StatusLabel = "The element read contained a label or vocabulary version that this console cannot safely compare. No points were shown.";
                return;
            }
            foreach (var point in orderedPoints) Points.Add(ProxyElementPointView.From(point));
            ProvenanceLine = ProxyConversationText.WindowLine(response.Window, response.WindowStart, response.WindowEnd) + " Source: " + response.Provenance + ".";
            StatusLabel = Points.Count == 0 ? "No element points were recorded for this thread in " + response.Window + "." : "Showing " + Points.Count.ToString(CultureInfo.InvariantCulture) + " separate segment point(s); retrieval and submission are never blended.";
        }
        catch (OperationCanceledException) when (!IsCurrent(generation)) { }
        catch (OperationCanceledException) { StatusLabel = "The element read was cancelled before the Host responded."; }
        catch (StyloMailApiException error) when (IsCurrent(generation)) { StatusLabel = "The element read failed: " + ProxyConversationText.Describe(error) + "."; }
        finally { if (IsCurrent(generation)) { IsLoading = false; _cancellation = null; } }
    }

    private bool IsCurrent(long generation) => generation == Volatile.Read(ref _generation);

    private static bool ValidateVocabulary(ProxyElementPointResponse point)
        => point.Elements.All(score => score.VocabularyVersion switch
        {
            "elements/2" => CurrentElements.Contains(score.Element),
            "elements/1" => true,
            _ => false,
        });
}

public sealed class ProxyElementPointView
{
    public required string Summary { get; init; }
    public required string VocabularyLabel { get; init; }
    public required string ToneLabel { get; init; }
    public required IReadOnlyList<ProxyElementScoreView> Elements { get; init; }
    public static ProxyElementPointView From(ProxyElementPointResponse point) => new()
    {
        Summary = point.TrafficKind + " · segment " + point.Ordinal.ToString(CultureInfo.InvariantCulture) + " · " + ProxyConversationText.Utc(point.ObservedAt) + " · turn " + ProxyConversationText.ShortToken(point.TurnToken),
        VocabularyLabel = VocabularyLine(point.Elements),
        ToneLabel = ProxyConversationText.IsAvailable(point.Tone.Availability)
            ? "Tone is available from its separate scorer."
            : "Tone not produced: " + point.Tone.AvailabilityReason + ".",
        Elements = point.Elements.Select(ProxyElementScoreView.From).ToArray(),
    };

    private static string VocabularyLine(IReadOnlyList<ProxyElementScoreResponse> elements)
    {
        var versions = elements.Select(score => score.VocabularyVersion).Distinct(StringComparer.Ordinal).ToArray();
        if (versions.Length == 0) return "Vocabulary: no element scores were recorded.";
        var version = string.Join(", ", versions);
        return versions.All(value => string.Equals(value, "elements/1", StringComparison.Ordinal))
            ? "Vocabulary: " + version + " · historical read-only data; not comparable with elements/2."
            : "Vocabulary: " + version + ".";
    }
}

public sealed class ProxyElementScoreView
{
    public required string ElementLabel { get; init; }
    public required string ScorerLabel { get; init; }
    public required string ScoreLabel { get; init; }
    public required string AvailabilityLabel { get; init; }
    public required string ExemplarLabel { get; init; }
    public bool HasExemplar => !string.IsNullOrEmpty(ExemplarLabel);
    public static ProxyElementScoreView From(ProxyElementScoreResponse score)
    {
        var available = ProxyConversationText.IsAvailable(score.Availability);
        var isKnn = string.Equals(score.Scorer, "prototype_knn", StringComparison.Ordinal);
        var scoreLabel = !available || !score.Score.HasValue
            ? "Not produced; no numeric score is implied."
            : isKnn
                ? "Signed kNN margin: " + score.Score.Value.ToString("0.###", CultureInfo.InvariantCulture) + (score.Score.Value > 0 ? " (toward threatening exemplars)." : score.Score.Value < 0 ? " (toward benign exemplars)." : " (equidistant).")
                : "Probe probability: " + score.Score.Value.ToString("0.###", CultureInfo.InvariantCulture) + ".";
        return new()
        {
            ElementLabel = score.Element + " · " + score.VocabularyVersion,
            ScorerLabel = (isKnn ? "prototype kNN" : "linear probe") + " · " + score.ScorerVersion,
            ScoreLabel = scoreLabel,
            AvailabilityLabel = available ? "Available." : DescribeAvailability(score.AvailabilityReason),
            ExemplarLabel = string.IsNullOrWhiteSpace(score.NearestExemplarId) ? string.Empty : "Nearest exemplar (opaque id): " + ProxyConversationText.ShortToken(score.NearestExemplarId),
        };
    }

    private static string DescribeAvailability(string reason) => reason switch
    {
        "insufficient_exemplars" => "Not produced: insufficient exemplars; this element is in the vocabulary but has too few positive exemplars to score.",
        _ => "Not produced: " + reason + ".",
    };
}
