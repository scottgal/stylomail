# Using Nimble from C#: local alternatives to Jev and a layered mail classifier

In [my first StyloMail article](/blog/stylomail-behavioural-inference-with-jev), I used Jev to ask specific questions about email and get typed answers back. Now there are local models for that kind of work too. I can try the same idea without sending the mail context to a hosted decision service.

**Nimble**, from Bespoke Labs, lets me try that locally through Ollama. From C#, the call is ordinary HTTP: send the message and some questions, then read the probabilities from the response.

For example, “does this message ask for new payment details?” is a useful question. It doesn't settle whether the message is fraudulent. A supplier can legitimately change banks. The sender's history, the conversation and the other observations still matter.

The interesting change is that I can now choose where that semantic judgement runs, which model does it, and how much work each layer should take on. That's particularly useful for the multi-layer approach planned for 1.5: give each stage a bounded job, and spend more on a message when the available evidence justifies it.

This is still a research project. I'll show a complete C# call, compare the local options with the hosted approach, and explain how they could fit into that layered design. The standalone example uses the documented API; the full StyloMail integration has some unresolved work, which I'll cover too.

[TOC]

## What a decision model gives us

A decision model answers questions whose possible outputs we define. It can choose between named options, evaluate against an ordered rubric, or return the probability that a yes/no statement is true. That last form is called **Noul**, and it's the one StyloMail uses for its semantic mail dimensions.

For email, several conditions can hold at once. A message can request credentials, contain a link and apply time pressure. Asking independent yes/no questions preserves those observations instead of forcing the model to pick a single label such as “spam” or “normal”.

The result is easy to consume in C#: a number associated with a named question. A general LLM can also classify mail and return constrained JSON. The appeal here is a model and interface built for these narrow decisions, without asking it to generate an explanation on every call.

This follows the [behavioural inference approach](/blog/behavioural-inference-systems-blog) I've been exploring across these projects. The model interprets language; the application combines that interpretation with observed behaviour and decides what action is justified.

## The local options

