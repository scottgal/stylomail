using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using StyloMail.Core;
using StyloMail.Mime;
using StyloMail.Nimble;
using StyloMail.Policy;

namespace StyloMail.Cascade.Tests;

/// <summary>
/// The cascade measured over a generated corpus batch, against the live local endpoint.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a measurement and not an assertion.</b> It writes one artifact per run and asserts
/// nothing about the values, because a rule that is asserted to escalate at a particular rate would
/// be a rule fitted to the batch it was measured on. The assertions that matter live in the rule's
/// own tests; what this produces is the population, the per-dimension answers, and which conditions
/// fired.
/// </para>
/// <para>
/// <b>The components are the Host's, the composition is not.</b> The MIME adapter and the provider
/// adapter are the shipping ones, driven with the envelope the corpus's own <c>seed</c> posts, taken
/// from the manifest rather than re-derived, because a manifest that declared one envelope while
/// this rebuilt another would make the artifact a description of a message nobody sent. What is
/// bypassed is the assessor and the policy engine, which do not change what the classifier answers.
/// </para>
/// <para>
/// <b>The hosted arm is absent unless the operator has supplied a key file.</b> The documented
/// transport is <c>TYPESAFE_API_KEY_FILE</c>, opened by this process, so no value reaches a command
/// line or an artifact. When it is unset the artifact says <c>"hosted": "absent"</c> rather than
/// writing empty columns that a later reader could mistake for a measured zero.
/// </para>
/// </remarks>
public sealed class CascadeLocalMeasurementTests
{
    /// <summary>A fixed instant, so two runs over one batch are comparable.</summary>
    private static readonly DateTimeOffset ReceivedAt = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [CascadeBatchFact]
    public async Task Measures_the_local_arm_the_prior_run_and_the_rule_over_a_batch()
    {
        var batch = Environment.GetEnvironmentVariable(CascadeBatchFactAttribute.BatchVariable)!;
        var outDirectory = Environment.GetEnvironmentVariable(CascadeBatchFactAttribute.OutVariable)
            is { Length: > 0 } configured
                ? configured
                : batch;

        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(batch, "manifest.json")))!;
        var files = Directory.GetFiles(batch, "*.eml")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        var analyzer = new BoundedMimeMessageAnalyzer();
        var localOptions = NimbleOptionsFor(CascadeBatchFactAttribute.LocalEndpoint());
        var local = new NimbleSemanticMailClassifier(new HttpClient(), localOptions);

        var priorEndpoint = CascadeBatchFactAttribute.PriorEndpoint();
        var priorOptions = priorEndpoint is null ? null : NimbleOptionsFor(priorEndpoint);
        var priorArm = priorOptions is null
            ? null
            : new NimbleSemanticMailClassifier(new HttpClient(), priorOptions);

        var hostedArm = TryBuildHostedArm();

        // The policy engine's OWN weights, injected rather than copied: a second copy here would
        // narrow the rule on a dimension the engine had stopped scoring, which is the drift this
        // injection exists to prevent.
        var options = new CascadeOptions
        {
            // Each arm's model AND host. The host is not decoration: two hosts running this model at
            // the same digest answered differently on every one of 352 dimension-instances measured
            // here, so a version string naming models alone would serve a cached entry taken under a
            // replaced host.
            ClassifierVersion = "cascade/1+"
                + $"{localOptions.Model}@{Authority(CascadeBatchFactAttribute.LocalEndpoint())}"
                + (priorEndpoint is null ? string.Empty : $"+{priorOptions!.Model}@{Authority(priorEndpoint)}"),
            DimensionWeights = new PolicyOptions().DimensionWeights,
        };

        var messages = new JsonArray();
        var summary = new Dictionary<string, int>(StringComparer.Ordinal);
        var asked = 0;
        var escalated = 0;
        var conditionCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var path in files)
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var file = Path.GetFileName(path);
            var entry = EntryFor(manifest, file);

            var analysis = analyzer.Analyze(new MimeAnalysisRequest
            {
                Envelope = EnvelopeFor(entry, file, bytes),
                RawMessage = bytes,
                Authentication = AuthenticationFor(entry),
                TimeProvider = TimeProvider.System,
            });

            if (!analysis.IsAnalysable)
            {
                messages.Add(new JsonObject
                {
                    ["file"] = file,
                    ["analysed"] = false,
                    ["disposition"] = analysis.Disposition.ToString(),
                });

                Count(summary, "messages_not_analysable");
                continue;
            }

            var input = new SemanticMailInput
            {
                Message = analysis.Message!,
                Dimensions = SemanticDimensions.All,
            };

            var localAssessment = await local.ClassifyAsync(input, CancellationToken.None);
            var priorAssessment = priorArm is null
                ? null
                : await priorArm.ClassifyAsync(input, CancellationToken.None);

            var priorValues = priorAssessment is null
                ? null
                : ValuesOf(priorAssessment.Evidence);

            var decision = CascadeEscalationRule.Decide(input, localAssessment.Evidence, options, priorValues);

            var askedHere = localAssessment.Evidence.Count(row =>
                row.Availability != EvidenceAvailability.NotApplicable);
            asked += askedHere;
            escalated += decision.Escalated.Count;
            Count(summary, "messages_analysed");
            Count(summary, decision.Escalates ? "messages_escalating" : "messages_not_escalating");

            foreach (var reason in decision.Reasons.Values.SelectMany(list => list))
            {
                var token = reason.Token();
                conditionCounts[token] = conditionCounts.GetValueOrDefault(token) + 1;
            }

            // Only the escalated dimensions go to the strong model, which is what the cascade does
            // and therefore what the cost of an escalation is measured against.
            IReadOnlyList<Evidence>? hostedEvidence = null;
            if (hostedArm is not null && decision.Escalates)
            {
                var hostedInput = input with { Dimensions = decision.Escalated };
                hostedEvidence = (await hostedArm.ClassifyAsync(hostedInput, CancellationToken.None)).Evidence;
                Count(summary, "hosted_calls");
            }

            messages.Add(Describe(
                file,
                entry,
                localAssessment,
                priorAssessment,
                decision,
                hostedEvidence));
        }

        var document = new JsonObject
        {
            ["batch"] = batch,
            ["corpusVersion"] = manifest["corpusVersion"]?.GetValue<int>(),
            ["seed"] = manifest["seed"]?.GetValue<int>(),
            ["profile"] = manifest["profile"]?.GetValue<string>(),
            ["messagesInBatch"] = files.Count,
            ["localEndpoint"] = CascadeBatchFactAttribute.LocalEndpoint(),
            ["localModel"] = localOptions.Model,
            ["priorEndpoint"] = priorEndpoint,
            ["hosted"] = hostedArm is null ? "absent" : "present",
            ["thresholds"] = new JsonObject
            {
                ["indecisiveHalfWidth"] = options.Trust.IndecisiveHalfWidth,
                ["disagreementTolerance"] = options.DisagreementTolerance,
                ["classifierVersion"] = options.ClassifierVersion,
                ["weightsInjected"] = "PolicyOptions.DimensionWeights",
            },
            ["summary"] = new JsonObject
            {
                ["askedDimensions"] = asked,
                ["escalatedDimensions"] = escalated,
                ["escalationRate"] = asked == 0 ? null : (double)escalated / asked,
                ["conditions"] = ToJson(conditionCounts),
                ["counts"] = ToJson(summary),
            },
            ["messages"] = messages,
        };

        Directory.CreateDirectory(outDirectory);
        var outPath = Path.Combine(
            outDirectory,
            $"local-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.json");

        await File.WriteAllTextAsync(
            outPath,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // The figure of record is printed with its population, because a rate without one is the
        // defect this fleet keeps recording.
        Console.WriteLine(
            $"cascade measurement: {summary.GetValueOrDefault("messages_analysed")} message(s), "
            + $"{asked} asked dimension(s), {escalated} escalated "
            + $"({(asked == 0 ? 0.0 : (double)escalated / asked):P2}), hosted calls "
            + $"{summary.GetValueOrDefault("hosted_calls")}, artifact {outPath}");
    }

    private static JsonObject Describe(
        string file,
        JsonNode? entry,
        SemanticAssessment local,
        SemanticAssessment? prior,
        CascadeDecision decision,
        IReadOnlyList<Evidence>? hosted)
    {
        var localRows = new JsonObject();
        foreach (var row in local.Evidence)
        {
            localRows[row.SignalId] = new JsonObject
            {
                ["availability"] = row.Availability.ToString(),
                ["value"] = row.Value,
                ["source"] = row.SourceVersion,
                ["reasons"] = new JsonArray(
                    [.. (row.Attributes ?? [])
                        .Where(attribute => attribute.Name == "reason")
                        .Select(attribute => (JsonNode)attribute.Value)]),
                ["cascade"] = decision.TokenFor(row.SignalId) is { Length: > 0 } token ? token : null,
            };
        }

        return new JsonObject
        {
            ["file"] = file,
            ["analysed"] = true,
            ["shape"] = new JsonObject
            {
                ["coverage"] = Text(entry, "coverage"),
                ["encoding"] = Text(entry, "encoding"),
                ["size"] = Text(entry, "size"),
                ["turnCharacters"] = Number(entry, "turnCharacters"),
                ["windowCharacters"] = Number(entry, "windowCharacters"),

                // CONDITIONAL members: the generator emits these only when the batch was made with
                // the flag, so they are absent on most batches rather than empty. Read with the
                // accessor that treats a shape it does not recognise as absent instead of fatal.
                ["bodyShape"] = Text(entry, "bodyShape"),
                ["quotedTailCharacters"] = Number(entry, "quotedTailCharacters"),

                // `intent` is an object with a label and a note, not a string. Reading it as one
                // threw, which is the sort of thing a manifest reader does when it assumes a shape
                // instead of looking at one.
                ["intent"] = Text(entry?["intent"], "label"),
                ["intentNote"] = Text(entry?["intent"], "note"),
                ["planted"] = new JsonArray(
                    [.. (entry?["planted"]?.AsArray() ?? new JsonArray())
                        .Select(fact => (JsonNode?)fact?["id"]?.GetValue<string>() ?? "unknown")]),
            },
            ["local"] = localRows,
            ["prior"] = prior is null ? null : ToJson(ValuesOf(prior.Evidence)),
            ["escalated"] = ToJson(decision.Reasons.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)[.. pair.Value.Select(reason => reason.Token())],
                StringComparer.Ordinal)),
            ["hosted"] = hosted is null ? null : ToJson(ValuesOf(hosted)),
        };
    }

    /// <summary>The authority (host and port) of an endpoint, so a version names WHERE it ran.</summary>
    private static string Authority(string endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Authority : endpoint;

    /// <summary>A string member, or null when the member is absent or is not a string.</summary>
    /// <remarks>
    /// Typed accessors rather than <c>GetValue&lt;T&gt;</c> at each site: the manifest is a document
    /// written by another lane, and a member whose shape moves would otherwise fail the whole
    /// measurement after the model calls have already been paid for.
    /// </remarks>
    private static string? Text(JsonNode? node, string name)
        => node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Number(JsonNode? node, string name)
        => node?[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static Dictionary<string, double> ValuesOf(IReadOnlyList<Evidence> evidence)
        => evidence
            .Where(row => row.Availability == EvidenceAvailability.Available && row.Value is not null)
            .GroupBy(row => row.SignalId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value!.Value, StringComparer.Ordinal);

    private static JsonNode? EntryFor(JsonNode manifest, string file)
        => manifest["messages"]?.AsArray()
            .FirstOrDefault(message => string.Equals(
                message?["file"]?.GetValue<string>(),
                file,
                StringComparison.Ordinal));

    private static MailEnvelope EnvelopeFor(JsonNode? entry, string file, byte[] bytes)
    {
        var submission = entry?["submission"];

        return new MailEnvelope
        {
            InternalMessageId = file,
            TenantId = "cascade-measure",
            Direction = Enum.TryParse<MailDirection>(
                submission?["direction"]?.GetValue<string>(),
                ignoreCase: true,
                out var direction)
                    ? direction
                    : MailDirection.Inbound,
            TrustedPrincipalId = "corpus",
            MailFrom = submission?["mailFrom"]?.GetValue<string>() ?? string.Empty,
            RcptTo = [.. (submission?["rcptTo"]?.AsArray() ?? new JsonArray())
                .Select(recipient => recipient?.GetValue<string>() ?? string.Empty)],
            ReceivedAt = ReceivedAt,
            MimeDigest = Convert.ToHexString(SHA256.HashData(bytes)),
            PayloadReference = "file://corpus/" + file,
        };
    }

    private static AuthenticationContext AuthenticationFor(JsonNode? entry)
    {
        var results = (entry?["submission"]?["authenticationResults"]?.AsArray() ?? new JsonArray())
            .Select(result => new AuthenticationResult
            {
                Mechanism = result?["mechanism"]?.GetValue<string>() ?? "unknown",
                Result = result?["result"]?.GetValue<string>() ?? "none",
                FromTrustedVerifier = result?["fromTrustedVerifier"]?.GetValue<bool>() ?? false,
                VerifierId = result?["verifier"]?.GetValue<string>(),
            })
            .ToList();

        return new AuthenticationContext
        {
            Results = results,
            ApprovedSenderIdentities = [],
            // Complete only when a trusted verifier reported, which is the adapter's own reading.
            ProvenanceIncomplete = !results.Any(result => result.FromTrustedVerifier),
        };
    }

    /// <summary>The variable that moves the client's assumed window, so the misconfiguration is measurable.</summary>
    /// <remarks>
    /// The same name the local adapter's own live tests read, deliberately: one variable, one meaning.
    /// A run that leaves it unset measures the deployment whose settings never reached the options,
    /// which is a state both this lane and `nimble-` have measured on real boots.
    /// </remarks>
    internal const string WindowVariable = "NIMBLE_EFFECTIVE_NUM_CTX";

    private static NimbleOptions NimbleOptionsFor(string endpoint)
    {
        var options = new NimbleOptions
        {
            Endpoint = endpoint,

            // The client's assumed window, which the Host pins through configuration. Unset it derives
            // NumCtx / 2 and refuses ordinary twelve-question mail, so a measurement that left it out
            // would report every dimension unavailable for a client-side reason and read as a model
            // result.
            EffectiveNumCtx = 65_536,
        };

        if (Environment.GetEnvironmentVariable(WindowVariable) is { Length: > 0 } configured
            && int.TryParse(configured, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var window)
            && window > 0)
        {
            options.EffectiveNumCtx = window;
        }

        return options;
    }

    private static ISemanticMailClassifier? TryBuildHostedArm()
    {
        var keyFile = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY_FILE");
        if (keyFile is not { Length: > 0 } || !File.Exists(keyFile))
        {
            return null;
        }

        // Read here, never printed, never written to the artifact, and never passed on a command
        // line. The file path reaches this process through the documented variable and nothing else.
        var key = File.ReadAllText(keyFile).Trim();
        return key.Length == 0
            ? null
            : new StyloMail.Jev.JevSemanticMailClassifier(
                new HttpClient(),
                new StyloMail.Jev.JevOptions { ApiKey = key });
    }

    private static void Count(Dictionary<string, int> counts, string key)
        => counts[key] = counts.GetValueOrDefault(key) + 1;

    private static JsonObject ToJson<T>(IReadOnlyDictionary<string, T> values)
    {
        var json = new JsonObject();
        foreach (var (key, value) in values)
        {
            json[key] = JsonValue.Create(value);
        }

        return json;
    }

    private static JsonObject ToJson(IReadOnlyDictionary<string, IReadOnlyList<string>> values)
    {
        var json = new JsonObject();
        foreach (var (key, value) in values)
        {
            json[key] = new JsonArray([.. value.Select(item => (JsonNode?)item)]);
        }

        return json;
    }
}
