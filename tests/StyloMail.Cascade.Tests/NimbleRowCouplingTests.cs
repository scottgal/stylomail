using System.Net;
using System.Text;
using System.Text.Json;
using StyloMail.Core;
using StyloMail.Nimble;

namespace StyloMail.Cascade.Tests;

/// <summary>
/// The partial-read condition, pinned against a row the REAL local adapter writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule reads another component's attribute by name, so a double shaped like that component
/// would prove the rule and not the coupling.</b> These tests drive
/// <see cref="NimbleSemanticMailClassifier"/> over a transport double and hand its own rows to the
/// rule, so what is asserted is that the shipping producer's output lands in the condition the
/// design note claims it lands in.
/// </para>
/// <para>
/// <b>The control is the same message with no pressure on the body.</b> If the binding held for
/// either reason, the un-pressured arm would escalate too, and the condition would be a constant
/// rather than a reading of whether the read was whole.
/// </para>
/// </remarks>
public sealed class NimbleRowCouplingTests
{
    private const string LongBody = "Please confirm your password to view the invoice. ";

    [Fact]
    public async Task A_read_cut_by_the_body_ceiling_is_escalated_with_the_partial_read_reason()
    {
        var decision = await DecideOverNimbleAsync(numCtx: 32_768, bodyCharacters: 3_120);

        Assert.Contains(
            EscalationReason.PartialRead,
            decision.Reasons[SemanticDimensions.All[0].Id]);
    }

    [Fact]
    public async Task A_read_nothing_cut_is_not_escalated_for_being_partial()
    {
        // The same adapter, a body under its 2,500-character ceiling and a window with room for it, so
        // neither of the two independent cuts fires. This arm began life with a 3,120-character body
        // and a roomy window and was RED, which is the finding rather than a fixture to adjust: a body
        // over the CEILING is marked cut whatever the window is, because the ceiling is applied before
        // the fit is considered. So "the read was whole" is a state only a body under the ceiling can
        // reach, and that is what this arm pins.
        var decision = await DecideOverNimbleAsync(numCtx: 32_768, bodyCharacters: 52);

        Assert.False(decision.Reasons.ContainsKey(SemanticDimensions.All[0].Id));
    }

    private static async Task<CascadeDecision> DecideOverNimbleAsync(int numCtx, int bodyCharacters)
    {
        var input = InputWithBodyOf(bodyCharacters);
        var classifier = new NimbleSemanticMailClassifier(
            new HttpClient(new SystemOneHandler()),
            new NimbleOptions
            {
                Endpoint = "http://127.0.0.1:11435/v1/systemone",
                NumCtx = numCtx,

                // The guard compares the server's evaluated count against the applied window, and the
                // fit compares the request's bytes against NumCtx. This arm is about the FIT, so the
                // applied window is set high enough that the guard cannot fire: with it unset the
                // applied window is half of NumCtx, and the 4,000-byte arm would answer every row
                // Unavailable for a reason that has nothing to do with the body being shortened.
                EffectiveNumCtx = 65_536,
            });

        var assessed = await classifier.ClassifyAsync(input, CancellationToken.None);
        return CascadeEscalationRule.Decide(input, assessed.Evidence, new CascadeOptions());
    }

    private static SemanticMailInput InputWithBodyOf(int characters)
    {
        var input = CascadeInputs.Input();
        return input with
        {
            Message = input.Message with
            {
                BodyText = string.Concat(Enumerable.Repeat(LongBody, (characters / LongBody.Length) + 1))
                    [..characters],
            },
        };
    }

    /// <summary>
    /// A transport that answers every dimension with a decisive probability, as the server types it.
    /// </summary>
    /// <remarks>
    /// The wire names are written out literally rather than taken from the adapter's constants, so a
    /// rename on either side of the wire breaks this test instead of moving with it.
    /// </remarks>
    private sealed class SystemOneHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            using var document = JsonDocument.Parse(body);
            var questionCount = document.RootElement.GetProperty("questions").EnumerateObject().Count();

            var answers = new Dictionary<string, object>(StringComparer.Ordinal);
            for (var index = 0; index < questionCount; index++)
            {
                answers["q" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                    new { type = "noul", noul = 0.97 };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        model = "nimble:latest",
                        answers,
                        usage = new { input_tokens = 10, output_tokens = 12 },
                    }),
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