[Ollama added `/v1/systemone` in version 0.35](https://ollama.com/blog/ollama-now-supports-jev-style-decision-models). It offers a Jev-style decision interface for local models, including Nimble and Tev1.

| Option | What it offers | What needs evaluating for StyloMail |
| --- | --- | --- |
| Hosted Jev | A managed decision service, with no local model server to operate | Network dependency, service cost, acceptable handling of the supplied mail context |
| [Nimble 9B](https://ollama.com/library/nimble) | A local model specialised for typed decisions | Memory requirements, mail accuracy, latency under the intended workload |
| [Tev1 4B](https://ollama.com/library/tev1) | An experimental local decision model at a smaller size | Whether its answers and input limits suit the questions assigned to it |
| [Tev1 0.8B](https://ollama.com/library/tev1:0.8b) | A still smaller experimental option | Whether a narrow first-stage task can tolerate its errors |

The local models share an HTTP interface, which makes comparative experiments easier. It doesn't make their probabilities interchangeable. The same question can produce different answers, and a threshold that worked for one model needs checking when the model changes.

Tev1's model card recommends short inputs and identifies gaps in its evaluation, including calibration and prompt injection. That matters for email: the text we're asking the model to judge can itself contain instructions. I would evaluate it on a bounded task before giving it a role in the mail path. [Tev1's documented limitations](https://ollama.com/library/tev1) describe that starting point.

Nimble is the model used by the current local adapter. Tev1 is an option to investigate, rather than an integrated StyloMail provider tested here.

## A complete C# call

If you just want to try the decision endpoint, you do not need StyloMail or a C# SDK. A console application and `HttpClient` are enough.

[Ollama's decision-model API](https://ollama.com/blog/ollama-now-supports-jev-style-decision-models) is available from version 0.35. Install or upgrade Ollama, keep its server running, then download Nimble and create a .NET console project:

```bash
ollama pull nimble
dotnet new console -n NimbleMailDemo
cd NimbleMailDemo
```

Replace `Program.cs` with the following. This uses Ollama's usual port, `11434`; the StyloMail measurements later in this article used a separate server on `11435`.

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using var http = new HttpClient
{
    BaseAddress = new Uri("http://127.0.0.1:11434/"),
    Timeout = TimeSpan.FromMinutes(2)
};

var request = new
{
    model = "nimble",
    state = JsonSerializer.Serialize(new
    {
        subject = "Updated payment details",
        body = "Please use our replacement bank account for today's payment."
    }),
    questions = new
    {
        payment_redirection = new
        {
            type = "noul",
            instructions = "Does the message ask to change where a payment is sent?",
            criteria = new
            {
                @true = "Requests a new bank account or other payment destination.",
                @false = "Does not request a change to the payment destination."
            }
        }
    }
};

using var response = await http.PostAsJsonAsync("v1/systemone", request);
response.EnsureSuccessStatusCode();

var result = await response.Content.ReadFromJsonAsync<DecisionResponse>();
if (result?.Answers is not { } answers
    || !answers.TryGetValue("payment_redirection", out var answer)
    || answer is null
    || answer.Type != "noul"
    || answer.Noul is not { } probability
    || !double.IsFinite(probability)
    || probability is < 0 or > 1)
{
    throw new InvalidDataException("Expected a Noul probability between 0 and 1.");
}

Console.WriteLine($"Probability of payment redirection: {probability:P1}");

public sealed record DecisionResponse(
    Dictionary<string, NoulAnswer?>? Answers);

public sealed record NoulAnswer(
    string? Type,
    [property: JsonPropertyName("noul")] double? Noul);
```

Run it with `dotnet run`. The percentage is the model's estimate for this question; the exact value will vary. It is not a spam verdict. A legitimate supplier can request new bank details too.

The `@true` and `@false` names escape C# keywords; JSON serialisation writes them as `"true"` and `"false"`. `state` is a JSON string here to match the StyloMail adapter's measured contract. The endpoint also accepts structured state, as shown in Ollama's example.

The response check matters: an absent answer is a failed call, not a probability of zero. The two criteria make the question's meaning explicit, while the surrounding application decides what to do with the answer. That is the same division described in [Constrained Fuzziness](/blog/constrained-fuzziness-pattern): a probabilistic component supplies evidence, and application code constrains how that evidence can be used.

StyloMail wraps this call with its mail state builder, retries, circuit breaker, evidence availability and cache provenance. Its internal wire types below explain that implementation; they are not types you need to copy into this standalone example.

## What running locally changes

The immediate advantage is control over where the supplied mail context goes. With a loopback endpoint, the classification request stays on the machine. A server on your own network gives you another deployment option, although you then need to account for that network boundary too.

Local inference also lets you run experiments without a metered service call for every question. I can replay a mail corpus, change the criteria and inspect where answers move. I still pay for the hardware, electricity and time spent operating the server. “No per-call API bill” is a different claim from “free”.

There are useful operational trade-offs:

| Concern | Local decision model | Hosted service |
| --- | --- | --- |
| Data path | Choose the machine or internal server that receives the context | Supply context to the service under its terms and configuration |
| Availability | Can work without an external inference service when the model is already installed | Depends on connectivity and service availability |
| Capacity | Share finite RAM and compute with the rest of the application | Delegate inference capacity to the provider, subject to its limits |
| Latency | Depends on hardware, model loading, contention and question count | Includes network time as well as service processing |
| Operation | Install, monitor and update the model server | Manage credentials, quotas and integration; provider operates inference |
| Reproducibility | Can retain a specific model artifact and runtime version | Depends on the provider's versioning and retention options |

I wouldn't claim that local is automatically faster. A warm model on suitable hardware is a different experiment from a cold model on a busy CPU. Measure the full request on the hardware that will serve it, including the number of questions and the concurrency you expect.

That question count deserves attention. One HTTP request can contain several questions, but that doesn't mean the model does one shared pass for all of them. [Nimble's documentation](https://ollama.com/library/nimble) describes scoring the prompt for each question. Batching simplifies the call; its runtime cost still needs measuring.

## Where this fits in the planned 1.5 layers

The attraction of local options is being able to assign different jobs to different stages. A cheap stage can handle a narrow, well-tested question. A more capable stage can examine messages that need additional interpretation. Explicit policy can then use the combined evidence.

This section describes the planned direction, rather than a model cascade already implemented in the current adapter.

For a payment-redirection message, the stages have different information to contribute. Extraction can identify the sender, recipients, links and authentication results. The semantic model can recognise a request for replacement bank details even when the wording changes. Behavioural and conversation context can show whether this is an expected exchange or a sudden change in the account's activity.

The [first article's behavioural model](/blog/stylomail-behavioural-inference-with-jev) explains why those observations belong together. A local decision model supplies one part of that assessment. Its availability doesn't remove the need for history or policy.

There's also an opportunity to keep the model's input focused. Supply the facts needed for the question, within a deliberate budget, instead of sending an entire mailbox history. That's related to [Reduced RAG](/blog/reduced-rag): do useful extraction and reduction before asking a probabilistic component to interpret the result.

If a smaller model becomes an early stage, we need to measure the errors of the whole route. Messages that stage passes without further examination never reach the more capable model. A confident mistake at the start can therefore defeat the intended benefit of layering. The evaluation needs to include both the messages escalated and the messages left behind.

Nor would I average two models' probabilities and call the result more reliable. They may share training influences or make the same mistakes. Record which model answered which question, and evaluate the way the application combines their evidence.

That's the useful role of [Constrained Fuzziness](/blog/constrained-fuzziness-pattern) here: give probabilistic components bounded jobs, preserve their uncertainty, and put explicit constraints around the actions that follow.

## How the current C# adapter fits

StyloMail's public boundary is an `ISemanticMailClassifier`. It accepts a bounded view of the message and the dimensions to ask, then returns semantic evidence:

```csharp
public interface ISemanticMailClassifier
{
    ValueTask<SemanticAssessment> ClassifyAsync(
        SemanticMailInput input,
        CancellationToken cancellationToken);
}
```

The Nimble implementation takes an `HttpClient` and a `NimbleOptions` object. A caller that already has a `SemanticMailInput` can use it like this:

```csharp
using var http = new HttpClient();
var options = new NimbleOptions
{
    Endpoint = "http://127.0.0.1:11435/v1/systemone",
    Model = "nimble:latest"
};

ISemanticMailClassifier classifier =
    new NimbleSemanticMailClassifier(http, options);

SemanticAssessment assessment =
    await classifier.ClassifyAsync(input, cancellationToken);
```

This is an integration excerpt using `StyloMail.Core` and `StyloMail.Nimble`; `input` and `cancellationToken` come from the caller. The earlier console example is the standalone version.

The adapter builds the state and questions, validates the answers and maps each result back to its semantic dimension. It also handles retries, a circuit breaker and provenance for caching. The classifier returns evidence rather than a mail action, so provider choice stays separate from the policy that decides whether to allow, hold or reject a message.

An unavailable answer must remain visible as unavailable. Treating a failed call as a probability of zero would make “we could not assess this” look like “we assessed this and found no concern”. That distinction matters at every stage of a layered system.

## When the guard counts the wrong thing

The local adapter is not yet a finished replacement for the hosted path. Its context-budget checks inherited assumptions from an earlier transport.

The current request sends `model`, `state` and `questions`. It does **not** send `num_ctx`. Changing the adapter's `NumCtx` setting therefore doesn't ask the SystemOne server to change its context window.

Two checks also use different units. Before sending, `FitState` counts the serialised request's UTF-8 **bytes** and compares that count with `NumCtx`. After receiving, the guard compares the reported **tokens** with `AppliedContextWindow`:

```csharp
// Request sizing: bytes.
var total = Encoding.UTF8.GetByteCount(
    JsonSerializer.Serialize(request, WireJson));

if (total <= _options.NumCtx)
{
    return state;
}

// Response guard: reported tokens.
if (response.Usage?.InputTokens is { } evaluated
    && evaluated >= _options.AppliedContextWindow)
{
    // The adapter reports this assessment as Unavailable.
}
```

These are shortened source excerpts, not a method to copy. The important point is the unit mismatch: making the two thresholds numerically equal wouldn't turn a byte count into a token count.

The default `AppliedContextWindow` is still derived as `EffectiveNumCtx ?? NumCtx / 2`, following measurements on the previous `/api/generate` transport. That historical halving is not a measurement of the current `/v1/systemone` route. The current [Ollama client documentation](https://github.com/ollama/ollama-js/blob/main/README.md#systemone) says the server checks context limits without truncating input.

In our current investigation, a captured twelve-question request occupied 5,394 UTF-8 bytes, below the fit's 8,192-byte ceiling. The server returned HTTP 200, all twelve requested answers and 12,230 reported input tokens. The adapter then rejected the response through its own guard. That establishes a client refusal; it doesn't establish that the server read only part of a message.

There is another distinction to check: usage across several questions versus the length of one question's prompt. With the same state, our probe reported 688 input tokens for one question and 8,234 for twelve, about 686 per question. That supports the interpretation that the usage total accumulates work across questions. Comparing that total with a single-prompt context window can reject a batch whose individual prompts fit. The guard needs to account for the current endpoint's semantics.

This is why the runnable single-question example and the full mail integration are separate claims. The first shows how to call the API. The second needs its own end-to-end verification, with the actual question set and input sizes.

## What I want to measure next

The useful comparison is the same mail corpus through each candidate route. Keep the message, questions and available history comparable, and record model and runtime versions.

I want to measure legitimate mail incorrectly flagged, abusive mail missed, answer availability, calibration, memory use and response time. For a layered route, also measure how many messages reach each stage and what errors occur among those that don't escalate. A smaller model earns its place if the complete route gives us an acceptable trade-off.

The [testing discussion in the Stylo.Bot series](/blog/stylobot-release-nondeterministic-testing) is relevant here too. Tests of JSON parsing and boundary arithmetic are useful, but they don't establish how a live model behaves. Report clearly whether the live comparison ran, which model it used and what workload it covered.

The reason to pursue this is practical: the semantic part of the system now has local choices. We can try different models behind a small C# boundary, keep mail context within our chosen deployment, and explore the 1.5 layers without making every interpretation a hosted call. The work is to establish which jobs each model can perform well enough, then make that evidence useful to the rest of StyloMail.
