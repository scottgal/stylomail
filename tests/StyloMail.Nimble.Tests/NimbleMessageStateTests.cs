using System.Net;
using System.Text;
using System.Text.Json;
using StyloMail.Core;
using StyloMail.Jev;

namespace StyloMail.Nimble.Tests;

public sealed class NimbleMessageStateTests
{
    /// <summary>
    /// The drift alarm.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This adapter copies the hosted adapter's message state so the two can be compared on one
    /// corpus. A copy drifts, and a comparison between two adapters that describe a message
    /// differently is a comparison of the descriptions rather than of the models.
    /// </para>
    /// <para>
    /// So the assertion is not that the code matches, it is that <b>the state each classifier
    /// actually puts on the wire</b> is equal for the same input. It drives both through a stub
    /// transport and compares what they sent. If the hosted state gains, loses or renames a field,
    /// this goes red and points at <c>NimbleMessageState</c>.
    /// </para>
    /// <para>
    /// A red here is not necessarily a bug in this lane. It means the two providers have stopped
    /// describing the same message, and that is a decision someone has to make rather than a test to
    /// loosen.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Sends_exactly_the_state_the_hosted_adapter_sends()
    {
        var input = NimbleTestMessage.With(m => m with
        {
            Subject = "Re: quarterly figures",
            BodyText = "Here are the numbers you asked for.",
            QuotedText = "> can you send the figures?",
            Links =
            [
                new LinkObservation
                {
                    DisplayedText = "portal",
                    ActualTarget = "https://evil.example.test/login",
                    UnicodeHost = "eviI.example.test",
                    AsciiHost = "xn--evil-xyz.example.test",
                },
            ],
            Attachments =
            [
                new AttachmentMetadata
                {
                    FileName = "figures.pdf.exe",
                    DeclaredContentType = "application/pdf",
                    ExtensionImpliedContentType = "application/x-msdownload",
                    SizeBytes = 91_233,
                    ContentUnavailable = true,
                },
            ],
            ConversationContext = ["Re: quarterly figures", "Please send the numbers."],
        }) with
        {
            TaggedContext = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["relationship"] = "established-correspondent",
            },
            Profile = Profile(),
        };

        var hostedHandler = new RecordingHandler((_, _) => Json(HttpStatusCode.OK, "{}"));
        var hosted = new JevSemanticMailClassifier(
            new HttpClient(hostedHandler),
            new JevOptions { ApiKey = "placeholder-so-the-state-is-built-never-a-real-key" });
        await hosted.ClassifyAsync(input, CancellationToken.None);

        var localHandler = new RecordingHandler((_, _) =>
            NimbleTestDoubles.Ok(SemanticDimensions.All, NimbleTestDoubles.AllAffirmative));
        await NimbleTestDoubles.Create(localHandler).ClassifyAsync(input, CancellationToken.None);

        // `state` on this adapter's body since the SystemOne migration; it was `prompt` under
        // nimble-request-shape/1. Same document, same JSON text, a different member name on a
        // different transport, and the comparison below is unaffected because it reads the text
        // rather than the member that carried it.
        var hostedState = Property(hostedHandler.LastBody, "state");
        var localState = JsonDocument.Parse(
            Property(localHandler.LastBody, "state").GetString()!).RootElement;

