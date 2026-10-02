# Using Nimble from C# (Part 2): local alternatives to Jev and a layered mail classifier

<!-- category -- AI,Architecture,Behavioural Inference,Jev,Nimble,StyloMail,.NET,Ollama,Decision Models,Patterns -->
<datetime class="hidden">2026-10-01T22:00</datetime>

In [my first StyloMail article](/blog/stylomail-behavioural-inference-with-jev), I used Jev to ask specific questions about email and get typed answers back. Now there are local models for that kind of work too. I can try the same idea without sending the mail context to a hosted decision service.

**Nimble**, from Bespoke Labs, now runs through Ollama as an integrated and tested StyloMail semantic provider. From C#, the call is ordinary HTTP: send the message and some questions, then read the probabilities from the response. I've tested the real adapter on this machine and on a second machine over the LAN; the Host now points at that second server.

For example, “does this message ask for new payment details?” is a useful question. It doesn't settle whether the message is fraudulent. A supplier can legitimately change banks. The sender's history, the conversation and the other observations still matter.

The interesting change is that I can now choose where that semantic judgement runs, which model does it, and how much work each layer should take on. That's particularly useful for the multi-layer approach proposed in [Part 1.5's conversation research](/blog/conversationresearch): give each stage a bounded job, and spend more on a message when the available evidence justifies it.

This is still a research project, but the Nimble integration is working. I'll show a complete C# call, explain how StyloMail selects the provider, and report what the live tests established. The layered specialist design remains a research direction; the current integration supplies the semantic dimensions behind the same boundary as Jev.

*Updated 2 October 2026 with the integrated provider, context-budget findings and a live comparison across two model servers.*

[TOC]

---

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

Nimble is the model used by the integrated self-hosted adapter. Tev1 remains an option to investigate. Our live route checks found `/v1/systemone` available on Ollama 0.35.0; the tested 0.31.1 and 0.32.12 servers did not serve it. Installing the model alone does not establish that the running server supports the decision endpoint.

## A complete C# call

If you just want to try the decision endpoint, you do not need StyloMail or a C# SDK. A console application and `HttpClient` are enough.

[Ollama's decision-model API](https://ollama.com/blog/ollama-now-supports-jev-style-decision-models) is available from version 0.35. Install or upgrade Ollama, keep its server running, then download Nimble and create a .NET console project:

```bash
ollama pull nimble
dotnet new console -n NimbleMailDemo
cd NimbleMailDemo
```

Replace `Program.cs` with the following. This uses Ollama's usual port, `11434`. The StyloMail comparison later in this article used a local server on `11435` and a second machine on `11434`, both running Ollama 0.35.0.

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

The example has been compiled, but this exact client has not yet been tested against a live endpoint. Once Ollama and the model are ready, run it with `dotnet run`. The percentage is the model's estimate for this question; the exact value will vary. It is not a spam verdict. A legitimate supplier can request new bank details too.

The `@true` and `@false` names escape C# keywords; JSON serialisation writes them as `"true"` and `"false"`. `state` is a JSON string here to match the StyloMail adapter's measured contract. The endpoint also accepts structured state, as shown in Ollama's example.

The response check matters: an absent answer is a failed call, not a probability of zero. The two criteria make the question's meaning explicit, while the surrounding application decides what to do with the answer. That is the same division described in [Constrained Fuzziness](/blog/constrained-fuzziness-pattern): a probabilistic component supplies evidence, and application code constrains how that evidence can be used.

StyloMail wraps this call with its mail state builder, retries, circuit breaker, evidence availability and cache provenance. Its internal wire types below explain that implementation; they are not types you need to copy into this standalone example.

## What running locally changes

The immediate advantage is control over where the supplied mail context goes. With a loopback endpoint, the classification request stays on the machine. StyloMail's current Host configuration uses a second machine on the private LAN, so mail context crosses that network boundary. This is self-hosted inference, with a separate server to operate and a network dependency to account for.

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

## Where local models fit into the Part 1.5 proposal

The attraction of local options is being able to assign bounded work to different stages. [The Part 1.5 research proposal](/blog/conversationresearch) makes those stages concrete:

| Layer | Proposed job |
| --- | --- |
| L0: profiles and reuse | Read prior sender, receiver and relationship state; verify whether existing evidence can be reused. |
| L1: orientation | Ask twelve broad questions when fresh semantic assessment is needed, selecting overlapping specialist routes. |
| L2: specialists | Ask focused questions about phishing and business email compromise, grooming and coercion, relationship and financial scams, or harassment and pile-ons. |
| L3: behavioural update | Record conversation events and update decayed sender, receiver and relationship observations. |
| L4: policy and review | Apply explicit evidence requirements, choose review priority and authorise an action. |

A specialist here is a question bank with a particular evidence view. It need not be a separate model. The same Nimble instance could answer orientation and specialist questions, while a smaller local model is another option to evaluate for a narrow task. Those are deployment choices to test, rather than measured advantages of the proposal.

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
    Model = "nimble:latest",
    EffectiveNumCtx = 65_536
};

