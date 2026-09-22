using System.Net;
using System.Text;
using System.Text.Json;
using StyloMail.Core;

namespace StyloMail.Jev.Tests;

/// <summary>
/// Records a live provider response for every message in the corpus.
/// </summary>
/// <remarks>
/// <para>
/// <b>Driven through the adapter, not a new HTTP client.</b> The capture is what
/// <see cref="JevSemanticMailClassifier"/> actually receives, so a replay test exercises the response
/// the adapter would have parsed rather than the response somebody remembered it parsed.
/// </para>
/// <para>
/// <b>The key never reaches a file, a log or a message.</b> It is read once, placed on
/// <c>JevOptions</c>, and sent as a bearer header. Nothing here writes it, and the captured fixture
/// holds only the provider's response.
/// </para>
/// <para>
/// <b>Nothing is recorded without a credential.</b> The fact skips with a message naming both places
/// the key may be supplied, because an empty corpus that looks like a finished recording is worse
/// than no corpus at all.
/// </para>
/// </remarks>
public sealed class JevCorpusRecordingTests
{
    [JevLiveFact]
    public async Task EveryCorpusCaseIsRecordedFromALiveCall()
    {
        var apiKey = JevCorpus.RequireCredential();
        var options = new JevOptions
        {
            ApiKey = apiKey,
            // The pinned versioned id, never the alias. An alias that resolves elsewhere would make
            // the corpus a record of a model we did not choose.
            Model = "jev-1.13.0",
            // The default one-second deadline is correct warm and too tight cold, and the first call
            // in a process pays TLS setup. Recording is not the place to discover that, so the
            // deadline is widened here and only here.
            Timeout = TimeSpan.FromSeconds(30),
            MaxRetries = 2,
        };

        var recorded = new List<string>();

        foreach (var caseName in JevCorpus.Cases)
        {
            var capture = new CapturingHandler(new HttpClientHandler());
            using var http = new HttpClient(capture);
            var classifier = new JevSemanticMailClassifier(http, options);

            var input = JevCorpus.BuildInput(caseName);
            var assessment = await classifier.ClassifyAsync(input, CancellationToken.None);

            if (capture.ResponseBody is null)
            {
                throw new InvalidOperationException(
                    $"No response was captured for '{caseName}', so there is nothing to record.");
            }

            // A response the adapter could not parse is a failed recording, not a fixture. Committing
            // it would put a broken shape into the corpus and the replay test would then assert on
            // the breakage as though it were intended.
            if (assessment.Evidence.All(e => e.Availability != EvidenceAvailability.Available))
            {
                throw new InvalidOperationException(
                    $"'{caseName}' produced no available evidence, so the capture is not a usable "
                    + "recording. Check the credential and the response shape before committing.");
            }

            var asked = input.Dimensions.Count(d =>
                d.Id != SemanticDimensions.ConversationalContinuityId
                || input.Message.ConversationContext is { Count: > 0 });

            await JevCorpus.WriteRecordingAsync(
                caseName,
                capture.ResponseBody,
                new JevRecordingProvenance
                {
                    Case = caseName,
                    RequestedModel = options.Model,
                    ReportedModel = assessment.ResolvedModelVersion,
                    QuestionSchemaVersion = SemanticDimensions.QuestionSchemaVersion,
                    RecordedAt = DateTimeOffset.UtcNow,
                    Source = "live",
                    InputTokens = assessment.InputTokens,
                    OutputTokens = assessment.OutputTokens,
                    AskedDimensions = asked,
                },
                CancellationToken.None);

            recorded.Add(caseName);
        }

        Assert.Equal(JevCorpus.Cases.Count, recorded.Count);

        // Named in the output so a recording run says what it wrote rather than passing silently.
        Assert.NotEmpty(recorded);
    }

    /// <summary>
    /// Keeps the provider's response as text while the adapter still parses it.
    /// </summary>
    /// <remarks>
    /// The body is buffered and the content replaced with an equivalent copy. Handing the original
    /// stream on would work until the day something reads it once, and then the adapter would parse
    /// an empty body and record that as an unavailable assessment.
    /// </remarks>
    private sealed class CapturingHandler : DelegatingHandler
    {
        internal CapturingHandler(HttpMessageHandler inner)
            : base(inner)
        {
        }

        internal string? ResponseBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // base.SendAsync is what passes the request to InnerHandler, which is the real transport.
            // HttpMessageHandler.SendAsync is protected, so wrapping rather than holding a reference
            // to the inner handler is the only way to delegate to it.
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return response;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            ResponseBody = Encoding.UTF8.GetString(bytes);

            var replacement = new ByteArrayContent(bytes);
            foreach (var header in response.Content.Headers)
            {
                replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Content = replacement;
            return response;
        }
    }
}