        Assert.Equal(Canonical(hostedState), Canonical(localState));
    }

    [Fact]
    public void States_that_no_behavioural_context_was_available_rather_than_omitting_it()
    {
        var state = NimbleMessageState.Build(
            NimbleTestMessage.Input().Message, null, profile: null, 2_500, 40, 20).State;

        var behaviour = (Dictionary<string, object?>)state["sender_behaviour"]!;

        // A model that does not know it is uninformed answers as confidently as one that is.
        Assert.Equal(false, behaviour["available"]);
        Assert.NotNull(behaviour["note"]);
    }

    [Fact]
    public void Bounds_links_and_attachments()
    {
        var links = Enumerable.Range(0, 50)
            .Select(i => new LinkObservation
            {
                DisplayedText = "link-" + i,
                ActualTarget = "https://example.test/" + i,
                UnicodeHost = "example.test",
                AsciiHost = "example.test",
            })
            .ToList();

        var attachments = Enumerable.Range(0, 30)
            .Select(i => new AttachmentMetadata
            {
                FileName = "file-" + i,
                DeclaredContentType = "application/octet-stream",
                ExtensionImpliedContentType = "application/octet-stream",
                SizeBytes = i,
                ContentUnavailable = false,
            })
            .ToList();

        var message = NimbleTestMessage.With(m => m with { Links = links, Attachments = attachments }).Message;
        var state = NimbleMessageState.Build(message, null, null, 2_500, 40, 20).State;
        var messageState = (Dictionary<string, object?>)state["message"]!;

        // A message with a thousand links must not be able to decide how much of the window it gets.
        Assert.Equal(40, ((IEnumerable<object>)messageState["links"]!).Count());
        Assert.Equal(20, ((IEnumerable<object>)messageState["attachments"]!).Count());
    }

    [Fact]
    public void Marks_a_shortened_body_and_leaves_an_intact_one_unmarked()
    {
        var longBody = new string('x', 5_000);
        var shortened = NimbleMessageState.Build(
            NimbleTestMessage.With(m => m with { BodyText = longBody }).Message, null, null, 100, 40, 20);

        Assert.True(shortened.BodyCut);
        Assert.False(shortened.QuotedCut);
        var shortenedMessage = (Dictionary<string, object?>)shortened.State["message"]!;
        Assert.Equal(true, shortenedMessage["body_text_shortened_for_prompt"]);
        Assert.Equal(100, shortenedMessage["body_text_characters_kept"]);

        // And the QUOTED pair is absent on a body-only cut, which is the mirror of the control below:
        // one flag spanning both fields would have written it here.
        Assert.False(shortenedMessage.ContainsKey("quoted_text_shortened_for_prompt"));

        var intact = NimbleMessageState.Build(
            NimbleTestMessage.Input().Message, null, null, 2_500, 40, 20);

        Assert.False(intact.BodyCut);
        Assert.False(intact.QuotedCut);
        Assert.False(((Dictionary<string, object?>)intact.State["message"]!)
            .ContainsKey("body_text_shortened_for_prompt"));
    }

    [Fact]
    public void Marks_a_shortened_quoted_tail_without_claiming_the_body_was_cut()
    {
        // THE RED CONTROL FOR THE PER-FIELD KEYS, and it fails against the code that shipped before this
        // change, which is the only thing that makes it evidence rather than decoration.
        //
        // The fixture is the intact one from the test above with a quoted tail longer than the budget
        // added, so the BODY is whole and only the QUOTED tail is cut. That is the ordinary reply shape
        // rather than an edge: BoundedMimeMessageAnalyzer fills QuotedText on any reply with a quoted
        // tail. The single flag this class used to build could not tell the two apart, so it wrote the
        // body's marker on a body it had never touched.
        var message = NimbleTestMessage.With(m => m with { QuotedText = new string('y', 3_000) }).Message;

        var state = NimbleMessageState.Build(message, null, null, 2_500, 40, 20);

        Assert.False(state.BodyCut);
        Assert.True(state.QuotedCut);

        var messageState = (Dictionary<string, object?>)state.State["message"]!;

        // The control on the control: the body must be WHOLE in the state, so an absent marker below is
        // absent because it was not written, and not because the body was dropped or cut to nothing.
        Assert.Equal(message.BodyText, messageState["body_text"]);

        Assert.True(messageState.ContainsKey("quoted_text_shortened_for_prompt"));
        Assert.Equal(2_500, messageState["quoted_text_characters_kept"]);

        Assert.False(messageState.ContainsKey("body_text_shortened_for_prompt"));
        Assert.False(messageState.ContainsKey("body_text_characters_kept"));
    }

    private static BehaviouralProfile Profile() => new()
    {
        Direction = MailDirection.Outbound,
        ProfileAvailable = true,
        ColdStart = false,
        FirstSeenDaysAgo = 412,
        MessagesObserved = 3_881,
        TrustedSamples = 2_906,
        Regime = "steady",
        DistinctRecipientsLastHour = 4,
        DistinctRecipientsLast30Days = 61,
        RecipientDistinctnessIsFloor = true,
        RecipientsNovelToSender = 1,
        MessagesLastHour = 9,
        MessagesLast24Hours = 140,
        BaselineMessagesPerHour = 6.5,
        FanoutLastHour = 6,
        BaselineFanoutPerHour = 4.25,
        TrendNarrative = "recipient fan-out rising while payment-redirection evidence rises",
        Movements =
        [
            new DimensionMovement { DimensionId = "semantic.fanout", Direction = "up", Magnitude = 0.4 },
            new DimensionMovement { DimensionId = "semantic.urgency_pressure", Direction = "up", Magnitude = 0.2 },
            // Beyond the cap, so both adapters have to make the same decision about it.
            new DimensionMovement { DimensionId = "semantic.link_lure", Direction = "flat", Magnitude = 0.0 },
            new DimensionMovement { DimensionId = "semantic.attachment_lure", Direction = "down", Magnitude = 0.3 },
            new DimensionMovement { DimensionId = "semantic.secrecy_bypass", Direction = "flat", Magnitude = 0.0 },
            new DimensionMovement { DimensionId = "semantic.identity_authority_claim", Direction = "up", Magnitude = 0.1 },
            new DimensionMovement { DimensionId = "semantic.transactional_character", Direction = "up", Magnitude = 0.6 },
            new DimensionMovement { DimensionId = "semantic.sensitive_data_request", Direction = "up", Magnitude = 0.9 },
        ],
        DimensionsWithSupport = 5,
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static JsonElement Property(string body, string name)
    {
        var root = JsonDocument.Parse(body).RootElement;

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new InvalidOperationException($"no '{name}' property in the request body");
    }

    /// <summary>
    /// Renders a JSON value with its object keys sorted, so the comparison is about what the state
    /// says rather than the order the two builders happened to write it in.
    /// </summary>
    private static string Canonical(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => "{"
                + string.Join(
                    ",",
                    element.EnumerateObject()
                        .OrderBy(p => p.Name, StringComparer.Ordinal)
                        .Select(p => $"\"{p.Name}\":{Canonical(p.Value)}"))
                + "}",
            JsonValueKind.Array => "["
                + string.Join(",", element.EnumerateArray().Select(Canonical))
                + "]",
            _ => element.GetRawText(),
        };
}
