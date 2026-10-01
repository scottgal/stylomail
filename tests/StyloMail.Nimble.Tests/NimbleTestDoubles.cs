using System.Net;
using System.Text;
using System.Text.Json;
using StyloMail.Core;

namespace StyloMail.Nimble.Tests;

/// <summary>A transport that records what was sent and answers with whatever the test decides.</summary>
/// <remarks>
/// Nothing in this suite touches the network. The recordings it makes are the assertions: what the
/// adapter actually put on the wire is the only thing that decides whether the provider was asked the
/// question we think it was.
/// </remarks>
internal sealed class RecordingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
    : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> RequestBodies { get; } = [];

    public string LastBody => RequestBodies[^1];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));

        return respond(request, Requests.Count - 1);
    }
}

internal static class NimbleTestDoubles
{
    /// <summary>
    /// A SystemOne success body: one Noul answer per askable dimension, keyed positionally, plus usage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The wire names are written out literally rather than taken from the adapter's constants.</b>
    /// <c>answers</c>, <c>type</c>, <c>noul</c>, <c>usage</c>, <c>input_tokens</c> and the <c>q0</c>
    /// key form are all spelled here as the server spells them. That is what makes this a double: if
    /// the adapter's serialisation or its key derivation is renamed, these bodies stop matching and the
    /// tests go red, which a double built from the adapter's own constants could not do.
    /// </para>
    /// <para>
    /// <b>The probability is supplied by the caller, including values in the middle of the range.</b>
    /// The shape this replaced could only answer 1.0 or 0.0, so a double could not express 0.5 at all.
    /// A test passing 0.5 here is asserting what the ADAPTER does with a mid-range answer, which is not
    /// evidence about what the model returns: see <see cref="NimbleQuestionSet.MapNoul"/> for what is
    /// and is not claimed about the real distribution.
    /// </para>
    /// </remarks>
    internal static HttpResponseMessage Ok(
        IReadOnlyList<SemanticDimension> askable,
        Func<SemanticDimension, double> probability,
        int? inputTokens = 900,
        int? outputTokens = 24,
        string model = "nimble:latest")
    {
        var answers = new Dictionary<string, object>(StringComparer.Ordinal);
        for (var index = 0; index < askable.Count; index++)
        {
            answers["q" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                Noul(probability(askable[index]));
        }

        return OkWithAnswers(answers, inputTokens, outputTokens, model);
    }

    /// <summary>One Noul answer object, as the server types it.</summary>
    internal static object Noul(double probability) => new { type = "noul", noul = probability };

    /// <summary>
    /// A success body carrying exactly the answers given, and nothing for any other key.
    /// </summary>
    /// <remarks>
    /// The escape hatch for the answer-shape tests: a server that omitted a key, or answered one with a
    /// type the question did not ask for, or answered with a bare string. Passing
    /// <c>inputTokens: null, outputTokens: null</c> omits the <c>usage</c> block entirely, which is how
    /// the "an absent count is reported as absent" path is reached.
    /// </remarks>
    internal static HttpResponseMessage OkWithAnswers(
        IReadOnlyDictionary<string, object> answers,
        int? inputTokens = 900,
        int? outputTokens = 24,
        string model = "nimble:latest")
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model,
                    answers,
                    usage = inputTokens is null && outputTokens is null
                        ? null
                        : new { input_tokens = inputTokens, output_tokens = outputTokens },
                }),
                Encoding.UTF8,
                "application/json"),
        };

    /// <summary>
    /// A success body carrying raw JSON text, for the cases where the promised object is what is wrong.
    /// </summary>
    /// <remarks>
    /// The companion to <see cref="OkWithAnswers"/>: that one builds a well-typed body out of typed
    /// answers, and this one is the only way to reach a body that is malformed as JSON, or whose
    /// <c>answers</c> member is a number, a string or an array where an object was promised. Those are
    /// real things a server can return and the adapter has a stated behaviour for each, so the tests
    /// that pin that behaviour need a seam that can produce them.
    /// </remarks>
    internal static HttpResponseMessage OkWithRawBody(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    internal static HttpResponseMessage Status(HttpStatusCode status, string body = "")
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    /// <summary>Answers every dimension 1.0, which is not a claim about the message.</summary>
    internal static Func<SemanticDimension, double> AllAffirmative => _ => 1.0;

    internal static NimbleSemanticMailClassifier Create(
        RecordingHandler handler,
        NimbleOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        var http = new HttpClient(handler);
        return new NimbleSemanticMailClassifier(http, options ?? Options(), timeProvider);
    }

    internal static NimbleOptions Options() => new()
    {
        Model = "nimble:latest",
        RetryBaseDelay = TimeSpan.Zero,
    };
}

/// <summary>
/// The message every test classifies, unless it needs a specific field.
/// </summary>
/// <remarks>
/// Shaped like the hosted adapter's test input on purpose: the drift test compares the state the two
/// classifiers build from the same input, and that is only meaningful if the input is identical.
/// </remarks>
internal static class NimbleTestMessage
{
    internal static SemanticMailInput Input() => With(m => m);

    internal static SemanticMailInput With(Func<MailAnalysisInput, MailAnalysisInput> mutate)
    {
        var input = new SemanticMailInput
        {
            Message = new MailAnalysisInput
            {
                Envelope = new MailEnvelope
                {
                    InternalMessageId = "msg-1",
                    TenantId = "tenant-a",
                    Direction = MailDirection.Inbound,
                    TrustedPrincipalId = "connector-1",
                    MailFrom = "sender@example.com",
                    RcptTo = ["finance@contoso.com"],
                    ReceivedAt = new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero),
                    MimeDigest = "sha256:abc",
                    PayloadReference = "spool/msg-1",
                },
                Authentication = new AuthenticationContext
                {
                    Results = [],
                    ApprovedSenderIdentities = [],
                    ProvenanceIncomplete = true,
                },
                Channel = ChannelContext.Email,
                Subject = "Invoice attached",
                BodyText = "Please confirm your password to view the invoice.",
                Links = [],
                Attachments = [],
                Coverage = new AnalysisCoverage
                {
                    BodyParsed = true,
                    HtmlPresent = false,
                    HasAttachments = false,
                    HtmlTextDisagreement = false,
                    ParserLimitExceeded = false,
                    ContentEncrypted = false,
                    Truncated = false,
                    ConversationContextMissing = true,
                },
            },
            Dimensions = SemanticDimensions.All,
        };

        return input with { Message = mutate(input.Message) };
    }
}