ISemanticMailClassifier classifier =
    new NimbleSemanticMailClassifier(http, options);

SemanticAssessment assessment =
    await classifier.ClassifyAsync(input, cancellationToken);
```

This is an integration excerpt using `StyloMail.Core` and `StyloMail.Nimble`; `input` and `cancellationToken` come from the caller. The earlier console example is the standalone version. The explicit `EffectiveNumCtx` matches the tested Host configuration. It sets the client's response guard; it does not configure Ollama's context window.

In the Host, select Nimble through configuration:

```json
{
  "StyloMail": {
    "Assessment": {
      "Provider": "Nimble"
    },
    "Nimble": {
      "Endpoint": "http://127.0.0.1:11435/v1/systemone",
      "Model": "nimble:latest",
      "EffectiveNumCtx": 65536
    }
  }
}
```

This example uses loopback. Set `Endpoint` to your own Ollama server for a LAN deployment. The adapter's code default still uses loopback; our Host overrides it to use the second machine. Provider selection defaults to Jev, so choosing Nimble is explicit. The Host logs the resolved endpoint and window settings at startup, and reads its configuration from the application directory so launching it from another working directory does not lose the window setting.

The adapter builds the state and questions, validates the answers and maps each result back to its semantic dimension. It also handles retries, a circuit breaker and provenance for caching. The classifier returns evidence rather than a mail action, so provider choice stays separate from the policy that decides whether to allow, hold or reject a message.

An unavailable answer must remain visible as unavailable. Treating a failed call as a probability of zero would make “we could not assess this” look like “we assessed this and found no concern”. That distinction matters at every stage of a layered system.

## Context budgets: what the integration tests changed

The initial integration could receive a complete answer and then refuse it in the client. The guard inherited a window estimate from the earlier `/api/generate` transport. That estimate did not describe the current `/v1/systemone` route.

There are two separate budgets. `FitState` caps the serialised request at `NumCtx` UTF-8 **bytes**, 8,192 by default, shortening the body when needed. After the response arrives, the guard compares `usage.input_tokens` with `AppliedContextWindow`, defined as `EffectiveNumCtx ?? NumCtx / 2`. Without an explicit setting, that gives a guard threshold of 4,096.

The request sends `model`, `state` and `questions`. It does **not** send `num_ctx`, so neither setting asks the server to change its window. Despite its name, `AppliedContextWindow` is the client's configured assumption, not a window reported by Ollama.

The live measurements explain why the inherited default failed. With one fixed state, six questions produced 5,404 request bytes and 7,644 reported input tokens; twelve produced 7,246 bytes and 20,642 tokens. The server constructs its own prompts, and the reported usage is not a tokenisation of the HTTP request. Those measurements are recorded in `question-count-curve.json` and `question-count-sweep.json` from 1 October.

Content matters too. At the full 2,500-character body cap and twelve questions, a prose body produced 19,814 input tokens, while a hexadecimal-looking body produced 43,202. Both fitted the request byte budget. The six-shape measurement is recorded in `expansion-shapes-2500.json`. A threshold chosen only from ordinary prose would refuse some dense bodies that the server can answer.

The Host now explicitly sets `EffectiveNumCtx` to **65,536**, above the largest evaluation measured for those bounded requests. Separate probes checked that information at the start and end of larger inputs remained readable, including a dense input reporting 46,556 tokens (`probe-shift-dense.json`). This supports the setting for the tested workload. It does not establish the server's exact maximum window or prove that every possible input will be read whole.

When running the adapter outside the Host, carry that setting across explicitly. A live test uses `NIMBLE_EFFECTIVE_NUM_CTX=65536`; the Host uses `StyloMail:Nimble:EffectiveNumCtx`. Leaving it unset restores the inherited 4,096 threshold and can make a usable server response appear unavailable.

## Partial input stays visible

The model receives a bounded message view. The default body cap is 2,500 characters, and the byte budget can shorten it further. Quoted history is bounded separately. The request records which fields were shortened so the model can see that its input is partial.

The returned evidence records that too. A valid answer over a nonempty shortened body remains `Available`, with a reason explaining the cut. That preserves the answered dimensions in policy's coverage calculation while exposing the input limit to the operator. If a nonempty body is cut to zero characters, its semantic rows become `Unavailable` with a specific reason. An answer over an emptied body does not count as an assessment of that body.

This is a deliberate policy choice: `Available` means the bounded question was answered, not that the model read the entire original email. Missing or malformed answers and provider failures remain unavailable. The management response exposes the evidence reasons, so a consumer can tell why a row was shortened or refused.

## What the live comparison established

On 2 October, the real C# adapter ran the same six-message corpus against two Ollama 0.35.0 servers: this machine and a Windows machine on the private LAN. The corpus covers transactional mail, credential requests, payment redirection, an in-thread reply, unsolicited solicitation, and urgency with secrecy. Each case sent one request containing eleven questions, or twelve when conversation context made continuity applicable. All **67 requested semantic dimensions** were answered on each host.

Both servers held `nimble:latest` at the same recorded digest, `24e550a16a7081881be2f1f0d91e8cc13a597472735c04119f035a0a85c67e0c`, with the same model size. The runs used the current `nimble-request-shape/2`, the 8,192-byte request budget and the explicit 65,536 client guard setting. The artifacts are `pair-local.json` and `pair-point15.json`; their exchange logs preserve the sent requests and raw responses.

| Measurement | This machine | Second machine over LAN |
| --- | --- | --- |
| Corpus calls after warmup | 6 | 6 |
| Requested dimensions answered | 67 of 67 | 67 of 67 |
| Median request latency | 4,602 ms | 1,959 ms |
| Request latency range | 2,170–4,821 ms | 1,243–2,159 ms |
| Warmup call, reported separately | 2,257 ms | 34,898 ms |

These are timings for this corpus on these machines, not throughput measurements or a general comparison with hosted Jev. The warmup is excluded from the six-call latency summary and can include model loading.

The more interesting result is that **identical model weights and requests did not produce identical probabilities across hosts**. All seven calls per host, including warmup, had byte-identical request bodies across the pair, and the reported input token counts matched. All 78 raw Noul probabilities still differed, with a maximum absolute difference of 0.0227, about **2.27 percentage points**. At the artifacts' three-decimal precision, 30 of the 67 corpus dimensions differed.

For example, urgency on the urgency-and-secrecy message moved from 0.847 to 0.870; link lure on the legitimate transactional message moved from 0.788 to 0.770. The latter is also a useful reminder that a real delivery link can satisfy a semantic question without making the email abusive.

Each host answered its repeated warmup/corpus case identically, which supports attributing the observed difference to the serving environment rather than variation between those repeated calls. We did not run policy through this pair, so this experiment does not establish whether the differences change a mail action.

The cache now includes the endpoint origin as well as the model reference, question/request versions, configured budgets and message input. Moving the server can change the evidence even when the model digest stays the same. The endpoint echoes the configured model name rather than a resolved model identity, so retaining the digest and runtime details alongside a measurement still matters.

These live tests establish that the integrated adapter sends the actual question set, receives typed probabilities and returns usable evidence under the tested configuration. Six fixtures do not establish mail accuracy, calibration or resistance to prompt injection. Those need their own evaluations.

## What I want to measure next

The next useful comparison is a larger labelled mail corpus through Nimble, hosted Jev and any candidate Tev1 route. The two-host Nimble test established integration and serving differences; it did not compare model accuracy. Keep the message, questions and available history comparable, and record model and runtime versions.

I want to measure legitimate mail incorrectly flagged, abusive mail missed, answer availability, calibration, memory use and response time. For a layered route, also measure how many messages reach each stage and what errors occur among those that don't escalate. A smaller model earns its place if the complete route gives us an acceptable trade-off.

The [testing discussion in the Stylo.Bot series](/blog/stylobot-release-nondeterministic-testing) is relevant here too. Tests of JSON parsing and boundary arithmetic are useful, but they don't establish how a live model behaves. Report clearly whether the live comparison ran, which model it used and what workload it covered.

Nimble now supplies usable semantic evidence through StyloMail's C# boundary, with the deployment choosing where inference runs. The next work is to establish which jobs each model can perform well enough on real mail, and then evaluate the layers proposed in Part 1.5 using that evidence.

> **StyloMail series**
>
> - **Part 1:** [Behavioural inference with Jev](/blog/stylomail-behavioural-inference-with-jev), the mail system, semantic evidence and explicit policy.
> - **Part 1.5:** [Conversation analysis with specialists (research)](/blog/conversationresearch), the proposed layers, profiles and specialist question banks.
> - **Part 2:** [Using Nimble from C#](/blog/stylomail-using-nimble-from-csharp), local decision models, a C# example and the trade-offs of a layered classifier.
>
> **Coming soon:** the Avalonia console and management API write-up, once screenshots and client testing are ready.
