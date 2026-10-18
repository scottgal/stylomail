using System.Net;
using System.Text;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.ViewModels;

namespace StyloMail.Desktop.Tests;

public sealed class ProxyConversationTests
{
    private const string WindowStart = "2026-10-17T20:00:00+00:00";
    private const string WindowEnd = "2026-10-18T20:00:00+00:00";
    private const string WindowStartLabel = "2026-10-17 20:00:00 UTC";
    private const string WindowEndLabel = "2026-10-18 20:00:00 UTC";

    private static readonly string[] MarkerKinds = ["AuthenticationFailed", "LinkDomainMismatch"];
    private static readonly int[] FirstIndex = [0];
    private static readonly int[] ThirdIndex = [2];
    private static readonly int[] FirstThreeIndices = [0, 1, 2];
    private static readonly int[] FirstTwoIndices = [0, 1];
    private static readonly double?[] AbsentToneScores = [0.4, null];
    private static readonly string[] OrderedObservationLabels =
    [
        "2026-10-17 20:00:00 UTC",
        "2026-10-17 21:00:00 UTC",
        "2026-10-17 22:00:00 UTC",
    ];

    // ============================== the wire ==============================

    [Fact]
    public async Task Conversation_request_uses_only_the_frozen_window_and_echoes_the_cursor()
    {
        var handler = StubHttpMessageHandler.ReturningJson(ConversationsJson("available", string.Empty));

        await Client(handler).GetConversationsAsync(25, "cursor_next-1");

        Assert.Equal(
            "/v1/proxy/conversations?window=last_24_hours&limit=25&after=cursor_next-1",
            handler.SingleRequest.PathAndQuery);
        Assert.Equal(TestApiKeyProvider.SampleKey, handler.SingleRequest.Header(StyloMailApiClient.ApiKeyHeaderName));
    }

    [Fact]
    public async Task A_page_size_outside_the_contract_is_clamped_rather_than_rejected()
    {
        var handler = StubHttpMessageHandler.ReturningJson(ConversationsJson("available", string.Empty));

        await Client(handler).GetConversationsAsync(limit: 5000);

        Assert.Equal("/v1/proxy/conversations?window=last_24_hours&limit=100", handler.SingleRequest.PathAndQuery);
    }

    [Fact]
    public async Task Signals_request_keeps_the_thread_token_as_one_opaque_path_segment()
    {
        const string token = "abcDEF012-_9";
        var handler = StubHttpMessageHandler.ReturningJson(SignalsJson(token, string.Empty));

        await Client(handler).GetSignalsAsync(token);

        Assert.Equal(
            "/v1/proxy/conversations/abcDEF012-_9/signals?window=last_24_hours",
            handler.SingleRequest.PathAndQuery);
    }

    [Theory]
    [InlineData("abcd", true)]
    [InlineData("abcDEF012-_9", true)]
    [InlineData("ab=", false)]
    [InlineData("a", false)]
    [InlineData("ab/cd", false)]
    [InlineData("ab+cd", false)]
    public void Token_shape_mirrors_the_hosts_unpadded_base64url_rule(string token, bool accepted)
        => Assert.Equal(accepted, ProxyConversationClient.IsThreadToken(token));

