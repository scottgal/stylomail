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
    /// <summary>An Ollama success body, with the answer codes spelled out per dimension.</summary>
    internal static HttpResponseMessage Ok(
        IReadOnlyList<SemanticDimension> askable,
        Func<SemanticDimension, string> code,
        int promptEvalCount = 900,
        int evalCount = 24,
        string model = "nimble:latest")
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < askable.Count; index++)
        {
            answers["q" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)] = code(askable[index]);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model,
                    response = JsonSerializer.Serialize(answers),
                    done = true,
                    done_reason = "stop",
                    prompt_eval_count = promptEvalCount,
                    prompt_eval_cached_count = 0,
                    eval_count = evalCount,
                    total_duration = 8_100_000_000L,
                    load_duration = 0L,
                    prompt_eval_duration = 2_000_000_000L,
                    eval_duration = 6_000_000_000L,
                }),
                Encoding.UTF8,
                "application/json"),
        };
    }

    /// <summary>A success body whose <c>response</c> is arbitrary text rather than the schema's object.</summary>
    internal static HttpResponseMessage OkWithRawAnswer(string rawAnswer)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = "nimble:latest",
                    response = rawAnswer,
                    done = true,
                    done_reason = "stop",
                    prompt_eval_count = 900,
                    eval_count = 10,
                }),
                Encoding.UTF8,
                "application/json"),
        };

    internal static HttpResponseMessage Status(HttpStatusCode status, string body = "")
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    /// <summary>Answers every dimension affirmatively, which is not a claim about the message.</summary>
    internal static Func<SemanticDimension, string> AllAffirmative => _ => NimbleQuestionSet.AffirmativeCode;

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