    [Fact]
    public async Task A_token_that_is_not_base64url_is_refused_before_anything_is_sent()
    {
        var handler = StubHttpMessageHandler.Unreachable();
        var client = Client(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetSignalsAsync("thread=1/../other"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_read_without_a_key_does_not_reach_the_network()
    {
        var handler = StubHttpMessageHandler.Unreachable();
        var client = new ProxyConversationClient(handler.CreateClient(), new TestApiKeyProvider(key: null));

        var failure = await Assert.ThrowsAsync<StyloMailApiException>(
            () => client.GetConversationsAsync());

        Assert.Equal(StyloMailApiFailure.ApiKeyNotConfigured, failure.Failure);
        Assert.Empty(handler.Requests);
    }

    // ============================== point rendering ==============================

    [Fact]
    public void An_available_point_with_zero_markers_reads_as_measured_clean()
    {
        var point = ProxySignalPointView.From(Point(
            at: DateTimeOffset.Parse("2026-10-17T21:00:00Z"),
            turn: "turn_one",
            tone: null,
            markerCount: 0));

        Assert.Equal("0 warnings, measured", point.MarkerCountLabel);
        Assert.True(point.WarningsAvailable);
        Assert.Empty(point.Markers);
        Assert.Null(point.ToneScore);
        Assert.Equal("tone not produced for this turn (absent, not zero)", point.ToneLabel);
    }

    [Theory]
    [InlineData("NoAnalyserConfigured", "no analyser is configured")]
    [InlineData("UnreadableMessage", "the message could not be read")]
    [InlineData("UnsupportedContent", "the content type is not supported")]
    [InlineData("SomethingNew", "reason SomethingNew")]
    public void A_not_produced_point_carries_its_reason_text(string reason, string expected)
    {
        var point = ProxySignalPointView.From(Point(
            at: DateTimeOffset.Parse("2026-10-17T21:00:00Z"),
            turn: "turn_one",
            tone: null,
            markerCount: 0,
            availability: "not_produced",
            reason: reason));

        Assert.False(point.WarningsAvailable);
        Assert.Contains(expected, point.MarkerCountLabel, StringComparison.Ordinal);
        Assert.Contains(expected, point.AvailabilityLabel, StringComparison.Ordinal);
        Assert.Contains(expected, point.ToneLabel, StringComparison.Ordinal);
        Assert.Contains(expected, point.ToneGapReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Warning_markers_render_the_kind_names_the_producer_sent()
    {
        var point = ProxySignalPointView.From(Point(
            at: DateTimeOffset.Parse("2026-10-17T21:00:00Z"),
            turn: "turn_one",
            tone: null,
            markerCount: 2,
            markers: ["AuthenticationFailed", "LinkDomainMismatch"]));

        Assert.Equal(MarkerKinds, point.Markers.Select(marker => marker.Kind));
        Assert.Contains("AuthenticationFailed", point.Markers[0].Label, StringComparison.Ordinal);
        Assert.Contains("LinkDomainMismatch", point.Markers[1].Label, StringComparison.Ordinal);
        Assert.Equal("2 warnings, measured", point.MarkerCountLabel);
    }

    // ============================== the tone series ==============================

    [Fact]
    public void A_gap_ends_the_segment_and_is_never_interpolated()
    {
        var gapped = ToneSeries.From(
        [
            PointView(0, 0.2),
            PointView(1, null),
            PointView(2, 0.8),
        ]);

        Assert.Equal(2, gapped.Segments.Count);
        Assert.Equal(FirstIndex, gapped.Segments[0].Indices);
        Assert.Equal(ThirdIndex, gapped.Segments[1].Indices);
        Assert.Equal(1, gapped.GapCount);
        Assert.DoesNotContain(gapped.Segments, segment => segment.Indices.Contains(0) && segment.Indices.Contains(2));

        // Control: without the gap the same three turns are one segment. This is what makes the
        // assertion above a measurement of the gap rather than of the series being empty.
        var contiguous = ToneSeries.From(
        [
            PointView(0, 0.2),
            PointView(1, 0.5),
            PointView(2, 0.8),
        ]);

        var only = Assert.Single(contiguous.Segments);
        Assert.Equal(FirstThreeIndices, only.Indices);
        Assert.Equal(0, contiguous.GapCount);
    }

    [Fact]
    public void An_absent_tone_is_null_and_a_measured_zero_is_zero()
    {
        var absent = ToneSeries.From([PointView(0, 0.4), PointView(1, null)]);

        Assert.Equal(AbsentToneScores, absent.Points.Select(point => point.Score));
        Assert.DoesNotContain(absent.Points, point => point.Score == 0d);
        Assert.Contains("not produced", absent.Points[1].GapReason, StringComparison.OrdinalIgnoreCase);

        // Control: a genuine zero is carried as zero, so the check above is not passing because
        // zero can never appear at all.
        var measuredZero = ToneSeries.From([PointView(0, 0.4), PointView(1, 0.0)]);
        Assert.Equal(0d, measuredZero.Points[1].Score);
        Assert.Equal(FirstTwoIndices, Assert.Single(measuredZero.Segments).Indices);
    }

    // ============================== the list view ==============================

    [Fact]
    public async Task The_list_names_its_window_in_utc_and_describes_each_row_without_content()
    {
        var handler = StubHttpMessageHandler.ReturningJson(ConversationsJson("available", RowJson("thread-token-1")));
        var model = new ProxyConversationListViewModel(Client(handler));

        await model.LoadAsync();

        var row = Assert.Single(model.Conversations);
        Assert.Contains("last_24_hours", model.ProvenanceLine, StringComparison.Ordinal);
        Assert.Contains(WindowStartLabel, model.ProvenanceLine, StringComparison.Ordinal);
        Assert.Contains(WindowEndLabel, model.ProvenanceLine, StringComparison.Ordinal);
        Assert.Contains("depth 2", row.Summary, StringComparison.Ordinal);
        Assert.Contains("2 participant(s)", row.Summary, StringComparison.Ordinal);
        Assert.Contains("participant token(s)", row.Detail, StringComparison.Ordinal);
        Assert.Contains("opaque", row.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("last activity 2026-10-17 21:00:00 UTC", row.LastActivityLabel, StringComparison.Ordinal);
        Assert.Equal("structural facts available", row.AvailabilityDetail);
    }

    [Fact]
    public async Task An_empty_available_page_is_not_an_error()
    {
        var handler = StubHttpMessageHandler.ReturningJson(ConversationsJson("available", string.Empty));
        var model = new ProxyConversationListViewModel(Client(handler));

        await model.LoadAsync();

        Assert.True(model.ShowEmpty);
        Assert.Empty(model.Conversations);
        Assert.Contains("not an error", model.StatusLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("last_24_hours", model.EmptyLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", model.StatusLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_not_produced_list_names_that_it_was_not_produced_and_claims_no_empty_mailbox()
    {
        var handler = StubHttpMessageHandler.ReturningJson(ConversationsJson("not_produced", string.Empty));
        var model = new ProxyConversationListViewModel(Client(handler));

        await model.LoadAsync();

        Assert.True(model.ShowEmpty);
        Assert.Contains("not produced", model.StatusLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a claim", model.EmptyLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("error", model.StatusLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_row_that_is_not_an_opaque_token_is_refused_instead_of_rendered()
    {
        const string address = "user@example.test";
        var handler = StubHttpMessageHandler.ReturningJson(
            ConversationsJson("available", RowJson("thread-token-1", participant: "\"" + address + "\"")));
        var model = new ProxyConversationListViewModel(Client(handler));

        await model.LoadAsync();

        Assert.Empty(model.Conversations);
        Assert.Contains("not an opaque token", model.StatusLabel, StringComparison.Ordinal);
        Assert.DoesNotContain(address, model.StatusLabel, StringComparison.Ordinal);
        Assert.DoesNotContain(address, model.ProvenanceLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_list_read_says_which_read_failed()
    {
        var handler = Refusing("The key lacks the review privilege.");
        var model = new ProxyConversationListViewModel(Client(handler));

        await model.LoadAsync();

        Assert.False(model.ShowEmpty);
        Assert.Contains("conversation list read failed", model.StatusLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("review privilege", model.StatusLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_thread_token_is_shortened_and_never_decoded_into_its_content()
    {
        const string secret = "user@example.test|Subject: Q3 payment change";
        var token = Base64Url(secret);
        var participant = Base64Url("raw.login@example.test");

        var row = ProxyConversationRowView.From(new ProxyConversationResponse
        {
            ThreadToken = token,
            AccountId = "acc_safe",
            ParticipantTokens = [participant],
            ReferenceDepth = 3,
            LastActivityAt = DateTimeOffset.Parse("2026-10-17T21:00:00Z"),
            Availability = "available",
        });

        var rendered = row.Summary + row.Detail + row.LastActivityLabel;

        Assert.Equal(token, row.ThreadToken);
        Assert.True(row.ShortThreadToken.Length < token.Length);
        Assert.StartsWith(token[..12], row.ShortThreadToken, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.test", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw.login@example.test", rendered, StringComparison.OrdinalIgnoreCase);

        // Control: the same search does find an address when one is actually present, so the
        // absences above are a property of the rendering rather than of the search.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => Assert.DoesNotContain("user@example.test", "user@example.test", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "depth not recorded")]
    [InlineData(0, "depth 0")]
    [InlineData(1, "depth 1")]
    public void Reference_depth_preserves_not_recorded_and_real_root_values(int? depth, string expected)
    {
        var row = ProxyConversationRowView.From(new ProxyConversationResponse
        {
            ThreadToken = "thread_safe",
            AccountId = "acc_safe",
            ParticipantTokens = [],
            ReferenceDepth = depth,
            LastActivityAt = DateTimeOffset.Parse("2026-10-17T21:00:00Z"),
            Availability = "available",
        });

        Assert.Contains(expected, row.Summary, StringComparison.Ordinal);
    }

    // ============================== the signals view ==============================

    [Fact]
    public async Task Signals_points_are_ordered_by_observation_time_even_when_the_wire_is_not()
    {
        var points = string.Join(',',
            PointJson("turn_three", "2026-10-17T22:00:00+00:00", "0.3", 0),
            PointJson("turn_one", "2026-10-17T20:00:00+00:00", "0.1", 0),
            PointJson("turn_two", "2026-10-17T21:00:00+00:00", "0.2", 0));
        var handler = StubHttpMessageHandler.ReturningJson(SignalsJson("thread-token-1", points));
        var model = new ProxyConversationSignalsViewModel(Client(handler));

        await model.LoadAsync("thread-token-1");

        Assert.Equal(OrderedObservationLabels, model.Points.Select(point => point.ObservedAtLabel));
        Assert.Equal(3, model.Tone.Segments.Single().Indices.Count);
    }

    [Fact]
    public async Task Signals_name_the_window_bound_and_source_and_render_a_gap_reason()
    {
        var points = string.Join(',',
            PointJson("turn_one", "2026-10-17T20:00:00+00:00", "0.4", 1, markers: "[\"AuthenticationFailed\"]"),
            PointJson("turn_two", "2026-10-17T21:00:00+00:00", null, 0),
            PointJson("turn_three", "2026-10-17T22:00:00+00:00", null, 0, availability: "not_produced", reason: "NoAnalyserConfigured"));
        var handler = StubHttpMessageHandler.ReturningJson(SignalsJson("thread-token-1", points));
        var model = new ProxyConversationSignalsViewModel(Client(handler));

        await model.LoadAsync("thread-token-1");

        Assert.Equal(3, model.Points.Count);
        Assert.Contains("last_24_hours", model.ProvenanceLine, StringComparison.Ordinal);
        Assert.Contains(WindowStartLabel, model.ProvenanceLine, StringComparison.Ordinal);
        Assert.Contains(WindowEndLabel, model.ProvenanceLine, StringComparison.Ordinal);
        Assert.Contains("proxy_threading_and_assessment", model.ProvenanceLine, StringComparison.Ordinal);

        Assert.Single(model.Tone.Segments);
        Assert.Contains("AuthenticationFailed", model.Points[0].Markers.Single().Kind, StringComparison.Ordinal);
        Assert.Contains("no analyser is configured", model.Points[2].MarkerCountLabel, StringComparison.Ordinal);
        Assert.Contains("not produced", model.ToneRangeLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never drawn through", model.ToneRangeLabel, StringComparison.OrdinalIgnoreCase);
        Assert.False(model.ShowEmpty);
    }

    [Fact]
    public void Before_a_thread_is_selected_there_is_no_tone_claim()
    {
        // The runtime capture found the unselected pane saying "No turns to plot in this window."
        // beside "Signals are not loaded.", which is a claim about a window nothing had read.
        var model = new ProxyConversationSignalsViewModel();

        Assert.Equal(string.Empty, model.ToneRangeLabel);
        Assert.False(model.HasToneSeries);
        Assert.Equal("No thread selected", model.ThreadLabel);
    }

    [Fact]
    public async Task An_empty_series_names_that_it_is_empty_and_is_not_an_error()
    {
        var handler = StubHttpMessageHandler.ReturningJson(SignalsJson("thread-token-1", string.Empty));
        var model = new ProxyConversationSignalsViewModel(Client(handler));

        await model.LoadAsync("thread-token-1");

        Assert.True(model.ShowEmpty);
        Assert.Empty(model.Points);
        Assert.Contains("not an error", model.StatusLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No turns to plot", model.ToneRangeLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_signals_read_names_the_read_and_the_thread()
    {
        var handler = Refusing("The key lacks the review privilege.");
        var model = new ProxyConversationSignalsViewModel(Client(handler));

        await model.LoadAsync("thread-token-1");

        Assert.Contains("signals read", model.StatusLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("thread-token", model.StatusLabel, StringComparison.Ordinal);
        Assert.Contains("review privilege", model.StatusLabel, StringComparison.Ordinal);
        Assert.Empty(model.Points);
    }

    [Fact]
    public async Task A_superseded_signal_response_never_applies_to_the_new_selection()
    {
        var firstRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = StubHttpMessageHandler.ReturningAsync(request =>
        {
            if (request.Path.EndsWith("thread-old/signals", StringComparison.Ordinal))
            {
                firstRequestStarted.TrySetResult();
                return oldResponse.Task;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SignalsJson("thread-new", PointJson("turn_new", "2026-10-17T20:00:00+00:00", "0.2", 0)), Encoding.UTF8, "application/json"),
            });
        });
        var model = new ProxyConversationSignalsViewModel(Client(handler));

        var oldRead = model.LoadAsync("thread-old");
        await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await model.LoadAsync("thread-new");
        oldResponse.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SignalsJson("thread-old", PointJson("turn_old", "2026-10-17T20:00:00+00:00", "0.1", 0)), Encoding.UTF8, "application/json"),
        });
        await oldRead;

        Assert.Equal("thread-new", model.ThreadToken);
        Assert.Single(model.Points);
        Assert.Contains("turn_new", model.Points.Single().Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("turn_old", model.Points.Single().Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signals_read_for_a_different_thread_is_refused_without_showing_points()
    {
        var handler = StubHttpMessageHandler.ReturningJson(SignalsJson(
            "thread-token-2",
            PointJson("turn_one", "2026-10-17T20:00:00+00:00", "0.1", 0)));
        var model = new ProxyConversationSignalsViewModel(Client(handler));

        await model.LoadAsync("thread-token-1");

        Assert.Empty(model.Points);
        Assert.Contains("different thread token", model.StatusLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_turn_token_that_is_not_opaque_is_refused_instead_of_rendered()
    {
        const string address = "sender@example.test";
        var handler = StubHttpMessageHandler.ReturningJson(SignalsJson(
            "thread-token-1",
            PointJson(address, "2026-10-17T20:00:00+00:00", null, 0)));
        var model = new ProxyConversationSignalsViewModel(Client(handler));

        await model.LoadAsync("thread-token-1");

        Assert.Empty(model.Points);
        Assert.Contains("not an opaque turn token", model.StatusLabel, StringComparison.Ordinal);
        Assert.DoesNotContain(address, model.StatusLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_selected_thread_is_reported_as_a_statement_not_a_failure()
    {
        var model = new ProxyConversationSignalsViewModel(Client(StubHttpMessageHandler.Unreachable()));

        await model.LoadAsync(" ");

        Assert.Contains("No thread is selected", model.StatusLabel, StringComparison.Ordinal);
        Assert.Empty(model.Points);
        Assert.Equal("No thread selected", model.ThreadLabel);
    }

    // ============================== fixtures ==============================

    private static ProxyConversationClient Client(StubHttpMessageHandler handler)
        => new(handler.CreateClient(), new TestApiKeyProvider());

    private static StubHttpMessageHandler Refusing(string detail)
        => new(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                "{\"error\":\"forbidden\",\"detail\":\"" + detail + "\"}",
                Encoding.UTF8,
                "application/json"),
        });

    private static string ConversationsJson(string availability, string conversations) =>
        $$"""
        {"tenantId":"tenant","window":"last_24_hours","windowStart":"{{WindowStart}}","windowEnd":"{{WindowEnd}}","availability":"{{availability}}","conversations":[{{conversations}}],"nextCursor":null,"hasMore":false}
        """;

    private static string RowJson(
        string threadToken,
        string participant = "\"participant_one\",\"participant_two\"",
        string availability = "available") =>
        $$"""
        {"threadToken":"{{threadToken}}","accountId":"acc_safe","participantTokens":[{{participant}}],"referenceDepth":2,"lastActivityAt":"2026-10-17T21:00:00+00:00","availability":"{{availability}}"}
        """;

    private static string SignalsJson(string threadToken, string points) =>
        $$"""
        {"tenantId":"tenant","threadToken":"{{threadToken}}","window":"last_24_hours","windowStart":"{{WindowStart}}","windowEnd":"{{WindowEnd}}","provenance":"proxy_threading_and_assessment","points":[{{points}}]}
        """;

    private static string PointJson(
        string turnToken,
        string observedAt,
        string? toneScore,
        int markerCount,
        string availability = "available",
        string reason = "None",
        string markers = "[]") =>
        $$"""
        {"observedAt":"{{observedAt}}","turnToken":"{{turnToken}}","toneScore":{{(toneScore ?? "null")}},"warningMarkers":{{markers}},"dimensionIds":[],"availability":"{{availability}}","availabilityReason":"{{reason}}","markerCount":{{markerCount}}}
        """;

    private static ProxySignalPointResponse Point(
        DateTimeOffset at,
        string turn,
        double? tone,
        int markerCount,
        string availability = "available",
        string reason = "None",
        string[]? markers = null) => new()
    {
        ObservedAt = at,
        TurnToken = turn,
        ToneScore = tone,
        WarningMarkers = markers ?? [],
        DimensionIds = [],
        Availability = availability,
        AvailabilityReason = reason,
        MarkerCount = markerCount,
    };

    private static ProxySignalPointView PointView(int minute, double? tone) =>
        ProxySignalPointView.From(Point(
            at: new DateTimeOffset(2026, 10, 17, 20, minute, 0, TimeSpan.Zero),
            turn: "turn_" + minute.ToString(System.Globalization.CultureInfo.InvariantCulture),
            tone: tone,
            markerCount: 0));

    private static string Base64Url(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
