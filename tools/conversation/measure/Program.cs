using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StyloMail.Adaptive.Profiles;
using StyloMail.Assessment;
using StyloMail.Assessment.Semantic;
using StyloMail.Core;
using StyloMail.Mime;
using StyloMail.Nimble;

namespace StyloMail.Conversation.Measure;

/// <summary>
/// The reproduction for the conversation lane's reported numbers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every number reported by this lane comes from here.</b> This drives the shipping adapter
/// (<see cref="NimbleSemanticMailClassifier"/>) over the shipping parser
/// (<see cref="BoundedMimeMessageAnalyzer"/>) against the real local model, so what is measured is
/// the path that ships rather than a description of it.
/// </para>
/// <para>
/// <b>It asserts almost nothing, and that is deliberate.</b> A measurement tool that fails when the
/// model says something surprising cannot report the surprise. The two things it does insist on are
/// that the provider actually answered (an <c>Unavailable</c> row is not a measurement of the
/// question) and that no call silently vanished.
/// </para>
/// </remarks>
internal static class Program
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string RepoRoot { get; } = FindRepositoryRoot();

    /// <summary>
    /// Where artifacts are written: this lane's directory under the gitignored scratch tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The artifact belongs with the instrument's lane, not in a temporary directory.</b> An
    /// artifact carries message bodies and model answers, so it is measurement data rather than
    /// source and does not belong under <c>src</c> or <c>tests</c>; but a temporary directory is
    /// worse, because it evaporates and a quoted number whose artifact is gone cannot be re-checked
    /// by anyone. That is the whole reason this fleet reads measurements instead of asserting them.
    /// </para>
    /// <para>
    /// <b>An earlier version of this default was <c>Path.GetTempPath()</c>, and that was wrong in a
    /// way worth recording:</b> it was chosen to keep artifacts out of a <c>git add -A</c>, which it
    /// did, while also making every number this lane reported unverifiable. <c>overview-</c> ruled
    /// on it. `.styloagent/scratch/` is gitignored, so it keeps both properties.
    /// Point <c>CONVERSATION_RESULTS_DIR</c> at a path to keep a run's artifacts somewhere specific.
    /// </para>
    /// </remarks>
    private static string ResultsDirectory =>
        Environment.GetEnvironmentVariable("CONVERSATION_RESULTS_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(RepoRoot, ".styloagent", "scratch", "conversation-");

    private static async Task<int> Main(string[] args)
    {
        var which = args.Length > 0 ? args[0] : "all";

        // Unbuffered progress. A long run redirected to a file buffers its own output, so a run
        // that is working and a run that is hung look identical until the process exits. This lane
        // has already lost time to that once, and the fix costs nothing.
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

        Directory.CreateDirectory(ResultsDirectory);

        // The coverage flag and the reduction reasons are set by the MIME analyzer, not by the
        // model, so this arm is deterministic and must not pay a model load to answer a question
        // the model is never consulted for. It returns before the classifier is built.
        if (which is "flag")
        {
            ContextFlagHalf();
            return 0;
        }

        // The axis sweep costs 27 model calls, so the thing it varies is printed first, with no call
        // made. If the fixture's quote block were ever missed, the sweep would spend fifteen minutes
        // measuring nine conditions that differ only in ways nobody chose.
        if (which is "axis-dry")
        {
            var (body, bare, attribution, quoted) = JevReplyParts();
            Console.WriteLine($"body({body.Length} chars):\n{body}");
            Console.WriteLine($"\n--- bare({bare.Length}):\n{bare}");
            Console.WriteLine($"\n--- attribution({attribution.Length}):\n{attribution}");
            Console.WriteLine($"\n--- quoted({quoted.Length}):\n{quoted}");
            return 0;
        }

        // The site question, answered with no model call. `overview-` refuted my canonicaliser site and
        // named the candidates; one is the MIME analyser's rendering. The analyser does not hand the
        // classifier a single body: it splits the message into BodyText and a separate QuotedText, and
        // the renderer emits both as distinct fields. So two raw messages that differ only inside a
        // quote block are not two messages with different bodies, they are two messages with different
        // QuotedText, which is a different statement about the input. This is a read of the analyser,
        // not a measurement of the model, and it is printed as a body shape rather than an answer.
        if (which is "bodyshape-dry")
        {
            BodyShapeHalf();
            return 0;
        }

        // The site experiment's four inputs and the digest they will be compared against, printed
        // with no model call. Same reason `axis-dry` exists: eight calls on a model the whole fleet
        // shares should not be spent discovering that a candidate was built wrong, or that the
        // artifact holding the digest to match is not there.
        if (which is "site-dry")
        {
            SiteDry();
            return 0;
        }

        // The site question redone with the key function the Host actually serves, and with no model
        // call at all. The site experiment compared the Host's served digest against the adapter's own
        // ComputeCacheKeyDigest, which is a different function over a different payload, so its
        // no-match was a property of the instrument rather than of the input. This one computes the
        // Host's key with SemanticCacheKey.Digest, under the Host's own options, and compares.
        if (which is "site-key")
        {
            SiteKey();
            return 0;
        }

        // The same key computation, swept over the two axes this tool cannot read off a response:
        // the tenant the Host assesses under, and the model-version string that goes into the key.
        // Both are values the Host holds internally; the request carries neither, and the response
        // reports only the resolved model, which is a different field. A hit here identifies them.
        if (which is "site-grid")
        {
            SiteGrid();
            return 0;
        }

        // The end of the reconstruction: the Host's own assessor builds the input, the pipeline's own
        // decorator keys it, and a stub stands where the model stands. No model call.
        if (which is "site-capture")
        {
            await SiteCapture(Path.Combine(ResultsDirectory, "capture"));
            return 0;
        }

        var options = new NimbleOptions
        {
            Endpoint = Environment.GetEnvironmentVariable("NIMBLE_LIVE_ENDPOINT") is { Length: > 0 } e
                ? e
                : new NimbleOptions().Endpoint,
        };

        using var recorder = new RecordingHandler { InnerHandler = new HttpClientHandler() };
        using var http = new HttpClient(recorder) { Timeout = TimeSpan.FromMinutes(5) };
        var classifier = new NimbleSemanticMailClassifier(http, options);

        var runner = new Runner(classifier, recorder, options);

        Console.WriteLine($"endpoint : {options.Endpoint}");
        Console.WriteLine($"model    : {options.Model}");
        Console.WriteLine($"num_ctx  : {options.NumCtx}");
        Console.WriteLine();

        // Warm the model and say so. The first call after a context-window change pays a model
        // load, which the survey measured at 3.98 s, and folding that into a measurement would
        // report it as the provider's latency.
        //
        // Modes that call nothing are skipped, and the reason is not tidiness. A warmup is a real
        // call on the shared endpoint, so a mode whose whole point is that it consults no model would
        // otherwise be the only traffic it sends, and a report saying "no call was made" would be
        // false. That happened: `arms-current` and the dry rewrites both warmed up while their
        // reports said zero calls. The property is now enforced here rather than asserted later.
        // The dry and key modes above return before this point, so they never reach a warmup. These
        // three do reach it and consult no model: `mime` and `arms-current` are input assembly, and
        // `site-participants-dry` prints the rewrite it would make without asking anyone about it.
        var modelFree = which is "mime" or "arms-current" or "site-participants-dry"
            or "participants-sharp-dry";

        if (modelFree)
        {
            Console.WriteLine("warmup skipped: this mode makes no call, so the process sends none.");
            Console.WriteLine();
        }
        else
        {
            var warmup = Stopwatch.StartNew();
            await runner.CallAsync(Corpus.Input(Corpus.Cases[0], null), CancellationToken.None);
            warmup.Stop();
            Console.WriteLine($"warmup (may include a model load): {warmup.ElapsedMilliseconds} ms");
            Console.WriteLine();
        }

        var ran = new List<string>();

        if (which is "all" or "mime")
        {
            MimeHalf();
            ran.Add("mime");
        }

        if (which is "all" or "context")
        {
            await ContextHalf(runner);
            ran.Add("context");
        }

        if (which is "all" or "stability")
        {
            await Stability(runner);
            ran.Add("stability");
        }

        if (which is "all" or "planted")
        {
            await Planted(runner);
            ran.Add("planted");
        }

        if (which is "all" or "window")
        {
            await Window(runner);
            ran.Add("window");
        }

        if (which is "all" or "latency")
        {
            await LatencyHalf(recorder, runner);
            ran.Add("latency");
        }

        // Not in "all": this mode costs 27 model calls and is run on its own, when the model is free
        // and the lane is ready to read the answer. "all" is the cheap reproduction set.
        if (which is "axis")
        {
            await Axis(runner);
            ran.Add("axis");
        }

        if (which is "axis2")
        {
            await AxisProse(runner);
            ran.Add("axis2");
        }

        // Four calls: the site experiment `overview-` assigned, and the cheapest of the modes that
        // need the model. It is not in "all" for the usual reason: it is a question, not a
        // reproduction, and it is run once with the answer read.
        if (which is "site")
        {
            await Site(runner);
            ran.Add("site");
        }

        // The answer column, measured on the input the Host actually builds rather than on a
        // candidate of mine. Six calls: three arms, twice each.
        if (which is "site-answer")
        {
            await SiteAnswer(runner);
            ran.Add("site-answer");
        }

        // What the answer column pointed at. Four calls, one field varied, two windows, same fixture.
        if (which is "site-provenance")
        {
            await SiteProvenance(runner);
            ran.Add("site-provenance");
        }

        // The axis M6 measured, asked again at the provenance the Host sends. Twelve calls: four
        // conditions, three repeats, interleaved.
        if (which is "axis-provenance")
        {
            await AxisProvenance(runner);
            ran.Add("axis-provenance");
        }

        // The bisection the two disagreeing runs asked for: one canonicalised field at a time, off the
        // Host's own captured input rather than off a construction of this lane's. Five calls.
        if (which is "site-envelope")
        {
            await SiteEnvelope(runner);
            ran.Add("site-envelope");
        }

        // The bisection the site experiment's no-match asked for: the same message and the same
        // provenance at three window lengths, each against the arm that took that window. Six calls.
        if (which is "site-near")
        {
            await SiteNear(runner);
            ran.Add("site-near");
        }

        // The last two things owed on the site question, taken together because the endpoint is
        // shared: is the Host input's own answer stable, and does it follow the verifier string alone?
        // Seven calls: five anchors with the digest printed every time, and two with the string moved,
        // interleaved so the second section is bracketed by the first rather than blocked after it.
        if (which is "site-close")
        {
            await SiteClose(runner);
            ran.Add("site-close");
        }

        // Every arm replayed through the assessor against the code that is in the tree right now, with
        // no model call. An arm whose digest no longer reproduces is an arm whose recorded answer was
        // taken on a question this code no longer asks, and nothing in the artifact would say so.
        if (which is "arms-current")
        {
            await ArmsCurrent();
            ran.Add("arms-current");
        }

        // The half of the participant prediction that is still open. The body half is already
        // answered by three committed arms that share an envelope, a window and a block and differ
        // only in the body, so this varies the window's participants alone, in both directions:
        // set to the message's own counterparty, and set to a third party. Eight calls.
        if (which is "site-participants")
        {
            await SiteParticipants(runner, dry: false);
            ran.Add("site-participants");
        }

        // The same build with no calls, because the first version of this cell refused after taking
        // the endpoint for a warmup: a rewrite that cannot reach every turn is a defect in the cell
        // rather than a result, and it should be found without the model in the loop.
        if (which is "site-participants-dry")
        {
            await SiteParticipants(runner, dry: true);
            ran.Add("site-participants-dry");
        }

        // The confound `site-participants` named in its own verdict. That cell wrote one address into
        // every turn, so both of its variants collapsed the window from two distinct senders to one,
        // and "the identity of the sender" is not separated from "the number of distinct senders".
        // This cell holds the count and moves the identity, then holds neither and moves the count,
        // so a difference between those two conditions is the count rather than the identity.
        if (which is "participants-sharp")
        {
            await ParticipantsSharp(runner, dry: false);
            ran.Add("participants-sharp");
        }

        // No calls, for the same reason `site-participants-dry` exists: a rewrite that cannot reach
        // every turn is a defect in the cell and must be found without the model in the loop.
        if (which is "participants-sharp-dry")
        {
            await ParticipantsSharp(runner, dry: true);
            ran.Add("participants-sharp-dry");
        }

        Console.WriteLine();
        Console.WriteLine($"completed: {string.Join(", ", ran)}");
        return 0;
    }

    // ---------------------------------------------------------------- M1, the deterministic half

    /// <summary>
    /// What supplying a window changes before any model is consulted.
    /// </summary>
    /// <remarks>
    /// The coverage reduction and the deterministic thread signal are both decided in the MIME
    /// layer, so they are measurable with no model call and no run-to-run variation. If the answer
    /// to "does supplying a window change an outcome" were only ever asked of the model, this half
    /// would be the part that is actually reproducible, and it is separated for that reason.
    /// </remarks>
    private static void MimeHalf()
    {
        Console.WriteLine("== M1a: what a supplied window changes deterministically (no model call) ==");

        var rows = new List<object>();
        var window = Threads.All[0].PriorTurns;

        foreach (var caseName in Corpus.Cases)
        {
            // Two provenance conditions, because with no authentication the coverage stays reduced
            // for an unrelated reason and the conversation's own effect is not observable.
            var bare = (Without: Summarise(Corpus.Analyze(caseName, null)), With: Summarise(Corpus.Analyze(caseName, window)));
            var auth = (Without: Summarise(Corpus.Analyze(caseName, null, authenticated: true)), With: Summarise(Corpus.Analyze(caseName, window, authenticated: true)));

            Console.WriteLine($"{caseName}");
            Console.WriteLine(
                $"    no auth   missing {bare.Without.Missing,-5} -> {bare.With.Missing,-5}  "
                + $"coverage {bare.Without.Coverage,-16} -> {bare.With.Coverage,-16}  "
                + $"reasons {bare.Without.CoverageDetail}  =>  {bare.With.CoverageDetail}");
            Console.WriteLine(
                $"    auth      missing {auth.Without.Missing,-5} -> {auth.With.Missing,-5}  "
                + $"coverage {auth.Without.Coverage,-16} -> {auth.With.Coverage,-16}  "
                + $"reasons {auth.Without.CoverageDetail}  =>  {auth.With.CoverageDetail}");

            rows.Add(new
            {
                @case = caseName,
                no_authentication = new { without_context = bare.Without, with_context = bare.With },
                with_authentication = new { without_context = auth.Without, with_context = auth.With },
            });
        }

        Write("m1a-mime-half", new { window_turns = window.Count, cases = rows });
        Console.WriteLine();
    }

    private sealed record MimeSummary(
        bool Missing,
        string Coverage,
        string? CoverageDetail,
        string Thread,
        int? ThreadValue);

    private static MimeSummary Summarise(MimeAnalysisResult result)
    {
        var evidence = result.Evidence;
        var coverage = evidence.FirstOrDefault(e => e.SignalId == MimeSignals.AnalysisCoverage);
        var thread = evidence.FirstOrDefault(e => e.SignalId == MimeSignals.ThreadHeaderConsistency);

        return new MimeSummary(
            result.Coverage.ConversationContextMissing,
            coverage?.Availability.ToString() ?? "absent",
            coverage?.Attributes is { Count: > 0 } attributes
                ? string.Join(",", attributes.Select(a => $"{a.Name}={a.Value}"))
                : null,
            thread?.Availability.ToString() ?? "absent",
            thread?.Value is { } v ? (int)v : null);
    }

    // ---------------------------------------------------------------- M1, the model half

    /// <summary>
    /// The same message with and without a window, through the real adapter.
    /// </summary>
    private static async Task ContextHalf(Runner runner)
    {
        Console.WriteLine("== M1b: the same message with and without a window (real model) ==");

        // The reply fixture already carries threading headers, so it is the case where a window is
        // most plausible. Every other case gets the same window supplied anyway, because the
        // question is what supplying a window does to a message that did not have one.
        var window = Threads.All[0].PriorTurns;

        var rows = new List<object>();

        foreach (var caseName in Corpus.Cases)
        {
            var without = await runner.CallAsync(Corpus.Input(caseName, null), CancellationToken.None);
            var with = await runner.CallAsync(Corpus.Input(caseName, window), CancellationToken.None);

            Console.WriteLine(
                $"{caseName,-26} asked {without.Asked} -> {with.Asked}  "
                + $"continuity {without.Continuity,-16} -> {with.Continuity,-16}  "
                + $"tokens {without.PromptTokens} -> {with.PromptTokens}");

            var moved = without.Codes
                .Where(p => with.Codes.TryGetValue(p.Key, out var code) && !string.Equals(code, p.Value, StringComparison.Ordinal))
                .Select(p => $"{p.Key}: {p.Value} -> {with.Codes[p.Key]}")
                .ToList();

            Console.WriteLine($"{"",-26} dimensions that moved: {moved.Count}");
            foreach (var change in moved)
            {
                Console.WriteLine($"{"",-28} {change}");
            }

            rows.Add(new
            {
                @case = caseName,
                without_context = without,
                with_context = with,
                moved,
            });
        }

        Write("m1b-context-half", new
        {
            window_turns = window.Count,
            window_characters = window.Sum(t => t.Length),
            cases = rows,
        });
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- M2, stability

    /// <summary>
    /// Whether the same pair of turns, asked repeatedly, gives the same answer.
    /// </summary>
    /// <remarks>
    /// <b>Distribution, not a single reading.</b> A change question that flips on repeat makes a
    /// thread's history meaningless, and one repeat cannot show a flip. The whole answer vector is
    /// reported rather than only the continuity row, because supplying a window rewrites the prompt
    /// for every dimension, so a window can destabilise an answer that has nothing to do with
    /// conversation.
    /// </remarks>
    private static async Task Stability(Runner runner)
    {
        const int repeats = 10;

        Console.WriteLine($"== M2: stability over {repeats} repeats of the same input ==");

        var window = Threads.All[0].PriorTurns;
        var rows = new List<object>();

        // The 1-turn and 3-turn windows carry the same first turn, so a difference between them is
        // attributable to the window's length rather than to a different conversation. That pair is
        // here because the end-to-end run answered A on one window and B on another for the same
        // message, and a single reading cannot say which of the two readings is the stable one.
        var conditions = new (string Name, SemanticMailInput Input)[]
        {
            ("reply-in-thread, no window", Corpus.Input("reply-in-thread", null)),
            ("reply-in-thread, 1-turn window", Corpus.Input("reply-in-thread", [window[0]])),
            ("reply-in-thread, 3-turn window", Corpus.Input("reply-in-thread", window)),
            ("credential-request, 3-turn window", Corpus.Input("credential-request", window)),
        };

        foreach (var (name, input) in conditions)
        {
            var answers = new List<CallResult>();
            for (var i = 0; i < repeats; i++)
            {
                answers.Add(await runner.CallAsync(input, CancellationToken.None));
            }

            // Per dimension, the set of codes seen across the repeats. A dimension with one entry
            // is stable; more than one is a flip and is reported with the split.
            var unstable = new List<object>();
            var stableCount = 0;

            foreach (var dimension in answers[0].Codes.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var distribution = answers
                    .GroupBy(a => a.Codes.TryGetValue(dimension, out var c) ? c : "?")
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

                if (distribution.Count == 1)
                {
                    stableCount++;
                    continue;
                }

                unstable.Add(new { dimension, distribution });
                Console.WriteLine($"  UNSTABLE {dimension}: {string.Join(", ", distribution.Select(p => $"{p.Key} x{p.Value}"))}");
            }

            var continuityDistribution = answers
                .GroupBy(a => a.Continuity)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            Console.WriteLine(
                $"{name,-28} dimensions={answers[0].Codes.Count} stable={stableCount} "
                + $"unstable={unstable.Count} continuity={string.Join(",", continuityDistribution.Select(p => $"{p.Key} x{p.Value}"))}");

            rows.Add(new
            {
                condition = name,
                repeats,
                dimensions = answers[0].Codes.Count,
                stable_dimensions = stableCount,
                unstable,
                continuity_distribution = continuityDistribution,
                codes_per_repeat = answers.Select(a => a.Codes).ToList(),
                prompt_tokens = answers.Select(a => a.PromptTokens).ToList(),
            });
        }

        Write("m2-stability", new { repeats, conditions = rows });
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- M3, agreement

    /// <summary>
    /// Whether the question reports a change that was planted, and stays quiet when none was.
    /// </summary>
    /// <remarks>
    /// The three pairs are run interleaved rather than pair by pair, so a model that drifted over
    /// the run would not land entirely on one arm of one pair.
    /// </remarks>
    private static async Task Planted(Runner runner)
    {
        // Three, not five: M2 measured zero variance in 40 calls, so the repeats here confirm a
        // distribution rather than hunt for noise, and every repeat is a model call that costs
        // minutes at the observed per-call latency.
        const int repeats = 3;

        Console.WriteLine($"== M3: agreement with a planted change, {repeats} repeats per arm ==");
        Console.WriteLine(
            "prompt shape: the adapter's default rendered state, held identical across both arms. "
            + "Control and planted differ only in the latest message body, because adding a window "
            + "moves unrelated dimensions deterministically and a quiet arm has to compare within "
            + "one fixed shape.");

        var rows = new List<object>();

        var schedules = new List<(Threads.Pair Pair, string Arm, SemanticMailInput Input)>();
        foreach (var pair in Threads.All)
        {
            for (var i = 0; i < repeats; i++)
            {
                schedules.Add((pair, "control", Threads.Input(pair, pair.PriorTurns, pair.Control)));
                schedules.Add((pair, "planted", Threads.Input(pair, pair.PriorTurns, pair.Planted)));
            }
        }

        var answers = new Dictionary<(string Pair, string Arm), List<CallResult>>();
        var done = 0;
        foreach (var (pair, arm, input) in schedules)
        {
            var key = (pair.Name, arm);
            if (!answers.TryGetValue(key, out var list))
            {
                answers[key] = list = [];
            }

            var result = await runner.CallAsync(input, CancellationToken.None);
            list.Add(result);

            // Printed as each call lands rather than after the set, because the per-call latency
            // here is the difference between "slow" and "hung" and a run that only reports at the
            // end cannot tell them apart.
            done++;
            Console.WriteLine(
                $"  [{done,2}/{schedules.Count}] {pair.Name,-26} {arm,-8} "
                + $"tokens={result.PromptTokens,-6} continuity={result.Continuity,-16} "
                + $"answered={result.Answered}/{result.Asked} {result.ElapsedMs} ms"
                + (result.UnavailableReason is { Length: > 0 } why ? $" {why}" : ""));
            Console.Out.Flush();
        }

        foreach (var pair in Threads.All)
        {
            var control = answers[(pair.Name, "control")];
            var planted = answers[(pair.Name, "planted")];

            var controlDist = control.GroupBy(a => a.Continuity).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var plantedDist = planted.GroupBy(a => a.Continuity).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            var changedElsewhere = MovedBetweenArms(control[0].Codes, planted[0].Codes);

            Console.WriteLine(
                $"{pair.Name,-26} planted={pair.PlantedKind}");
            Console.WriteLine(
                $"{"",-26} continuity control={Format(controlDist)} planted={Format(plantedDist)}");
            Console.WriteLine(
                $"{"",-26} dimensions differing between arms: {changedElsewhere.Count}"
                + (changedElsewhere.Count > 0 ? " -> " + string.Join(", ", changedElsewhere) : ""));

            rows.Add(new
            {
                pair = pair.Name,
                planted_kind = pair.PlantedKind,
                control_continuity = controlDist,
                planted_continuity = plantedDist,
                control_codes = control.Select(a => a.Codes).ToList(),
                planted_codes = planted.Select(a => a.Codes).ToList(),
                dimensions_differing_between_arms = changedElsewhere,
            });
        }

        Write("m3-planted", new
        {
            repeats,
            prompt_shape = "adapter default rendered state, identical across control and planted",
            pairs = rows,
        });
        Console.WriteLine();
    }

    private static List<string> MovedBetweenArms(
        Dictionary<string, string> a,
        Dictionary<string, string> b)
        => a
            .Where(p => b.TryGetValue(p.Key, out var code) && !string.Equals(code, p.Value, StringComparison.Ordinal))
            .Select(p => $"{p.Key}: {p.Value} -> {b[p.Key]}")
            .ToList();

    // ---------------------------------------------------------------- M4, the cost of a window

    /// <summary>
    /// What a window costs in prompt tokens at N = 1, 3, 5 and 10 turns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two turn sizes, because the cost is a rate and not a constant.</b> The same turn count
    /// costs very different amounts depending on how long a turn is, and a default chosen from one
    /// turn size would be a default that silently changes meaning in a thread with longer messages.
    /// </para>
    /// <para>
    /// The window is not free even when it fits: the body budget is what pays for it, and
    /// <c>FitPrompt</c> shortens only the body. So each row also reports whether the message body
    /// was shortened, and a window that cannot be made to fit at all comes back <c>Unavailable</c>
    /// rather than as a shortened answer.
    /// </para>
    /// </remarks>
    private static async Task Window(Runner runner)
    {
        // The window a call actually gets is half the requested one on the reference server, and this
        // sweep is the one that can reach it. Every row is therefore checked against the APPLIED
        // window: a prompt that was silently cut comes back with done_reason "stop", no warning, and
        // a prompt_eval_count describing the shortened prompt, so comparing against the requested
        // window would read a truncated answer as a clean one and a saturated count as a real cost.
        var appliedWindow = runner.Options.AppliedContextWindow;
        var requestedWindow = runner.Options.NumCtx;

        Console.WriteLine("== M4: the token cost of a window ==");
        Console.WriteLine(
            $"measuring against the APPLIED window: {appliedWindow} tokens "
            + $"(requested num_ctx={requestedWindow}, effective override="
            + $"{runner.Options.EffectiveNumCtx?.ToString(CultureInfo.InvariantCulture) ?? "none"}). "
            + $"A row whose evaluated count reaches {appliedWindow} was truncated and is reported "
            + "as NOT a data point.");

        int[] turnCounts = [0, 1, 3, 5, 10];
        int[] turnSizes = [400, 2_000];

        var rows = new List<object>();

        foreach (var turnSize in turnSizes)
        {
            foreach (var n in turnCounts)
            {
                var turns = SyntheticTurns(n, turnSize);
                var input = Corpus.Input("reply-in-thread", turns.Count == 0 ? null : turns);

                var result = await runner.CallAsync(input, CancellationToken.None);

                // Three outcomes, and conflating any two of them is how a sweep reports a cost it
                // never measured. An Unavailable row has no evaluated count at all, so it is not a
                // data point and not a saturation either: `null >= appliedWindow` is false, so a
                // single flag would silently stamp a refused call as a measured one. That is the
                // defect an earlier version of this loop had.
                var evaluated = result.PromptTokens;
                var saturated = evaluated is { } count && count >= appliedWindow;
                var refused = evaluated is null;
                var isDataPoint = !refused && !saturated;

                Console.WriteLine(
                    $"turns={n,-3} chars/turn~{turnSize,-5} context_chars={turns.Sum(t => t.Length),-6} "
                    + $"evaluated_tokens={evaluated?.ToString(CultureInfo.InvariantCulture) ?? "-",-6} "
                    + (saturated ? "AT-CEILING/NOT-A-DATA-POINT " : refused ? "REFUSED/NOT-A-DATA-POINT " : "")
                    + $"continuity={result.Continuity,-16} "
                    + $"answered={result.Answered}/{result.Asked} body_shortened={result.BodyShortened}");

                rows.Add(new
                {
                    turn_count = n,
                    turn_target_characters = turnSize,
                    context_characters = turns.Sum(t => t.Length),
                    context_turns = turns.Count,
                    evaluated_tokens = evaluated,
                    applied_window = appliedWindow,
                    requested_window = requestedWindow,
                    at_or_over_applied_window = saturated,
                    refused_no_request_sent = refused,
                    is_data_point = isDataPoint,
                    output_tokens = result.OutputTokens,
                    continuity = result.Continuity,
                    answered = result.Answered,
                    asked = result.Asked,
                    body_shortened = result.BodyShortened,
                    elapsed_ms = result.ElapsedMs,
                    unavailable_reason = result.UnavailableReason,
                });
            }

            Console.WriteLine();
        }

        Write("m4-window-cost", new
        {
            measured_against = "applied_window",
            requested_num_ctx = requestedWindow,
            applied_window = appliedWindow,
            effective_num_ctx_override = runner.Options.EffectiveNumCtx,
            max_body_characters = runner.Options.MaxBodyCharacters,
            rows,
        });
        Console.WriteLine();
    }

    /// <summary>
    /// Plausible prior turns of roughly a given length, so the cost measured is a rate per
    /// character rather than a property of one sentence repeated.
    /// </summary>
    private static IReadOnlyList<string> SyntheticTurns(int count, int targetCharacters)
    {
        const string seed =
            "Hello, following up on the order we discussed last week. The delivery window you gave "
            + "us is still workable, and the quantities on the revised schedule look right. Nothing "
            + "on our side has changed since the last message, so please continue as planned and let "
            + "us know if anything needs confirming before the next shipment goes out. ";

        var turns = new List<string>(count);

        for (var i = 0; i < count; i++)
        {
            var builder = new StringBuilder();
            builder.Append($"From: alice@example.example\nSubject: Re: order\n\nTurn {i + 1}. ");

            while (builder.Length < targetCharacters)
            {
                builder.Append(seed);
            }

            turns.Add(builder.ToString(0, Math.Min(builder.Length, targetCharacters)));
        }

        return turns;
    }

    // ---------------------------------------------------------------- the coverage flag

    /// <summary>
    /// What the analyzer records for no context, an empty supplied window, and a partial window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deterministic and model-free, because none of it is a model output.</b>
    /// <c>ConversationContextMissing</c> and the <c>no-conversation-context</c> reduction reason are
    /// written by <c>BoundedMimeMessageAnalyzer</c>; whether the continuity question is asked at all
    /// is decided by the classifier's own predicate over the same list. The model never sees the
    /// difference between "zero turns" and "no turns", so a model call here would measure nothing
    /// about the flag. This arm returns before the classifier is constructed and pays no model load.
    /// </para>
    /// <para>
    /// The conditions are the three states a store can hand the Host: <c>null</c> (the store was not
    /// consulted), an empty list (the store looked and there are no priors), and a list of turns.
    /// The coverage availability and its attributes are read off the real
    /// <c>mime.analysis_coverage</c> row rather than recomputed here, so what this prints is what a
    /// decision would carry.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Prints what the MIME analyser hands the classifier for each of the axis arms' raw messages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No model call and no window: this is a read of the analyser, so it says what the input IS and
    /// not what the classifier did with it. It builds each arm's raw bytes exactly the way
    /// <c>measure-e2e.sh</c> builds them, replacing the body after the header/body boundary and
    /// leaving the headers, the threading fields and the Message-ID alone.
    /// </para>
    /// <para>
    /// The point is that <see cref="MailAnalysisInput"/> is not one body. <c>BodyText</c> and
    /// <c>QuotedText</c> are separate fields, and <c>QuotedHistory</c> decides where one ends and the
    /// other begins. The renderer emits both, and the canonicaliser keys on both. So "arm2 carries a
    /// quote block and arm5 does not" is not "their bodies differ": it may be that their
    /// <c>BodyText</c> is identical and only <c>QuotedText</c> moves, which is a different claim about
    /// what the classifier was asked.
    /// </para>
    /// </remarks>
    private static void BodyShapeHalf()
    {
        var fixture = File.ReadAllText(Path.Combine(Corpus.FixturesDirectory, "reply-in-thread.eml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var boundary = fixture.IndexOf("\n\n", StringComparison.Ordinal);
        if (boundary < 0)
        {
            throw new InvalidOperationException("The fixture has no header/body separator.");
        }

        var header = fixture[..boundary];

        var arms = new (string Name, string? Body)[]
        {
            ("arm2-verbatim ", null),
            ("arm5-restating", "Thanks for the update. Two working days is fine."),
            ("arm6-advancing", "Thanks, the parcel arrived this morning."),
            ("arm7-overlap  ", "Thanks, Order NW-4482 arrived this morning."),
        };

        Console.WriteLine("== body shape: what the analyser hands the classifier ==");
        Console.WriteLine("no model call is made; this reads the analyser");

        foreach (var (name, body) in arms)
        {
            var raw = Encoding.UTF8.GetBytes(body is null ? fixture : header + "\n\n" + body + "\n");
            var result = new BoundedMimeMessageAnalyzer().Analyze(new MimeAnalysisRequest
            {
                Envelope = new MailEnvelope
                {
                    InternalMessageId = $"conversation-bodyshape-{name.Trim()}",
                    TenantId = "measure",
                    Direction = MailDirection.Inbound,
                    TrustedPrincipalId = "conversation-measure",
                    MailFrom = "sender@example.test",
                    RcptTo = ["recipient@example.test"],
                    ReceivedAt = DateTimeOffset.UnixEpoch,
                    MimeDigest = Convert.ToHexStringLower(SHA256.HashData(raw)),
                    PayloadReference = PayloadReferences.Ephemeral,
                },
                RawMessage = raw,
            });

            if (!result.IsAnalysable || result.Message is null)
            {
                Console.WriteLine($"{name}: NOT ANALYSABLE ({result.Disposition})");
                continue;
            }

            var message = result.Message;
            Console.WriteLine($"{name}: raw={raw.Length}B  BodyText={message.BodyText.Length}  "
                + $"QuotedText={message.QuotedText?.Length ?? 0}");
            Console.WriteLine($"                 body  |{Clip(message.BodyText)}|");
            Console.WriteLine($"                 quoted|{Clip(message.QuotedText)}|");
        }

        static string Clip(string? text)
            => text is null ? "(null)" : text.Length <= 72
                ? text.Replace("\n", "\\n", StringComparison.Ordinal)
                : text[..72].Replace("\n", "\\n", StringComparison.Ordinal) + "...";
    }

    private static void ContextFlagHalf()
    {
        Console.WriteLine("== coverage flag: null vs empty-supplied vs partial window ==");
        Console.WriteLine(
            "prompt shape: not applicable. Nothing here consults the model; the flag and the "
            + "reduction reasons are the analyzer's.");
        Console.WriteLine();

        var oneTurn = new[]
        {
            "From: a@example.example\nSubject: Re: order NW-4482\n\nPrior turn one.",
        };

        var threeTurns = new[]
        {
            oneTurn[0],
            "From: b@example.example\nSubject: Re: order NW-4482\n\nPrior turn two.",
            "From: a@example.example\nSubject: Re: order NW-4482\n\nPrior turn three.",
        };

        var conditions = new (string Name, IReadOnlyList<string>? Context)[]
        {
            ("null, store not consulted", null),
            ("empty list, store found no priors", []),
            ("1-turn window (partial)", oneTurn),
            ("3-turn window", threeTurns),
        };

        Console.WriteLine(
            $"{"condition",-36} {"missing",-8} {"turns",-6} {"asked",-6} {"coverage",-17} reduced");

        foreach (var (name, context) in conditions)
        {
            // Provenance is completed here so the only varying reduction reason is the
            // conversation one; otherwise the incomplete-provenance reason confounds the column.
            var analysis = Corpus.Analyze("reply-in-thread", context, authenticated: true);

            // The predicate the classifier partitions on, read off the same field it reads.
            var asked = analysis.Message?.ConversationContext is { Count: > 0 };

            var row = analysis.Evidence.FirstOrDefault(
                e => string.Equals(e.SignalId, MimeSignals.AnalysisCoverage, StringComparison.Ordinal));

            var reduced = row?.Attributes is { Count: > 0 } attributes
                ? string.Join(",", attributes.Select(a => a.Value))
                : "none";

            Console.WriteLine(
                $"{name,-36} {analysis.Coverage.ConversationContextMissing,-8} "
                + $"{context?.Count ?? 0,-6} {asked,-6} "
                + $"{row?.Availability.ToString() ?? "absent",-17} {reduced}");
        }

        Console.WriteLine();
        Console.WriteLine(
            "asked=false means the continuity question is never put to the model and the row is "
            + "NotApplicable; asked=true means the row is a real answer.");
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- the latency split

    /// <summary>
    /// Splits a windowed call's wall-clock cost into prefill and generation, from Ollama's counters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Because "46 seconds per call" is two different findings.</b> A cost that grows with the
    /// window argues for a shorter window; a cost that grows with the twelve answers does not, and
    /// is paid whether or not context is supplied. The adapter surfaces token counts but not
    /// durations, so this reads the counters off the wire through the recording handler.
    /// </para>
    /// <para>
    /// The counters are the server's own, so they describe the work the server did rather than what
    /// the client waited for. When the two disagree the difference is transport and scheduling, and
    /// both are printed so the gap is visible rather than assumed away.
    /// </para>
    /// </remarks>
    private static async Task LatencyHalf(RecordingHandler recorder, Runner runner)
    {
        Console.WriteLine("== latency split: prefill versus generation ==");
        Console.WriteLine();

        const int repeats = 3;

        var conditions = new (string Name, IReadOnlyList<string>? Context)[]
        {
            ("no window", null),
            ("3-turn window, ~400 chars/turn", SyntheticTurns(3, 400)),
        };

        Console.WriteLine(
            $"{"condition",-32} {"run",-4} {"prompt_n",-9} {"prefill_ms",-11} {"eval_n",-7} "
            + $"{"gen_ms",-8} {"load_ms",-9} {"total_ms",-9} adapter_ms done");

        var rows = new List<object>();

        foreach (var (name, context) in conditions)
        {
            for (var run = 1; run <= repeats; run++)
            {
                var result = await runner.CallAsync(
                    Corpus.Input("reply-in-thread", context), CancellationToken.None);

                var (promptN, prefillMs, evalN, genMs, loadMs, totalMs, done) =
                    ReadCounters(recorder.LastResponseBody);

                Console.WriteLine(
                    $"{name,-32} {run,-4} {Show(promptN),-9} {Show(prefillMs),-11} {Show(evalN),-7} "
                    + $"{Show(genMs),-8} {Show(loadMs),-9} {Show(totalMs),-9} {result.ElapsedMs,-11} {done}");

                rows.Add(new
                {
                    condition = name,
                    run,
                    context_turns = context?.Count ?? 0,
                    adapter_prompt_tokens = result.PromptTokens,
                    adapter_output_tokens = result.OutputTokens,
                    adapter_elapsed_ms = result.ElapsedMs,
                    prompt_eval_count = promptN,
                    prompt_eval_ms = prefillMs,
                    eval_count = evalN,
                    eval_ms = genMs,
                    load_ms = loadMs,
                    total_ms = totalMs,
                    done_reason = done,
                    continuity = result.Continuity,
                });

                Console.Out.Flush();
            }

            Console.WriteLine();
        }

        Write("m4b-latency-split", new { repeats, rows });
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- M6, the A/B axis

    /// <summary>
    /// Which property of the turn makes continuity answer A rather than B.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The axis is the question, and nothing downstream of it is safe to build.</b> M2 measured the
    /// committed <c>reply-in-thread</c> answering <b>A</b> ten times out of ten at a three-turn window.
    /// M3 measured this lane's own three coherent, in-thread pairs answering <b>B</b> three times out
    /// of three at the same three-turn window, byte-identical priors included. Same window, same
    /// subject, same turn count, opposite answers. Meanwhile <c>overview-</c> refuted the content
    /// hypothesis from a verbatim replay: the rendered shape moves the answer (structured state gives
    /// B, a bare body gives A), and it refuted the either/or reading of the criterion.
    /// </para>
    /// <para>
    /// <b>One property at a time, with the constructor held constant.</b> The two fixtures differ in
    /// two ways at once: how the MIME is built, and what the latest body says. So the first condition
    /// reproduces M2's A exactly, through the corpus constructor, as a self-calibration for this run;
    /// everything after it goes through <see cref="Threads.Input"/> so the envelope is byte-identical
    /// and only the body varies. Without that, a flip could be attributed to the header set and the
    /// answer would still be unexplained.
    /// </para>
    /// <para>
    /// <b>The leading candidate is the inline quote.</b> The committed fixture's body carries a
    /// <c>On ... wrote:</c> attribution line and a <c>&gt;</c>-prefixed copy of the prior turn; this
    /// lane's bodies carry neither. That is a property of the message that a criterion reading
    /// "consistent with the prior exchange" could plausibly key on, and it is one edit to remove. It is
    /// a candidate and not a conclusion: this measurement is what decides it, and the sweep also
    /// separates the attribution marker from the quoted text, which is the difference between the
    /// model noticing a *marker* and the model noticing *repetition*.
    /// </para>
    /// </remarks>
    private static async Task Axis(Runner runner)
    {
        // Three, not ten: M2 already measured zero variance in 40 calls over four conditions, so a
        // flip here is a flip in the question rather than noise, and every repeat is a model call
        // costing tens of seconds. Nine conditions at three repeats is 27 calls.
        const int repeats = 3;

        var window = Threads.All[0].PriorTurns;
        var pair = Threads.All[2];

        var (jevBody, jevBare, attribution, quoted) = JevReplyParts();
        var control = pair.Control;

        Console.WriteLine($"== M6: the A/B axis, {repeats} repeats per condition ==");
        Console.WriteLine(
            "The window is held byte-identical across every condition that uses Threads.Input: the "
            + "same three prior turns M2 and M3 both used. Only the latest body varies. The first "
            + "condition calibrates the run against M2's A.");
        Console.WriteLine();

        var conditions = new (string Name, string Property, SemanticMailInput Input)[]
        {
            ("A0-verbatim-via-corpus", "reproduces M2's A condition exactly, through the corpus constructor",
                Corpus.Input("reply-in-thread", window)),
            ("A1-verbatim-via-threads", "the same body through this lane's constructor, so the envelope is held",
                Threads.Input(pair, window, jevBody)),
            ("A2-bare-reply", "the quote block removed, nothing else changed",
                Threads.Input(pair, window, jevBare)),
            ("A3-attribution-only", "the On...wrote: marker kept, the quoted text removed",
                Threads.Input(pair, window, jevBare + "\n\n" + attribution)),
            ("A4-quoted-lines-only", "the quoted text kept, the On...wrote: marker removed",
                Threads.Input(pair, window, jevBare + "\n\n" + quoted)),
            ("A5-quote-unrelated", "a quote of a sentence that is in no window, so repetition and marker differ",
                Threads.Input(pair, window, jevBare + "\n\n" + attribution
                    + "\n> Please rotate the API credentials before the end of the quarter.")),
            ("B0-control-body", "this lane's own B body, no quote and no marker",
                Threads.Input(pair, window, control)),
            ("B1-control-plus-quote", "the same B body with the full quote block appended",
                Threads.Input(pair, window, control + "\n\n" + attribution + "\n" + quoted)),
            ("B2-control-plus-attribution", "the same B body with only the marker appended",
                Threads.Input(pair, window, control + "\n\n" + attribution)),
        };

        var answers = new Dictionary<string, List<CallResult>>(StringComparer.Ordinal);
        var done = 0;

        foreach (var round in Enumerable.Range(0, repeats))
        {
            // Interleaved rather than condition by condition, so a model that drifted across the run
            // would not land entirely on one side of the axis. That is the same reason M3 interleaves.
            foreach (var (name, _, input) in conditions)
            {
                var result = await runner.CallAsync(input, CancellationToken.None);
                if (!answers.TryGetValue(name, out var list))
                {
                    answers[name] = list = [];
                }

                list.Add(result);
                done++;

                Console.WriteLine(
                    $"  [{done,2}/{conditions.Length * repeats}] r{round + 1} {name,-26} "
                    + $"tokens={result.PromptTokens,-6} continuity={result.Continuity,-16} "
                    + $"answered={result.Answered}/{result.Asked} {result.ElapsedMs} ms");
                Console.Out.Flush();
            }
        }

        var reference = answers["A0-verbatim-via-corpus"][0].Codes;

        Console.WriteLine();
        Console.WriteLine($"{"condition",-26} {"property",-14} {"continuity",-20} tokens");
        Console.WriteLine(new string('-', 106));

        var rows = new List<object>();

        foreach (var (name, property, _) in conditions)
        {
            var set = answers[name];
            var distribution = set
                .GroupBy(a => a.Continuity)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            var moved = MovedBetweenArms(reference, set[0].Codes);

            Console.WriteLine(
                $"{name,-26} {property[..Math.Min(14, property.Length)],-14} "
                + $"{Format(distribution),-20} {string.Join("/", set.Select(a => Show(a.PromptTokens)))}");

            rows.Add(new
            {
                condition = name,
                property,
                continuity_distribution = distribution,
                prompt_tokens = set.Select(a => a.PromptTokens).ToList(),
                codes = set.Select(a => a.Codes).ToList(),
                dimensions_differing_from_A0 = moved,
                elapsed_ms = set.Select(a => a.ElapsedMs).ToList(),
                availability = set.Select(a => a.Answered + "/" + a.Asked).ToList(),
            });
        }

        Write("m6-axis", new
        {
            repeats,
            window_turns = window.Count,
            window_source = "Threads.All[0].PriorTurns, byte-identical to M2's three-turn condition",
            constructor_held = "Threads.Input for every condition except A0, which is the corpus constructor and is the calibration",
            conditions = rows,
        });
        Console.WriteLine();
    }

    /// <summary>
    /// Which of two remaining candidates is the axis: how long the body is, or whether it restates
    /// the window rather than advancing past it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A 2x2, because the two candidates are confounded in the material that produced the question.</b>
    /// The A body is short and restates the window ("Two working days" is in the window's first turn);
    /// this lane's B body is long and reports a state the window does not contain (the parcel arrived).
    /// Either property could be doing the work, and nothing in M6 separates them.
    /// </para>
    /// <para>
    /// <b>So the four cells are filled deliberately.</b> If the axis is restatement then both restating
    /// bodies answer A and both advancing bodies answer B, at either length. If the axis is length then
    /// both short bodies answer A and both long bodies answer B, whatever they say. The cells are
    /// length-matched in pairs rather than exactly equal, and each row prints its character count so the
    /// match can be checked rather than trusted.
    /// </para>
    /// <para>
    /// <b>A third property is guarded by two extra rows.</b> Length and restatement are both compatible
    /// with a model that has simply stopped reading: if an unrelated message also answers A when it is
    /// short, then neither candidate explains the axis and the dimension is not answering the question
    /// at all. Those two rows are the guard, and they are the reason this sweep can distinguish
    /// "the criterion means contained-in" from "the criterion has degenerated".
    /// </para>
    /// </remarks>
    /// <summary>The end-to-end arm the site experiment compares itself against.</summary>
    private const string SiteArm = "arm2-reply-in-thread-mismatched";

    /// <summary>
    /// The verifier id the end-to-end suite submits, which the Host records in the provider's state.
    /// </summary>
    /// <remarks>
    /// In the state, not a label on it: each trusted result reaches the provider as
    /// <c>(mechanism, result, verifier)</c>, so a candidate that gets this wrong is a different
    /// question with a different digest. <c>measure-e2e.sh</c> submits exactly this string, and the
    /// Host copies the caller's value through unchanged.
    /// </remarks>
    private const string HostVerifierId = "conversation-measure";

    /// <summary>The tenant the end-to-end suite's Host assesses under.</summary>
    /// <remarks>
    /// <b>The request does not carry this, so it has to be read from the deployment.</b>
    /// <c>POST /v1/assessments</c> takes no tenant field: <c>MessageIngress.Prepare</c> sets the
    /// envelope's tenant from the authenticated principal's claim, and rejects a request whose body
    /// disagrees with it. The harness Host that serves every arm configures one principal, whose
    /// tenant is this string (<c>ux-scripts/console-harness.sh</c>, the
    /// <c>StyloMail__Auth__Principals__0__</c> block). Tenant is the first field the canonicaliser
    /// writes, so a candidate that keeps this lane's own tenant cannot reproduce a digest the Host
    /// served, however exactly it reproduces everything else.
    /// </remarks>
    private const string HostTenantId = "harness";

    /// <summary>This lane's own tenant for its measurements, which is not the Host's.</summary>
    private const string MeasureTenantId = "measure";

    /// <summary>
    /// The four inputs the site experiment compares, built in one place so the dry check and the run
    /// cannot describe different experiments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three profile shapes, because Core does not treat two of them as one input.</b> "No
    /// profile" and "we looked and found nothing" produce different cache keys, and a populated
    /// profile is a third. The reason the absent shape matters beyond this fixture: if it is not the
    /// shape the product sends, then every classifier-level figure this project holds was measured on
    /// a question the product does not ask.
    /// </para>
    /// <para>
    /// <b>The Host's shape is a question, not an assumption.</b> <c>BuildBehaviouralProfile</c>
    /// returns null only when there is no snapshot row at all, and the encoder returns an unavailable
    /// profile rather than null once a row exists, so both are live and neither may be assumed.
    /// </para>
    /// <para>
    /// <b>One populated profile, and it is one rather than "the" populated profile.</b> Any set of
    /// counts is a different question from any other, so what this shape establishes is that a
    /// populated profile is a third distinct input, not what a particular correspondent scores.
    /// </para>
    /// <para>
    /// <b>Provenance is held at the Host's value in the first three</b>, so a difference between them
    /// is attributable to the profile and nothing else. The fourth is the one other field this
    /// harness has never supplied, and it is the shape this lane's own figures were taken under.
    /// </para>
    /// </remarks>
    private static (string Name, bool Authenticated, BehaviouralProfile? Profile, string Cell)[] SiteConditions()
    {
        var unavailable = new BehaviouralProfile
        {
            Direction = MailDirection.Inbound,
            ProfileAvailable = false,
            ColdStart = true,
        };

        var populated = new BehaviouralProfile
        {
            Direction = MailDirection.Inbound,
            ProfileAvailable = true,
            ColdStart = false,
            FirstSeenDaysAgo = 180,
            MessagesObserved = 42,
            TrustedSamples = 40,
            Regime = "steady",
            DistinctRecipientsLastHour = 1,
            DistinctRecipientsLast30Days = 3,
            RecipientsNovelToSender = 0,
            MessagesLastHour = 1,
            MessagesLast24Hours = 2,
            BaselineMessagesPerHour = 0.1,
            FanoutLastHour = 1,
            BaselineFanoutPerHour = 1.0,
            DimensionsWithSupport = 8,
        };

        return
        [
            ("S1-absent", true, null, "profile absent, the Host's provenance"),
            ("S2-unavailable", true, unavailable, "profile present and unavailable"),
            ("S3-populated", true, populated, "profile present and populated"),
            ("S4-noauth", false, null, "profile absent, no provenance: this lane's own shape"),
        ];
    }

    /// <summary>
    /// The site experiment's inputs and its comparison target, with no model call.
    /// </summary>
    /// <remarks>
    /// It reads the same fixture through the same parser as the run does, and prints the two body
    /// fields the splitter produced, because a candidate built against a message that did not parse
    /// the way it was expected to is a candidate measuring something else. It asserts nothing; it is
    /// the thing to read before eight calls on a shared model are spent.
    /// </remarks>
    private static void SiteDry()
    {
        var e2eResults = Path.Combine(ResultsDirectory, "e2e", "results");
        var target = ReadE2eDigest(SiteArm);

        Console.WriteLine("== site-dry: the inputs, and what they will be compared against ==");
        Console.WriteLine(
            target is null
                ? $"target digest: NOT FOUND. {SiteArm}.pass1.json is absent under {e2eResults}, so a "
                    + "run now would have nothing to compare against."
                : $"target digest: {target}\n"
                    + $"               read from {Path.Combine(e2eResults, SiteArm + ".pass1.json")}");
        Console.WriteLine($"verifier      : {HostVerifierId}");
        Console.WriteLine();

        var window = Threads.All[0].PriorTurns;
        Console.WriteLine($"window turns  : {window.Count} (Threads.All[0].PriorTurns, the same three the arm supplies)");
        Console.WriteLine();
        Console.WriteLine($"{"condition",-16} {"cell",-52} {"profile",-20} body/quoted");
        Console.WriteLine(new string('-', 140));

        foreach (var (name, authenticated, profile, cell) in SiteConditions())
        {
            var analysis = Corpus.Analyze("reply-in-thread", window, authenticated, HostVerifierId);

            var shape = analysis.Message is null
                ? "NOT ANALYSABLE"
                : $"body={analysis.Message.BodyText.Length} quoted={analysis.Message.QuotedText?.Length ?? 0}";

            var described = profile is null
                ? "absent"
                : $"available={profile.ProfileAvailable} cold={profile.ColdStart}";

            Console.WriteLine($"{name,-16} {cell,-52} {described,-20} {shape}");
        }

        Console.WriteLine();
        Console.WriteLine(
            "Each of the four is a distinct input and must hash to a distinct digest; the run reports "
            + "them so that a match can be read as an identification rather than a coincidence.");
    }

    /// <summary>
    /// Which input shape the Host actually sends, decided by reproducing its own cache key digest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The question.</b> The committed reply-in-thread fixture answered differently on the two
    /// paths: <c>B</c> end to end through the Host's <c>/v1/assessments</c>, and <c>A</c> through this
    /// lane's own harness. Same fixture, same window, same parser, opposite answers. Something about
    /// the input differs, and the candidates are the two things this harness has never supplied: the
    /// authentication block and the behavioural profile.
    /// </para>
    /// <para>
    /// <b>Why a digest rather than an answer.</b> The adapter hashes the model, the schema and shape
    /// versions, the applied window, the dimension count and the rendered state into the cache key,
    /// and serves that digest on every response. It is therefore an identity of the question the
    /// provider was asked, and an equality test across processes. If some candidate input reproduces
    /// the digest the Host served for the arm under investigation, then the provider was shown the
    /// same thing in both places, and whatever moved the answer is not the input. If no candidate
    /// does, the input differs in something none of these four models, which is a result rather than
    /// a failure.
    /// </para>
    /// <para>
    /// <b>The configuration cannot explain a mismatch.</b> The digest covers the window and the body
    /// budget as well as the state, and the Host builds its options from the same defaults this
    /// harness takes, overriding only the endpoint and the model name. So a mismatch here is a
    /// difference in the state rather than in how either side was configured.
    /// </para>
    /// <para>
    /// <b>Two passes per shape, eight calls.</b> The digest is deterministic, so its two readings are
    /// a check that the identity still holds rather than a sample, and the artifact records whether
    /// they agreed. The continuity column is two readings and is reported as two; the distribution
    /// for the fixture over this window is the end-to-end suite's three passes, which are the
    /// evidence for the answer rather than this.
    /// </para>
    /// </remarks>
    private static async Task Site(Runner runner)
    {
        // The digest the Host served for the arm under investigation, read from that run's artifact
        // rather than transcribed into a constant. A digest copied out of a response is a second
        // source of truth, and a re-run of the suite would then be compared against a number nobody
        // sent.
        const string Arm = SiteArm;
        var e2eResults = Path.Combine(ResultsDirectory, "e2e", "results");
        var target = ReadE2eDigest(Arm);

        // The same three prior turns the end-to-end arm supplies, from the same table that built them.
        var window = Threads.All[0].PriorTurns;

        var conditions = SiteConditions();

        const int repeats = 2;

        Console.WriteLine("== site: which input shape did the Host actually send ==");
        Console.WriteLine(
            target is null
                ? $"target digest: unavailable, the artifact for {Arm} is not present under {e2eResults}"
                : $"target digest: {target}\n"
                    + $"               read from {Path.Combine(e2eResults, Arm + ".pass1.json")}");
        Console.WriteLine();
        Console.WriteLine($"{"condition",-16} {"cell",-52} {"pass",-5} {"continuity",-11} digest");
        Console.WriteLine(new string('-', 140));

        var rows = new List<object>();
        string? matched = null;

        foreach (var (name, authenticated, profile, cell) in conditions)
        {
            var digests = new List<string>();
            var answers = new List<string>();

            foreach (var pass in Enumerable.Range(1, repeats))
            {
                var result = await runner.CallAsync(
                    Corpus.Input("reply-in-thread", window, authenticated, HostVerifierId, profile),
                    CancellationToken.None);

                var digest = result.KeyDigest ?? "none";
                var match = target is not null && string.Equals(digest, target, StringComparison.Ordinal);

                if (match && matched is null)
                {
                    matched = name;
                }

                digests.Add(digest);
                answers.Add(result.Continuity);

                Console.WriteLine(
                    $"{name,-16} {cell,-52} {pass,-5} {result.Continuity,-11} {digest}"
                    + (match ? "  <- the Host's digest" : string.Empty));
                Console.Out.Flush();
            }

            rows.Add(new
            {
                condition = name,
                cell,
                authenticated,
                profile = profile is null
                    ? "absent"
                    : profile.ProfileAvailable ? "populated" : "present-unavailable",
                continuity = answers,
                key_digest = digests,
                // A candidate whose two passes disagree on its own digest would mean the digest is not
                // the identity this test assumes, and the comparison would be meaningless. Recorded
                // rather than assumed away.
                digest_stable = digests.Distinct(StringComparer.Ordinal).Count() == 1,
                matches_host = target is not null
                    && digests.Any(d => string.Equals(d, target, StringComparison.Ordinal)),
            });
        }

        Console.WriteLine();

        if (target is null)
        {
            Console.WriteLine(
                "Nothing was compared: the end-to-end run's artifact is missing, so there is no Host "
                + "digest to match against. The digests above are still four different questions, and "
                + "that they differ from each other is what makes the comparison able to decide "
                + "anything at all.");
        }
        else if (matched is not null)
        {
            Console.WriteLine(
                $"MATCH on {matched}. The Host was shown the same state this candidate builds, so the "
                + "question it asked is identified exactly, and the input is not what moved the answer.");
        }
        else
        {
            Console.WriteLine(
                $"NO MATCH. None of the four candidates reproduces the Host's digest, so its provider "
                + "state differs in a field none of them varies. That is a result and not a failure: "
                + "it rules out the profile and the authentication block as the whole difference, and "
                + "the next step is to vary the remaining state fields one at a time against this "
                + "target rather than to narrow to the nearest explanation.");
        }

        Write("site-input-shape", new
        {
            arm = Arm,
            target_digest = target,
            window_turns = window.Count,
            verifier = HostVerifierId,
            repeats,
            design = "the three profile shapes the shipping classifier distinguishes (absent, present "
                + "and unavailable, present and populated) at the Host's own provenance, plus this "
                + "lane's provenance-free shape, all on the committed reply-in-thread fixture over the "
                + "end-to-end arm's own window",
            calls = rows,
            matched,
        });
        Console.WriteLine();
    }

    /// <summary>
    /// The same message and provenance at three window lengths, against each length's own arm.
    /// </summary>
    /// <remarks>
    /// <b>Why this exists.</b> The four shape candidates all failed to reproduce the Host's digest for
    /// the three-turn arm. Everything they vary, and everything they hold fixed, has been checked at
    /// source: the window text is byte-identical to the arm's, the envelope and the authentication
    /// block are the ones the arm posts, and neither the profile nor the provenance is the whole
    /// difference, because varying both moved the answer and neither moved it onto the Host's digest.
    /// The digest is not the part that moved either: the assembly this tool loaded and the one the
    /// target was served from hash the same field list, and the only change either side has seen since
    /// is a term added to <em>both</em>, which cannot turn equality into inequality.
    /// <para>
    /// So the remaining state fields have to be separated one at a time, and the window is the only one
    /// with a second target to test against. The suite already holds two more arms of the same fixture
    /// whose windows differ in length and in nothing else: <c>arm0</c> carries no window and
    /// <c>arm1</c> carries one turn of the window this tool supplies three of. If the profile-absent
    /// shape reproduces either, then the message, envelope, coverage and authentication are right and
    /// the divergence is in the window; if it reproduces neither, the divergence is in the message and
    /// the window is exonerated. Either outcome is a result. <c>arm2</c> is re-taken here as a control,
    /// so the new run is comparable with the site experiment rather than merely adjacent to it.
    /// </para>
    /// </remarks>
    private static async Task SiteNear(Runner runner)
    {
        // The arm, how many turns of this lane's window it supplies, and what that length is for.
        var targets = new (string Arm, int Turns, string Cell)[]
        {
            ("arm0-reply-in-thread-none", 0, "no window at all"),
            ("arm1-reply-in-thread-matching", 1, "one turn, the window's first"),
            ("arm2-reply-in-thread-mismatched", 3, "three turns, the site experiment's own arm"),
        };

        var prior = Threads.All[0].PriorTurns;

        const int repeats = 2;

        Console.WriteLine("== site-near: the same message and provenance at three window lengths ==");
        Console.WriteLine(
            $"{"arm",-40} {"turns",-6} {"pass",-5} {"continuity",-11} {"digest",-66} verdict");
        Console.WriteLine(new string('-', 150));

        var rows = new List<object>();
        string? matchedArm = null;
        var matchedTurns = -1;

        foreach (var (arm, turns, cell) in targets)
        {
            var target = ReadE2eDigest(arm);

            // Every arm is taken with the Host's provenance and no profile: the Host's own evidence
            // reports no sender snapshot (every behavioural row Unavailable at sampleSupport 0) and the
            // run posts a trusted authentication result, so this is the Host's shape by construction.
            var window = turns == 0 ? null : prior.Take(turns).ToList();

            var digests = new List<string>();
            var answers = new List<string>();

            foreach (var pass in Enumerable.Range(1, repeats))
            {
                var result = await runner.CallAsync(
                    Corpus.Input("reply-in-thread", window, authenticated: true, HostVerifierId, profile: null),
                    CancellationToken.None);

                var digest = result.KeyDigest ?? "none";
                var match = target is not null && string.Equals(digest, target, StringComparison.Ordinal);

                if (match && matchedArm is null)
                {
                    matchedArm = arm;
                    matchedTurns = turns;
                }

                digests.Add(digest);
                answers.Add(result.Continuity);

                Console.WriteLine(
                    $"{arm,-40} {turns,-6} {pass,-5} {result.Continuity,-11} {digest,-66} "
                    + (match ? "MATCH" : target is null ? "no target" : "-"));
                Console.Out.Flush();
            }

            rows.Add(new
            {
                arm,
                cell,
                turns,
                target_digest = target,
                continuity = answers,
                key_digest = digests,
                // Recorded rather than assumed, for the same reason the site experiment records it: a
                // digest that does not repeat is not an identity and the comparison would be empty.
                digest_stable = digests.Distinct(StringComparer.Ordinal).Count() == 1,
                matches_arm = target is not null
                    && digests.Any(d => string.Equals(d, target, StringComparison.Ordinal)),
            });
        }

        Console.WriteLine();

        Console.WriteLine(matchedArm is null
            ? "NO MATCH AT ANY LENGTH. The window is exonerated: the same message at zero, one and "
                + "three turns reproduces none of the three arms it belongs to, so what differs lies in "
                + "the message, the envelope, the coverage or the authentication rather than in the "
                + "conversation context."
            : $"MATCH on {matchedArm} at {matchedTurns} turns. The message, envelope, coverage and "
                + "authentication this tool builds are the Host's, and the divergence is in the window "
                + "after all.");

        Write("site-near", new
        {
            design = "one message, one provenance, three window lengths, each against the arm that "
                + "took that length",
            repeats,
            calls = rows,
        });
        Console.WriteLine();
    }

    /// <summary>
    /// The site question, asked of the Host's own key function instead of the adapter's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What was wrong with the first instrument.</b> The site experiment read
    /// <c>cache.keyDigest</c> off the Host and compared it with the adapter's
    /// <c>ComputeCacheKeyDigest</c> over the same candidates. Those are two different functions:
    /// the served key is <see cref="SemanticCacheKey.Digest"/>, computed by
    /// <c>SemanticCacheClassifier</c> in the layer between the pipeline and the provider, and it
    /// hashes a canonicalised classifier input with no window term at all; the adapter's own digest
    /// hashes the rendered provider state and the applied window. Two functions over two payloads
    /// cannot agree on any input, so the four candidates' failure to match was guaranteed before
    /// either was built. The contradiction that exposed it was a byte-identical re-take of all three
    /// arms under a tree where the adapter's digest had gained a term and the Host's key had not.
    /// </para>
    /// <para>
    /// <b>Why this one decides the question.</b> <see cref="SemanticCacheKey.Digest"/> is public and
    /// takes the input and the options, so the Host's key can be computed here for any candidate
    /// input, with the same code the Host ran, and compared against the digest the Host served. A
    /// match is an identification: the canonicaliser is the same one, so reproducing a 64-hex digest
    /// means the classifier input is byte-identical to the one the Host built, not merely similar. A
    /// no-match is equally decisive, because this time nothing but the input can explain it.
    /// </para>
    /// <para>
    /// <b>The options are the Host's, read through the Host's own type.</b> <c>BuildAssessor</c>
    /// constructs <see cref="MailAssessorOptions"/> with only the profile hasher set, so every other
    /// field, the cache options included, is the record's default. Taking that default from the type
    /// rather than transcribing the strings means a change to the default moves this tool with it.
    /// The hasher here is never read by anything; the instance exists only to reach the property.
    /// </para>
    /// <para>
    /// <b>Two families of candidate, and the reason for each.</b> The first reproduces each arm's own
    /// request: no window, one turn, three turns, all at the provenance the arms post and with no
    /// profile. If those three reproduce their three targets, then the parser, the envelope, the
    /// coverage, the provenance and the window text are all accounted for, and the site question is
    /// answered in the same breath. The second family is the four profile shapes at the three-turn
    /// window, which is the comparison the first instrument tried to make and could not.
    /// </para>
    /// <para>
    /// <b>And one control, which is a tenant swap.</b> The canonicaliser writes the envelope's tenant
    /// first, and the Host takes that from the caller's claim rather than from the request, so every
    /// candidate here is built under the harness Host's own tenant. The control row repeats one
    /// candidate under this lane's own tenant so that the tenant's effect is visible in the same
    /// table: the value is not assumed to matter, it is shown to.
    /// </para>
    /// <para>
    /// <b>No model call, and that is the point.</b> Every number here is a hash of bytes this process
    /// built, so the answer does not depend on a shared model being free, on a sampling seed, or on a
    /// warm context window. It is the one form of this experiment that cannot be confounded by
    /// anything outside the tree.
    /// </para>
    /// </remarks>
    private static void SiteKey()
    {
        var hostCache = new MailAssessorOptions
        {
            ProfileKeyHasher = new ProfileKeyHasher(new byte[ProfileKeyHasher.MinimumKeyBytes]),
        }.SemanticCache;

        // The same three prior turns the arms supply, from the table that built them.
        var window = Threads.All[0].PriorTurns;

        var arms = new (string Arm, int Turns)[]
        {
            ("arm0-reply-in-thread-none", 0),
            ("arm1-reply-in-thread-matching", 1),
            ("arm2-reply-in-thread-mismatched", 3),
        };

        Console.WriteLine("== site-key: the Host's own key over candidate inputs, with no model call ==");
        Console.WriteLine($"key scheme    : {SemanticCacheKey.KeySchemeVersion}");
        Console.WriteLine($"model version : {hostCache.ClassifierModelVersion}");
        Console.WriteLine($"schema version: {hostCache.QuestionSchemaVersion}");
        Console.WriteLine($"preprocessing : {hostCache.PreprocessingVersion}");
        Console.WriteLine($"verifier      : {HostVerifierId}");
        Console.WriteLine($"host tenant   : {HostTenantId}");
        Console.WriteLine();

        var targets = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var (arm, turns) in arms)
        {
            targets[arm] = ReadE2eDigest(arm);
            Console.WriteLine(
                $"{arm,-34} window={turns}  target={targets[arm] ?? "NOT FOUND under the results directory"}");
        }

        Console.WriteLine();
        Console.WriteLine($"{"candidate",-16} {"tenant",-9} {"window",-7} {"provenance",-11} {"profile",-20} {"digest",-66} verdict");
        Console.WriteLine(new string('-', 170));

        var rows = new List<object>();
        string? matchedArm = null;
        string? matchedCandidate = null;

        void Candidate(
            string name,
            string cell,
            IReadOnlyList<string>? prior,
            bool authenticated,
            BehaviouralProfile? profile,
            string tenantId)
        {
            var digest = SemanticCacheKey.Digest(
                Corpus.Input("reply-in-thread", prior, authenticated, HostVerifierId, profile, tenantId),
                hostCache);

            string? hit = null;

            foreach (var (arm, _) in arms)
            {
                if (targets[arm] is not null && string.Equals(targets[arm], digest, StringComparison.Ordinal))
                {
                    hit = arm;
                }
            }

            if (hit is not null && matchedArm is null)
            {
                matchedArm = hit;
                matchedCandidate = name;
            }

            var described = profile is null
                ? "absent"
                : profile.ProfileAvailable ? "populated" : "present-unavailable";

            Console.WriteLine(
                $"{name,-16} {tenantId,-9} {(prior is null ? "none" : prior.Count.ToString(CultureInfo.InvariantCulture)),-7} "
                + $"{(authenticated ? "present" : "absent"),-11} {described,-20} {digest,-66} "
                + (hit is null ? "(no arm)" : $"<- {hit}"));
            Console.Out.Flush();

            rows.Add(new
            {
                candidate = name,
                cell,
                tenant = tenantId,
                window_turns = prior?.Count,
                authenticated,
                profile = described,
                key_digest = digest,
                matches_arm = hit,
            });
        }

        // Family one: each arm's own request, rebuilt from the table that built it, under the tenant
        // the Host that served those arms assesses under.
        foreach (var (arm, turns) in arms)
        {
            Candidate(
                arm.Replace("reply-in-thread-", string.Empty, StringComparison.Ordinal),
                $"the {arm} request rebuilt here",
                turns == 0 ? null : [.. window.Take(turns)],
                authenticated: true,
                profile: null,
                tenantId: HostTenantId);
        }

        Console.WriteLine();

        // Family two: the four profile shapes at the three-turn window, which is the arm the site
        // experiment was about, under the same Host tenant.
        foreach (var (name, authenticated, profile, cell) in SiteConditions())
        {
            Candidate(name, cell, window, authenticated, profile, HostTenantId);
        }

        Console.WriteLine();

        // The control. The three-turn arm under this lane's own tenant, which is the digest the
        // previous run of this tool printed for it. It is here so that a match above can be read as
        // the tenant having been the difference rather than as the candidates having drifted: if
        // this row differs from the arm2 row above and the arm2 row is what matched the target, the
        // tenant is the field that moved, and the rest of the input is right.
        Candidate(
            "arm2-mismatched",
            "the same request under this lane's own tenant, which is the control",
            window,
            authenticated: true,
            profile: null,
            tenantId: MeasureTenantId);

        Console.WriteLine();

        if (matchedArm is not null)
        {
            Console.WriteLine(
                $"MATCH. Candidate '{matchedCandidate}' reproduces {matchedArm}'s served digest under the "
                + "Host's own key function, so the classifier input this tool builds is the input the Host "
                + "built, byte for byte. The input shape is identified, and the next question is what that "
                + "shape does to the answer rather than what it is.");
        }
        else
        {
            Console.WriteLine(
                "NO MATCH. No candidate reproduces any arm's served digest under the Host's own key "
                + "function, and this time the comparison is sound: the same function computed both sides. "
                + "So the input differs in something none of these candidates carries, and the remaining "
                + "state fields have to be varied one at a time against a target rather than narrowed to "
                + "the nearest explanation.");
        }

        Write("site-key", new
        {
            window_turns = window.Count,
            verifier = HostVerifierId,
            tenant = HostTenantId,
            key_scheme = SemanticCacheKey.KeySchemeVersion,
            cache_options = new
            {
                classifier_model_version = hostCache.ClassifierModelVersion,
                question_schema_version = hostCache.QuestionSchemaVersion,
                preprocessing_version = hostCache.PreprocessingVersion,
            },
            design = "the Host's SemanticCacheKey.Digest, under the cache options MailAssessorOptions "
                + "defaults to, over each arm's own request rebuilt here and over the four profile "
                + "shapes at the three-turn window, both under the tenant the harness Host assesses "
                + "under and, as a control, under this lane's own. No model call: every figure is a "
                + "hash of bytes this process built.",
            targets,
            candidates = rows,
            matched_arm = matchedArm,
            matched_candidate = matchedCandidate,
        });
        Console.WriteLine();
    }

    /// <summary>
    /// The key computation swept over the two values the Host holds but never reports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a sweep rather than a read.</b> The site-key candidates rebuilt each arm's request from
    /// the request artifact and still did not reproduce a served digest. Everything in that rebuild
    /// is either taken from the request, taken from the fixture, or read at source out of the Host's
    /// own composition, with two exceptions: the tenant, which the Host takes from the authenticated
    /// principal rather than the body, and the model-version string, which goes into the key but is
    /// not the resolved model the response reports. Both live in the deployment's configuration, and
    /// one of them being something other than this tool assumed would be invisible in the response.
    /// </para>
    /// <para>
    /// <b>This is a search, and a miss is informative.</b> The two axes are small and enumerable, so
    /// the whole product is cheap. If some cell reproduces a served digest, both unknowns are
    /// identified at once and the input shape is confirmed by construction. If no cell does, then the
    /// difference is in the message rather than in the configuration, which narrows the search to the
    /// parser's output and is where the next probe goes.
    /// </para>
    /// </remarks>
    private static void SiteGrid()
    {
        var tenants = new[] { HostTenantId, MeasureTenantId, "default", "harness-tenant" };
        var models = new[] { "jev-1.13.0", "nimble:latest", "nimble" };

        var unavailable = new BehaviouralProfile
        {
            Direction = MailDirection.Inbound,
            ProfileAvailable = false,
            ColdStart = true,
        };

        var profiles = new (string Name, BehaviouralProfile? Profile)[]
        {
            ("absent", null),
            ("present-unavailable", unavailable),
        };

        var arms = new (string Arm, int Turns)[]
        {
            ("arm0-reply-in-thread-none", 0),
            ("arm1-reply-in-thread-matching", 1),
            ("arm2-reply-in-thread-mismatched", 3),
        };

        var window = Threads.All[0].PriorTurns;
        var targets = arms.ToDictionary(a => a.Arm, a => ReadE2eDigest(a.Arm), StringComparer.Ordinal);

        Console.WriteLine("== site-grid: tenant x model version x profile, against the three arms ==");

        foreach (var (arm, _) in arms)
        {
            Console.WriteLine($"{arm,-34} target={targets[arm] ?? "NOT FOUND"}");
        }

        Console.WriteLine();
        Console.WriteLine($"{"arm",-34} {"tenant",-15} {"model version",-14} {"profile",-20} digest");
        Console.WriteLine(new string('-', 150));

        var hits = new List<object>();
        var tried = 0;

        foreach (var (arm, turns) in arms)
        {
            if (targets[arm] is null)
            {
                continue;
            }

            IReadOnlyList<string>? prior = turns == 0 ? null : [.. window.Take(turns)];

            foreach (var tenant in tenants)
            {
                foreach (var model in models)
                {
                    foreach (var (profileName, profile) in profiles)
                    {
                        tried++;

                        var digest = SemanticCacheKey.Digest(
                            Corpus.Input("reply-in-thread", prior, authenticated: true, HostVerifierId, profile, tenant),
                            new SemanticCacheOptions { ClassifierModelVersion = model });

                        if (!string.Equals(digest, targets[arm], StringComparison.Ordinal))
                        {
                            continue;
                        }

                        Console.WriteLine(
                            $"{arm,-34} {tenant,-15} {model,-14} {profileName,-20} {digest}   <- MATCH");
                        Console.Out.Flush();

                        hits.Add(new { arm, tenant, model, profile = profileName, key_digest = digest });
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            tried == 0
                ? "Nothing was tried: no target digest was readable, so there was nothing to compare against."
                : $"tried {tried.ToString(CultureInfo.InvariantCulture)} combinations, "
                    + $"{hits.Count.ToString(CultureInfo.InvariantCulture)} reproduced a served digest.");

        Write("site-grid", new
        {
            window_turns = window.Count,
            verifier = HostVerifierId,
            tenants,
            models,
            profiles = profiles.Select(p => p.Name).ToArray(),
            tried,
            hits,
        });
        Console.WriteLine();
    }

    /// <summary>
    /// The site question answered by letting the Host's own assessor build the input.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What each earlier instrument could not do.</b> The digest comparison over hand-built
    /// candidates was sound only if the hand build was the Host's build, and it was not: the
    /// candidates assemble the classifier input in this tool, so every step between the bytes and the
    /// classifier that belongs to <c>MailAssessor</c> was reproduced from a reading of its source
    /// rather than measured. That is the same class of error as the first instrument's, one level
    /// down: a comparison that cannot fail for the right reason.
    /// </para>
    /// <para>
    /// <b>So the reconstruction is deleted rather than improved.</b> The shipping assessor is
    /// composed exactly as the Host composes it, over an empty scratch database and spool, and is
    /// handed the same analysis the endpoint hands it. The provider position is occupied by a stub
    /// that records its input, and the pipeline's own cache decorator keys whatever it was given. The
    /// digest that comes back is therefore the Host's key over the Host's input, and the comparison
    /// with a served digest has nothing left in it that this lane wrote.
    /// </para>
    /// <para>
    /// <b>The difference is then named rather than searched for.</b> A miss is reported as the field
    /// list that differs between the captured input and this lane's own, so the next probe is aimed
    /// rather than guessed. Message content is compared by length and digest and never printed: the
    /// point is to name the field, and naming it does not require reproducing it.
    /// </para>
    /// </remarks>
    private static async Task SiteCapture(string directory)
    {
        var window = Threads.All[0].PriorTurns;

        var arms = new (string Arm, int Turns)[]
        {
            ("arm0-reply-in-thread-none", 0),
            ("arm1-reply-in-thread-matching", 1),
            ("arm2-reply-in-thread-mismatched", 3),
        };

        Console.WriteLine("== site-capture: the Host's own assessor builds the input, no model call ==");
        Console.WriteLine($"tenant        : {HostTenantId}");
        Console.WriteLine($"scratch       : {directory}");
        Console.WriteLine();

        var rows = new List<object>();
        string? matched = null;

        foreach (var (arm, turns) in arms)
        {
            var target = ReadE2eDigest(arm);
            IReadOnlyList<string>? prior = turns == 0 ? null : [.. window.Take(turns)];

            var analysis = Corpus.Analyze("reply-in-thread", prior, authenticated: true, HostVerifierId, HostTenantId);

            var capture = await HostInputCapture.CaptureAsync(
                directory,
                analysis.Message!,
                HostTenantId,
                Corpus.Bytes("reply-in-thread"));

            var hit = target is not null
                && string.Equals(capture.Digest, target, StringComparison.Ordinal);

            if (hit)
            {
                matched = arm;
            }

            Console.WriteLine($"--- {arm}   window={(prior?.Count ?? 0)}");
            Console.WriteLine($"    served by the Host : {target ?? "NOT FOUND"}");
            Console.WriteLine($"    built by assessor  : {capture.Digest ?? "none"}");
            Console.WriteLine(hit
                ? "    MATCH. The assessor's own input keys to the digest the Host served."
                : "    no match.");

            var mine = Corpus.Input(
                "reply-in-thread", prior, authenticated: true, HostVerifierId, profile: null, tenantId: HostTenantId);

            var differences = DiffInputs(capture.Input, mine);

            Console.WriteLine(differences.Count == 0
                ? "    no field differs from this lane's reconstruction."
                : $"    {differences.Count} field difference(s) from this lane's reconstruction:");

            foreach (var line in differences)
            {
                Console.WriteLine($"      {line}");
            }

            Console.WriteLine();

            rows.Add(new
            {
                arm,
                window_turns = prior?.Count,
                served_digest = target,
                captured_digest = capture.Digest,
                matches = hit,
                differences,
            });
        }

        Console.WriteLine(matched is not null
            ? $"MATCH on {matched}. The assessor's input keys to the digest that arm was served, so the "
                + "input shape is identified by construction rather than by argument."
            : "NO MATCH on any arm. The field list above names what differs, and the next probe follows "
                + "that list rather than the nearest explanation.");

        Write("site-capture", new
        {
            tenant = HostTenantId,
            verifier = HostVerifierId,
            window_turns = window.Count,
            design = "the shipping MailAssessor composed by AssessmentPipeline.Create over an empty "
                + "scratch database and spool, with a capturing stub in the provider position, so the "
                + "input and its cache key are the Host's own. No model call.",
            arms = rows,
            matched,
        });
        Console.WriteLine();
    }

    /// <summary>
    /// The answer column, asked of the Host's own input shape on this lane's own adapter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The question this settles.</b> The capture proves the Host's classifier input, by
    /// reproducing the digest the Host served from it. What it does not settle is whether the
    /// difference in the answers was that input. So the same captured input is handed to this lane's
    /// adapter, and the answer it produces is compared with the answer the Host recorded for that
    /// arm. Same input, same provider, one process apart: if the answers now agree, the input shape
    /// is the whole of the difference and the site question is closed. If they disagree on an input
    /// that is provably identical, the input is exonerated and the difference is downstream of it.
    /// </para>
    /// <para>
    /// <b>Both shapes are asked, every arm, and the control is the point.</b> The Host's input and
    /// this lane's own are sent to the same adapter in the same process, in the same run, so the two
    /// answers differ only by the input. A run that asked only the Host's shape could not tell
    /// "the shape answers B" from "the adapter answers B whatever it is given".
    /// </para>
    /// <para>
    /// <b>Six calls, and the digest is checked in the same pass.</b> The captured input's key is
    /// recomputed and compared with the arm's served digest before the model is asked anything, so a
    /// run whose capture had drifted would say so rather than spend its calls reporting on a
    /// different input.
    /// </para>
    /// </remarks>
    private static async Task SiteAnswer(Runner runner)
    {
        var directory = Path.Combine(ResultsDirectory, "capture");
        var window = Threads.All[0].PriorTurns;

        var arms = new (string Arm, int Turns)[]
        {
            ("arm0-reply-in-thread-none", 0),
            ("arm1-reply-in-thread-matching", 1),
            ("arm2-reply-in-thread-mismatched", 3),
        };

        Console.WriteLine("== site-answer: the Host's own input shape, through this lane's adapter ==");
        Console.WriteLine($"tenant        : {HostTenantId}");
        Console.WriteLine();
        Console.WriteLine($"{"arm",-34} {"window",-7} {"Host says",-10} {"this lane's shape",-18} {"the Host's shape",-18} capture");
        Console.WriteLine(new string('-', 130));

        var rows = new List<object>();
        var agreed = 0;
        var laneAgreed = 0;
        var asked = 0;

        foreach (var (arm, turns) in arms)
        {
            IReadOnlyList<string>? prior = turns == 0 ? null : [.. window.Take(turns)];

            var analysis = Corpus.Analyze("reply-in-thread", prior, authenticated: true, HostVerifierId, HostTenantId);

            var capture = await HostInputCapture.CaptureAsync(
                directory,
                analysis.Message!,
                HostTenantId,
                Corpus.Bytes("reply-in-thread"));

            var served = ReadE2eDigest(arm);
            var captureMatches = capture.Digest is not null
                && string.Equals(capture.Digest, served, StringComparison.Ordinal);

            var hostAnswer = ReadE2eContinuity(arm);

            // The gate, and it is not a formality: a capture that does not reproduce the served digest
            // is not the Host's input, and asking the model about it would produce a row that looks
            // like an answer to this question while being an answer to a different one. Both calls are
            // skipped together, so no arm is ever half-measured.
            if (capture.Input is null || !captureMatches)
            {
                var why = capture.Input is null
                    ? "capture produced no input"
                    : "DOES NOT REPRODUCE THE SERVED DIGEST";

                Console.WriteLine($"{arm,-34} {turns,-7} {hostAnswer,-10} {"not asked",-18} {"not asked",-18} {why}");
                Console.Out.Flush();

                rows.Add(new
                {
                    arm,
                    window_turns = turns,
                    host_answer = hostAnswer,
                    this_lane_shape = (string?)null,
                    host_shape = (string?)null,
                    capture_reproduces_served_digest = false,
                    adapter_digest_on_host_shape = (string?)null,
                });

                continue;
            }

            // Taken into a local before anything is awaited, so the non-nullness the guard above
            // established is a fact the compiler carries rather than one this code asserts with a '!'.
            var hostInput = capture.Input;

            // Both shapes, same adapter, same process, same run. The only thing that differs between
            // this row and the next is the behavioural profile the Host carries.
            var onMine = await runner.CallAsync(
                Corpus.Input("reply-in-thread", prior, authenticated: true, HostVerifierId, profile: null, tenantId: HostTenantId),
                CancellationToken.None);

            var onHost = await runner.CallAsync(hostInput, CancellationToken.None);

            if (hostAnswer is "A" or "B")
            {
                asked++;

                if (string.Equals(onHost.Continuity, hostAnswer, StringComparison.Ordinal))
                {
                    agreed++;
                }

                if (string.Equals(onMine.Continuity, hostAnswer, StringComparison.Ordinal))
                {
                    laneAgreed++;
                }
            }

            Console.WriteLine(
                $"{arm,-34} {turns,-7} {hostAnswer,-10} {onMine.Continuity,-18} {onHost.Continuity,-18} "
                + "the served digest");
            Console.Out.Flush();

            rows.Add(new
            {
                arm,
                window_turns = turns,
                host_answer = hostAnswer,
                this_lane_shape = onMine.Continuity,
                host_shape = onHost.Continuity,
                capture_reproduces_served_digest = true,
                adapter_digest_on_host_shape = onHost.KeyDigest,
            });
        }

        Console.WriteLine();

        // Four readings, and which one applies is the whole result. They are separated here rather than
        // folded into one pass/fail, because "the profile shape is the site" and "this adapter answers
        // what the Host answers" are different findings with different next steps.
        string finding;

        if (asked == 0)
        {
            finding = "nothing-compared";
            Console.WriteLine(
                "Nothing was compared: no arm recorded an A or a B for continuity, so there is no "
                + "answer to agree or disagree with.");
        }
        else if (agreed == asked && laneAgreed == asked)
        {
            finding = "both-shapes-agree";
            Console.WriteLine(
                $"BOTH SHAPES AGREE on all {asked} arm(s). Given the input the Host builds, this adapter "
                + "answers what the Host answered, and it answers the same without the Host's profile "
                + "too. The input shape does not separate the two answers on this run, and the earlier A "
                + "was not reproduced by either shape here.");
        }
        else if (agreed == asked)
        {
            finding = "the-profile-shape-is-the-site";
            Console.WriteLine(
                $"SEPARATED. On {asked} arm(s) this adapter, given the input the Host builds, answers "
                + $"what the Host answered; on {asked - laneAgreed} of them the same adapter on the same "
                + "message and window answers differently once the Host's behavioural profile is removed. "
                + "The profile the Host carries, present and unavailable, is therefore what the answer "
                + "turns on. Nothing downstream of the input has to be invoked to explain the split.");
        }
        else
        {
            finding = "downstream-of-the-input";
            Console.WriteLine(
                $"DISAGREEMENT on {asked - agreed} of {asked} arm(s), on an input whose digest reproduces "
                + "the one the Host served. The input is exonerated by construction, and what remains is "
                + "downstream of it: the provider call this adapter makes, its options, or how the answer "
                + "is read back. That is a narrower question than the one this run was asked.");
        }

        Write("site-answer", new
        {
            tenant = HostTenantId,
            verifier = HostVerifierId,
            design = "the input the shipping MailAssessor builds, captured with no model call, then sent "
                + "to this lane's adapter beside this lane's own input for the same message and window",
            rows,
            arms_with_an_answer = asked,
            host_shape_agreements = agreed,
            lane_shape_agreements = laneAgreed,
            finding,
        });
        Console.WriteLine();
    }

    /// <summary>
    /// One field at a time, against the same message: is the answer the authentication block?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is the next question and not a guess.</b> The answer column came back with both
    /// shapes agreeing on B, which rules the behavioural profile out: the profile is the only field
    /// that differed between the Host's input and this lane's authenticated reconstruction, and the
    /// answer did not move. So the field that separated <c>A</c> from <c>B</c> is some other field,
    /// and the measurement that has to be made is the one <c>overview-</c> asked for -- the remaining
    /// state fields varied one at a time against the same target, rather than narrowed to the nearest
    /// explanation.
    /// </para>
    /// <para>
    /// <b>The field this varies, and why it is the one.</b> This lane has always measured with no
    /// authentication block, because a boundary measurement of its own never had one. The Host's
    /// request carries a trusted one: <c>connectingIp</c>, and a <c>spf/pass</c> result from
    /// <c>conversation-measure</c> marked as coming from a trusted verifier. Every other field is held
    /// fixed here -- the same fixture, the same parser, the same verifier string, the same absent
    /// profile, the same tenant -- so a difference between a row and its twin is the authentication
    /// block and nothing else.
    /// </para>
    /// <para>
    /// <b>Both windows are asked because the window is the other half of the split.</b> Ten repeats of
    /// this lane's own no-provenance shape answered <c>A</c> at three turns and <c>B</c> at one, so a
    /// run that varied the provenance at one window only could not tell "the provenance decides it"
    /// from "the window decides it and the provenance is noise". Four calls, one variable, two windows.
    /// </para>
    /// </remarks>
    private static async Task SiteProvenance(Runner runner)
    {
        var window = Threads.All[0].PriorTurns;

        var shapes = new (string Name, bool Authenticated, int Turns)[]
        {
            ("3-turn, trusted provenance", true, 3),
            ("3-turn, no provenance", false, 3),
            ("1-turn, trusted provenance", true, 1),
            ("1-turn, no provenance", false, 1),
        };

        Console.WriteLine("== site-provenance: the authentication block varied alone, both windows ==");
        Console.WriteLine($"tenant        : {HostTenantId}");
        Console.WriteLine($"profile       : absent in every row");
        Console.WriteLine();
        Console.WriteLine($"{"shape",-30} {"window",-7} {"continuity",-11} adapter digest");
        Console.WriteLine(new string('-', 110));

        var measured = new List<(string Name, int Turns, bool Authenticated, string Continuity, string? Digest)>();

        foreach (var (name, authenticated, turns) in shapes)
        {
            IReadOnlyList<string>? prior = [.. window.Take(turns)];

            var input = Corpus.Input(
                "reply-in-thread", prior, authenticated, HostVerifierId, profile: null, tenantId: HostTenantId);

            var result = await runner.CallAsync(input, CancellationToken.None);

            Console.WriteLine($"{name,-30} {turns,-7} {result.Continuity,-11} {result.KeyDigest ?? "none"}");
            Console.Out.Flush();

            measured.Add((name, turns, authenticated, result.Continuity, result.KeyDigest));
        }

        Console.WriteLine();

        // The claim is only about the three-turn window, where the split was measured; the one-turn
        // rows are the control that stops a difference there being read as a difference everywhere.
        var withProvenance = measured
            .Where(r => r.Turns == 3 && r.Authenticated).Select(r => r.Continuity).FirstOrDefault();
        var without = measured
            .Where(r => r.Turns == 3 && !r.Authenticated).Select(r => r.Continuity).FirstOrDefault();

        if (withProvenance is null || without is null)
        {
            Console.WriteLine(
                "INCONCLUSIVE: a three-turn row did not produce a continuity answer, so the pair this "
                + "run exists to compare was not completed.");
        }
        else
        {
            Console.WriteLine(string.Equals(withProvenance, without, StringComparison.Ordinal)
                ? "NO SEPARATION at three turns: the authentication block does not move the answer, so "
                    + "the field that separates A from B is still elsewhere and this run narrows the "
                    + "search by exactly one field."
                : $"SEPARATED at three turns: the same message, the same window, the same absent profile "
                    + $"and the same tenant answer {withProvenance} with a trusted authentication block, "
                    + $"and {without} without one. The authentication block is the site of the A/B split "
                    + "on this fixture, and it is the field this lane's own measurements have never "
                    + "carried.");
        }

        Write("site-provenance", new
        {
            tenant = HostTenantId,
            verifier = HostVerifierId,
            design = "the authentication block varied alone against the same fixture, window, profile and "
                + "tenant, at both window lengths the committed arms use",
            rows = measured.Select(r => new
            {
                shape = r.Name,
                window_turns = r.Turns,
                authenticated = r.Authenticated,
                continuity = r.Continuity,
                adapter_digest = r.Digest,
            }),
        });
        Console.WriteLine();
    }

    /// <summary>
    /// The A/B axis asked again under the provenance the Host actually sends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is owed.</b> Decision 34's discrimination pair is the condition for continuity's
    /// re-entry to the index, and continuity is masked in both directions until it exists. M6 measured
    /// a clean axis without provenance: every reply-shaped body answered <c>A</c> three times, every
    /// control body answered <c>B</c> three times, nine conditions, no flips. That is a discriminating
    /// pair, but it was taken on this lane's own shape, and the site experiment has since shown that
    /// shape answers a different question from the Host's on the same bytes.
    /// </para>
    /// <para>
    /// <b>So ask the same four arms twice, differing only in provenance.</b> Two bodies (the reply that
    /// continues the thread, and this lane's control that does not), each with and without a trusted
    /// authentication block, everything else byte-identical: same constructor, same window, same
    /// absent profile, same tenant, same run. Three repeats because M2 measured zero variance in 40
    /// calls, so a flip here would be a flip in the question rather than noise.
    /// </para>
    /// <para>
    /// <b>Four readings, and they are not interchangeable.</b> If both bodies answer <c>B</c> with
    /// provenance, the axis collapses and continuity cannot discriminate where the Host asks it, which
    /// is evidence for the masking rather than against it. If the pair inverts (the reply answering
    /// <c>B</c> and the control <c>A</c>), the reading is inverted under provenance, which is the one
    /// outcome that must not be quietly folded into an index. If it separates the same way it does
    /// without provenance, the axis survives and the pair is owed at this provenance. And if the
    /// no-provenance arms fail to reproduce M6, the run is not comparable to it and says so.
    /// </para>
    /// </remarks>
    private static async Task AxisProvenance(Runner runner)
    {
        const int repeats = 3;

        var window = Threads.All[0].PriorTurns;
        var pair = Threads.All[2];

        var (jevBody, _, _, _) = JevReplyParts();
        var control = pair.Control;

        Console.WriteLine($"== axis-provenance: the same two bodies, with and without the Host's provenance ({repeats} repeats) ==");
        Console.WriteLine($"tenant        : {HostTenantId}");
        Console.WriteLine($"verifier      : {HostVerifierId}");
        Console.WriteLine();

        var conditions = new (string Name, SemanticMailInput Input)[]
        {
            ("A-body, trusted provenance", Threads.Input(
                pair, window, jevBody, authenticated: true, HostVerifierId, HostTenantId)),
            ("A-body, no provenance", Threads.Input(
                pair, window, jevBody, tenantId: HostTenantId)),
            ("B-body, trusted provenance", Threads.Input(
                pair, window, control, authenticated: true, HostVerifierId, HostTenantId)),
            ("B-body, no provenance", Threads.Input(
                pair, window, control, tenantId: HostTenantId)),
        };

        var answers = new Dictionary<string, List<CallResult>>(StringComparer.Ordinal);
        var done = 0;
        var total = conditions.Length * repeats;

        foreach (var round in Enumerable.Range(0, repeats))
        {
            // Interleaved, for the same reason M6 interleaves: a model that drifted across the run
            // would otherwise land entirely on one side of the axis.
            foreach (var (name, input) in conditions)
            {
                var result = await runner.CallAsync(input, CancellationToken.None);

                if (!answers.TryGetValue(name, out var list))
                {
                    answers[name] = list = [];
                }

                list.Add(result);
                done++;

                Console.WriteLine(
                    $"  [{done,2}/{total}] r{round + 1} {name,-30} continuity={result.Continuity,-15} "
                    + $"answered={result.Answered}/{result.Asked}");
                Console.Out.Flush();
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{"condition",-30} {"continuity",-16} calls");
        Console.WriteLine(new string('-', 70));

        foreach (var (name, _) in conditions)
        {
            var list = answers[name];
            var spread = string.Join("/", list.Select(r => r.Continuity));
            Console.WriteLine($"{name,-30} {spread,-16} {list.Count}");
        }

        Console.WriteLine();

        static string? Sole(IReadOnlyList<CallResult> list)
        {
            var first = list[0].Continuity;
            return list.All(r => string.Equals(r.Continuity, first, StringComparison.Ordinal)) ? first : null;
        }

        var replyWith = Sole(answers["A-body, trusted provenance"]);
        var replyWithout = Sole(answers["A-body, no provenance"]);
        var controlWith = Sole(answers["B-body, trusted provenance"]);
        var controlWithout = Sole(answers["B-body, no provenance"]);

        if (replyWithout is null || controlWithout is null)
        {
            Console.WriteLine(
                "NOT COMPARABLE to M6: a no-provenance arm did not answer the same way every time, so "
                + "the calibration this run depends on is not reproduced and nothing here should be "
                + "read against the earlier axis.");
        }
        else if (!string.Equals(replyWithout, "A", StringComparison.Ordinal)
            || !string.Equals(controlWithout, "B", StringComparison.Ordinal))
        {
            Console.WriteLine(
                $"NOT COMPARABLE to M6: its no-provenance axis was reply=A and control=B, and this run "
                + $"measured reply={replyWithout} and control={controlWithout} without provenance. The "
                + "run differs from M6 in something other than provenance.");
        }
        else if (replyWith is null || controlWith is null)
        {
            Console.WriteLine(
                "INCONCLUSIVE WITH PROVENANCE: a provenance arm did not answer the same way every time, "
                + "so there is no single reading to compare.");
        }
        else if (string.Equals(replyWith, controlWith, StringComparison.Ordinal))
        {
            Console.WriteLine(
                $"THE AXIS COLLAPSES UNDER PROVENANCE: with a trusted authentication block, the reply "
                + $"that continues the thread and this lane's control that does not both answer "
                + $"{replyWith}, three times each, where without provenance they separate. Continuity "
                + "cannot discriminate where the Host asks it, and a mask that excludes it is excluding "
                + "a dimension that is constant whenever it is available.");
        }
        else if (string.Equals(replyWith, controlWithout, StringComparison.Ordinal)
            && string.Equals(controlWith, replyWithout, StringComparison.Ordinal))
        {
            Console.WriteLine(
                $"THE AXIS INVERTS UNDER PROVENANCE: the reply answers {replyWith} with provenance and "
                + $"the control answers {controlWith}, which is the opposite way round from the "
                + "no-provenance pair. An inverted reading must not be folded into an index, and this "
                + "is the measurement that says so.");
        }
        else
        {
            Console.WriteLine(
                $"THE AXIS SURVIVES PROVENANCE: reply={replyWith}, control={controlWith}, separating the "
                + "same way as without provenance. The discrimination pair is available at the "
                + "provenance the Host sends, and the no-provenance pair must not be offered in its "
                + "place.");
        }

        Write("axis-provenance", new
        {
            tenant = HostTenantId,
            verifier = HostVerifierId,
            repeats,
            design = "two bodies (a reply that continues the thread, and this lane's control that does "
                + "not) through one constructor, with and without a trusted authentication block, "
                + "everything else byte-identical",
            conditions = conditions.Select(c => new
            {
                condition = c.Name,
                continuity = answers[c.Name].Select(r => r.Continuity).ToArray(),
                digest = answers[c.Name][0].KeyDigest,
            }),
        });
        Console.WriteLine();
    }

    /// <summary>
    /// One field at a time, off the Host's own captured input, until the answer moves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the anchor is the capture and not a reconstruction.</b> Two runs have now disagreed
    /// about which field explains the split, and both were built from constructions of this lane's.
    /// The one object that is not a construction is the input the capture produced, whose cache key
    /// reproduces the digest the Host served. So the baseline here is that object, and every variant
    /// is it with exactly one canonicalised field changed. A flip can then be read as a cause rather
    /// than as a difference between two guesses.
    /// </para>
    /// <para>
    /// <b>The two candidates, and why these two.</b> The construction that answers <c>B</c> carries
    /// <c>sender@example.test</c> to <c>recipient@example.test</c>; the construction that answers
    /// <c>A</c> carries the thread's own participants, <c>alice@example.example</c> to
    /// <c>orders@northwind.example</c>. Both are canonicalised, both are rendered, and nothing else
    /// in the canonicalised input differs between them. They are varied separately as well as
    /// together, so a joint effect and a single-field effect can be told apart.
    /// </para>
    /// <para>
    /// <b>Provenance is varied on the same object for the same reason.</b> Its effect was measured on
    /// a reconstruction; if removing it from the Host's actual input does not reproduce <c>A</c>, then
    /// the earlier reading was a property of that reconstruction and has to be reported as one.
    /// </para>
    /// </remarks>
    private static async Task SiteEnvelope(Runner runner)
    {
        var window = Threads.All[0].PriorTurns;

        const string ThreadsSender = "alice@example.example";
        const string ThreadsRecipient = "orders@northwind.example";

        var analysis = Corpus.Analyze("reply-in-thread", window, authenticated: true, HostVerifierId, HostTenantId);

        var capture = await HostInputCapture.CaptureAsync(
            Path.Combine(ResultsDirectory, "capture"),
            analysis.Message!,
            HostTenantId,
            Corpus.Bytes("reply-in-thread"));

        if (capture.Input is null)
        {
            Console.WriteLine("REFUSING: the capture produced no input, so there is no anchor to vary.");
            return;
        }

        SemanticMailInput host = capture.Input;
        var served = ReadE2eDigest("arm2-reply-in-thread-mismatched");

        // The block the analyser substitutes when no provenance was supplied, taken from the shipping
        // analyser's own unauthenticated parse rather than hand-built: this lane's own shape carries
        // exactly this object, and a copy of it written here could drift from the original.
        var noProvenance = Corpus
            .Analyze("reply-in-thread", window, authenticated: false, HostVerifierId, HostTenantId)
            .Message!
            .Authentication;

        Console.WriteLine("== site-envelope: one field at a time, off the Host's own captured input ==");
        Console.WriteLine($"anchor digest : {capture.Digest ?? "none"}");
        Console.WriteLine($"served digest : {served ?? "NOT FOUND"}");
        Console.WriteLine(capture.Digest is not null && string.Equals(capture.Digest, served, StringComparison.Ordinal)
            ? "the anchor is the Host's input: its key reproduces the digest that arm was served."
            : "THE ANCHOR DOES NOT MATCH: what follows varies an input that is not the Host's.");
        Console.WriteLine();

        var variants = new (string Name, SemanticMailInput Input)[]
        {
            ("as captured", host),
            ("sender -> thread participant", host with
                { Message = host.Message with { Envelope = host.Message.Envelope with { MailFrom = ThreadsSender } } }),
            ("recipient -> thread participant", host with
                { Message = host.Message with { Envelope = host.Message.Envelope with { RcptTo = [ThreadsRecipient] } } }),
            ("both envelope fields", host with
                { Message = host.Message with { Envelope = host.Message.Envelope with
                    { MailFrom = ThreadsSender, RcptTo = [ThreadsRecipient] } } }),
            ("authentication -> none supplied", host with
                { Message = host.Message with { Authentication = noProvenance } }),
        };

        Console.WriteLine($"{"variant",-34} {"continuity",-11} adapter digest");
        Console.WriteLine(new string('-', 110));

        var measured = new List<(string Name, string Continuity, string? Digest)>();

        foreach (var (name, input) in variants)
        {
            var result = await runner.CallAsync(input, CancellationToken.None);

            Console.WriteLine($"{name,-34} {result.Continuity,-11} {result.KeyDigest ?? "none"}");
            Console.Out.Flush();

            measured.Add((name, result.Continuity, result.KeyDigest));
        }

        Console.WriteLine();

        var baseline = measured[0].Continuity;
        var flipped = measured.Skip(1).Where(r => !string.Equals(r.Continuity, baseline, StringComparison.Ordinal)).ToList();

        if (flipped.Count == 0)
        {
            Console.WriteLine(
                $"NO FLIP: every variant answered {baseline}, the same as the Host's own input. The "
                + "envelope addresses and the authentication block are both ruled out on the Host's "
                + "actual input, so the difference between the two constructions is in something "
                + "neither of them names.");
        }
        else
        {
            Console.WriteLine(
                $"FLIP: the Host's own input answers {baseline}, and changing one field moves it:");
            foreach (var (name, continuity, _) in flipped)
            {
                Console.WriteLine($"  {name} -> {continuity}");
            }

            Console.WriteLine(
                "A variant that flips is the site, and a variant that does not is excluded by the same "
                + "measurement rather than by argument.");
        }

        Write("site-envelope", new
        {
            tenant = HostTenantId,
            verifier = HostVerifierId,
            anchor_digest = capture.Digest,
            served_digest = served,
            anchor_matches_served = capture.Digest is not null
                && string.Equals(capture.Digest, served, StringComparison.Ordinal),
            baseline = measured[0].Continuity,
            variants = measured.Select(r => new
            {
                variant = r.Name,
                continuity = r.Continuity,
                adapter_digest = r.Digest,
            }),
        });
        Console.WriteLine();
    }

    /// <summary>
    /// Closes the site question: is the Host input's own answer stable, and does it move with the
    /// verifier string alone?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two questions, one hold on the endpoint, reported as two sections.</b> The local model is
    /// single and shared, so taking it twice to keep two artefacts tidy would spend another lane's
    /// time. The calls are interleaved rather than run in blocks, which is what makes the second
    /// section a comparison at all: if the machine or the model state drifts across the run, the
    /// verifier pair is read against anchors taken on both sides of it rather than against a block
    /// that might have been measured under a different state.
    /// </para>
    /// <para>
    /// <b>Section 1, the anchor, five calls.</b> Every single-field change to the Host's captured
    /// input moved its answer from <c>B</c> to <c>A</c>. That is one phenomenon if <c>B</c> is a knife
    /// edge only that exact input reaches, and four independent sufficiencies if <c>B</c> is simply
    /// what that input answers, and the distinction decides what the site experiment concluded.
    /// Overview- also requires the with-provenance three-turn cell to carry the same n as the ten-call
    /// cell it is compared against; these five calls are what raises it from one. The adapter digest is
    /// printed every time, because a set of calls with a moving digest is not a set of repeats and the
    /// run has to say so rather than average them.
    /// </para>
    /// <para>
    /// <b>Section 2, the verifier string, two calls.</b> Overview-'s step 3, and the only variation the
    /// four-call pair could not separate: the block is otherwise identical and only <c>VerifierId</c>
    /// moves. `NimbleMessageState.cs:141` writes that string into the state, so if the answer follows
    /// it, the continuity dimension is partly steerable by a label the caller supplies, which is a
    /// design input rather than a footnote. The digest is the control on the cell: if it does not move
    /// when the string does, the change never reached the state and the cell is a change to nothing.
    /// </para>
    /// </remarks>
    private static async Task SiteClose(Runner runner)
    {
        const string AlternateVerifier = "conversation-measure-alternate";

        var window = Threads.All[0].PriorTurns;

        var analysis = Corpus.Analyze("reply-in-thread", window, authenticated: true, HostVerifierId, HostTenantId);

        var capture = await HostInputCapture.CaptureAsync(
            Path.Combine(ResultsDirectory, "capture"),
            analysis.Message!,
            HostTenantId,
            Corpus.Bytes("reply-in-thread"));

        if (capture.Input is null)
        {
            Console.WriteLine("REFUSING: the capture produced no input, so there is nothing to repeat.");
            return;
        }

        var served = ReadE2eDigest("arm2-reply-in-thread-mismatched");

        var anchorResults = capture.Input.Message.Authentication.Results;
        var trustedCount = anchorResults.Count(r => r.FromTrustedVerifier);
        var anchorVerifier = anchorResults.FirstOrDefault(r => r.FromTrustedVerifier)?.VerifierId;

        Console.WriteLine("== site-close: the anchor repeated, and the verifier string alone ==");
        Console.WriteLine($"anchor digest : {capture.Digest ?? "none"}");
        Console.WriteLine($"served digest : {served ?? "NOT FOUND"}");
        Console.WriteLine(capture.Digest is not null && string.Equals(capture.Digest, served, StringComparison.Ordinal)
            ? "the anchor is the Host's input: its key reproduces the digest that arm was served."
            : "THE ANCHOR DOES NOT MATCH: these repeats are not of the Host's input.");
        Console.WriteLine($"trusted results in the anchor: {trustedCount}, verifier string '{anchorVerifier ?? "none"}'");
        Console.WriteLine();

        // Section 2 needs a trusted result to vary. Without one an "alternate verifier" would edit a
        // field that is not there and the section would report the string as inert when nothing was
        // changed, so it is refused by name rather than run.
        var canVaryVerifier = trustedCount > 0 && anchorVerifier is not null;

        if (!canVaryVerifier)
        {
            Console.WriteLine(
                "REFUSING SECTION 2: the captured input carries no trusted authentication result, so an "
                + "alternate verifier string would be a change to nothing. Section 1 still runs.");
        }
        else
        {
            Console.WriteLine(
                $"section 2 holds the block fixed and moves only VerifierId, '{anchorVerifier}' to "
                + $"'{AlternateVerifier}'.");
        }

        Console.WriteLine();

        var alternate = canVaryVerifier
            ? capture.Input with
            {
                Message = capture.Input.Message with
                {
                    Authentication = capture.Input.Message.Authentication with
                    {
                        Results =
                        [
                            .. anchorResults.Select(r =>
                                r.FromTrustedVerifier ? r with { VerifierId = AlternateVerifier } : r),
                        ],
                    },
                },
            }
            : null;

        // Anchors on both sides of each verifier call, so section 2 is bracketed rather than blocked.
        string[] plan =
        [
            "anchor", "verifier", "anchor", "anchor", "verifier", "anchor", "anchor",
        ];

        var anchors = new List<(string Continuity, string? Digest)>();
        var verifiers = new List<(string Continuity, string? Digest)>();

        foreach (var step in plan)
        {
            if (step == "verifier" && alternate is null)
            {
                continue;
            }

            var isAnchor = step == "anchor";
            var result = await runner.CallAsync(isAnchor ? capture.Input : alternate!, CancellationToken.None);

            if (isAnchor)
            {
                anchors.Add((result.Continuity, result.KeyDigest));
            }
            else
            {
                verifiers.Add((result.Continuity, result.KeyDigest));
            }

            Console.WriteLine(
                $"  {step,-9} continuity={result.Continuity,-15} adapter digest={result.KeyDigest ?? "none"}");
            Console.Out.Flush();
        }

        Console.WriteLine();

        var anchorAnswers = anchors.Select(a => a.Continuity).ToList();
        var distinctAnchorAnswers = anchorAnswers.Distinct(StringComparer.Ordinal).ToList();
        var distinctAnchorDigests = anchors.Select(a => a.Digest).Distinct(StringComparer.Ordinal).Count();

        var anchorStable = distinctAnchorDigests == 1 && distinctAnchorAnswers.Count == 1;

        if (distinctAnchorDigests != 1)
        {
            Console.WriteLine(
                "SECTION 1 NOT A SET OF REPEATS: the adapter digest moved between calls, so these "
                + "answers are about different questions and none of them is a repeat of another.");
        }
        else if (distinctAnchorAnswers.Count == 1)
        {
            Console.WriteLine(
                $"SECTION 1 STABLE: {distinctAnchorAnswers[0]} on all {anchors.Count} calls of one input, "
                + "one digest. The answer the Host's own input produces is not a knife edge, so the four "
                + "single-field changes are four independent sufficiencies rather than one fragile answer "
                + "being nudged, and the with-provenance three-turn cell now carries "
                + $"{anchors.Count} observations where it carried one.");
        }
        else
        {
            Console.WriteLine(
                $"SECTION 1 UNSTABLE: {anchors.Count} calls of one unchanged input answered "
                + $"{string.Join(", ", anchorAnswers)}. The input sits on a boundary the model crosses on "
                + "its own, so a single observation of it is not a measurement and every earlier reading "
                + "of this shape has to be re-read with that in mind.");
        }

        string? verifierVerdict = null;

        if (verifiers.Count > 0)
        {
            var verifierAnswers = verifiers.Select(v => v.Continuity).Distinct(StringComparer.Ordinal).ToList();
            var verifierDigests = verifiers.Select(v => v.Digest).Distinct(StringComparer.Ordinal).ToList();

            // Three separate reasons the cell can fail to be a comparison, and they must not be
            // collapsed into each other. An anchor whose own digest moved is not one input, so the
            // pair has no baseline and "the digest did not move" would be a false statement about a
            // digest that moved. Only once the anchor is confirmed as a single input does comparing
            // the verifier digest against it mean anything.
            var anchorIsOneInput = distinctAnchorDigests == 1;
            var verifierIsOneInput = verifierDigests.Count == 1;
            var digestMoved = anchorIsOneInput
                && verifierIsOneInput
                && !string.Equals(verifierDigests[0], anchors[0].Digest, StringComparison.Ordinal);

            Console.WriteLine();

            if (!anchorIsOneInput)
            {
                verifierVerdict =
                    "not comparable: the anchor is not one input on this run, so there is no baseline "
                    + "for the alternate to differ from.";

                Console.WriteLine($"SECTION 2 {verifierVerdict}");
            }
            else if (!verifierIsOneInput)
            {
                verifierVerdict =
                    "not comparable: the two alternate-verifier calls do not agree on their own digest, "
                    + "so the pair is not a pair.";

                Console.WriteLine($"SECTION 2 {verifierVerdict}");
            }
            else if (!digestMoved)
            {
                verifierVerdict =
                    "the cell is a change to nothing: the adapter digest did not move when the verifier "
                    + "string did, so the string never reached the state and the answers below are the "
                    + "anchor's answers under a different label.";

                Console.WriteLine($"SECTION 2 {verifierVerdict}");
            }
            else if (distinctAnchorAnswers.Count != 1 || verifierAnswers.Count != 1)
            {
                verifierVerdict =
                    "not comparable: at least one side of the pair moved on its own, so the difference "
                    + "between them cannot be attributed to the string.";

                Console.WriteLine(
                    $"SECTION 2 {verifierVerdict} Anchors {string.Join(", ", anchorAnswers)}; "
                    + $"alternate '{AlternateVerifier}' {string.Join(", ", verifiers.Select(v => v.Continuity))}.");
            }
            else if (string.Equals(verifierAnswers[0], distinctAnchorAnswers[0], StringComparison.Ordinal))
            {
                verifierVerdict =
                    $"the caller-supplied label did not move the answer: {distinctAnchorAnswers[0]} under "
                    + $"'{anchorVerifier}' and {verifierAnswers[0]} under '{AlternateVerifier}', with the "
                    + "digest moving, so the string reached the state and the answer did not follow it.";

                Console.WriteLine($"SECTION 2 {verifierVerdict}");
            }
            else
            {
                verifierVerdict =
                    $"THE ANSWER FOLLOWS THE VERIFIER STRING: {distinctAnchorAnswers[0]} under "
                    + $"'{anchorVerifier}' against {verifierAnswers[0]} under '{AlternateVerifier}', on the "
                    + "same block with the digest confirming the string reached the state. The continuity "
                    + "dimension is partly steerable by a label the caller supplies.";

                Console.WriteLine($"SECTION 2 {verifierVerdict}");
            }
        }

        Write("site-close", new
        {
            tenant = HostTenantId,
            anchor_verifier = anchorVerifier,
            alternate_verifier = AlternateVerifier,
            trusted_result_count = trustedCount,
            anchor_digest = capture.Digest,
            served_digest = served,
            anchor_matches_served = capture.Digest is not null
                && string.Equals(capture.Digest, served, StringComparison.Ordinal),
            section_1_repeats = anchors.Count,
            section_1_answers = anchorAnswers,
            section_1_digests = anchors.Select(a => a.Digest).ToList(),
            section_1_stable = anchorStable,
            section_2_ran = verifiers.Count > 0,
            section_2_answers = verifiers.Select(v => v.Continuity).ToList(),
            section_2_digests = verifiers.Select(v => v.Digest).ToList(),
            section_2_verdict = verifierVerdict,
        });
        Console.WriteLine();
    }

    /// <summary>
    /// Replays every end-to-end arm's request through the shipping assessor and compares the digest
    /// it computes now with the digest that arm was served.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Every continuity figure this lane holds was read from an arm artifact
    /// written by an earlier build. A digest is an identity of the question the provider was asked,
    /// and the code that computes it can change underneath the artifact: a field added to the state,
    /// a version constant moved, a window constant retuned. When it does, the recorded answer is an
    /// answer to a question nothing is asking any more, and nothing in the artifact says so.
    /// </para>
    /// <para>
    /// <b>No model call, and none is possible.</b> The assessor runs with a capturing stub where the
    /// provider would be, exactly as the anchor capture does, so this measures input assembly only.
    /// It can be run as often as the argument needs it rather than budgeted against a shared endpoint.
    /// </para>
    /// <para>
    /// <b>It is not a re-measurement.</b> A reproduced digest says the arm's recorded answer is still
    /// an answer about this code. It says nothing about whether the model would answer the same way
    /// today, which is a different question and needs calls.
    /// </para>
    /// </remarks>
    private static async Task ArmsCurrent()
    {
        var directory = Path.Combine(ResultsDirectory, "e2e", "results");

        var arms = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.request.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is not null && n.EndsWith(".request", StringComparison.Ordinal))
                .Select(n => n![..^".request".Length])
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList()
            : [];

        Console.WriteLine("== arms-current: every arm replayed through the shipping assessor, no model call ==");

        if (arms.Count == 0)
        {
            Console.WriteLine($"REFUSING: no request artifacts under {directory}, so there is nothing to replay.");
            return;
        }

        Console.WriteLine($"arms       : {arms.Count}");
        Console.WriteLine();

        var rows = new List<object>();
        var stale = new List<string>();

        foreach (var arm in arms)
        {
            var served = ReadE2eDigest(arm);
            var replayed = await ReplayArm(directory, arm);

            var current = served is not null
                && replayed is not null
                && string.Equals(replayed, served, StringComparison.Ordinal);

            if (!current)
            {
                stale.Add(arm);
            }

            Console.WriteLine($"{arm,-48} {(current ? "current" : "STALE")}");
            Console.WriteLine($"    served by the Host then : {served ?? "NOT FOUND"}");
            Console.WriteLine($"    computed by this code   : {replayed ?? "not replayed"}");

            rows.Add(new
            {
                arm,
                served_digest = served,
                replayed_digest = replayed,
                current,
            });
        }

        Console.WriteLine();

        Console.WriteLine(stale.Count == 0
            ? $"ALL {arms.Count} ARMS CURRENT: every recorded arm digest is reproduced by the code in the "
                + "tree now, so each arm's continuity row is an answer about the question this code asks. "
                + "Nothing here says the model would answer the same way today."
            : $"{stale.Count} ARM(S) STALE: {string.Join(", ", stale)}. A row read from a stale arm is a "
                + "figure about a question this code no longer asks, and it must not be quoted without "
                + "that fact beside it.");

        Write("arms-current", new
        {
            tenant = HostTenantId,
            arms = rows,
            stale = stale,
        });
        Console.WriteLine();
    }

    /// <summary>
    /// One arm's request artifact, rebuilt as the classifier input the shipping assessor makes of it.
    /// </summary>
    /// <remarks>
    /// The envelope, the window and the authentication block are read from the artifact rather than
    /// reconstructed from this lane's fixtures, because the artifact is what the Host was actually
    /// sent. The path is validated by the arms whose request bytes are a committed fixture: replaying
    /// those reproduces the digest that is already known to match, so a mismatch on another arm is a
    /// finding about that arm rather than about the replay.
    /// </remarks>
    private static async Task<string?> ReplayArm(string directory, string arm)
    {
        var path = Path.Combine(directory, arm + ".request.json");

        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var raw = Convert.FromBase64String(root.GetProperty("rawMime").GetString() ?? string.Empty);

        var window = root.TryGetProperty("conversationContext", out var context)
            ? context.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
            : [];

        var recipients = root.TryGetProperty("rcptTo", out var rcpt)
            ? rcpt.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
            : [];

        var envelope = new MailEnvelope
        {
            InternalMessageId = $"conversation-measure-replay-{arm}",
            TenantId = HostTenantId,
            Direction = root.TryGetProperty("direction", out var direction)
                && string.Equals(direction.GetString(), "Outbound", StringComparison.OrdinalIgnoreCase)
                    ? MailDirection.Outbound
                    : MailDirection.Inbound,
            TrustedPrincipalId = "conversation-measure",
            MailFrom = root.TryGetProperty("mailFrom", out var from) ? from.GetString() ?? string.Empty : string.Empty,
            RcptTo = recipients,
            ReceivedAt = DateTimeOffset.UnixEpoch,
            MimeDigest = Convert.ToHexStringLower(SHA256.HashData(raw)),
            PayloadReference = PayloadReferences.Ephemeral,
        };

        var analysis = new BoundedMimeMessageAnalyzer().Analyze(new MimeAnalysisRequest
        {
            Envelope = envelope,
            RawMessage = raw,
            ConversationContext = window.Count == 0 ? null : window,
            Authentication = RequestAuthentication(root),
        });

        if (!analysis.IsAnalysable)
        {
            return null;
        }

        var capture = await HostInputCapture.CaptureAsync(
            Path.Combine(ResultsDirectory, "replay"),
            analysis.Message!,
            HostTenantId,
            raw);

        return capture.Digest;
    }

    /// <summary>
    /// The authentication block a request artifact carried, or null when it carried none.
    /// </summary>
    /// <remarks>
    /// <b>Absent and empty are the same here on purpose</b>, because they reach the provider the same
    /// way: only trusted results are rendered, so a block with no trusted result presents exactly as
    /// no block does. The arm that exercises that is the one with no authentication at all.
    /// </remarks>
    private static AuthenticationContext? RequestAuthentication(JsonElement root)
    {
        if (!root.TryGetProperty("authenticationResults", out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
        {
            return null;
        }

        return new AuthenticationContext
        {
            ConnectingIp = root.TryGetProperty("connectingIp", out var ip) ? ip.GetString() ?? string.Empty : string.Empty,
            Results =
            [
                .. results.EnumerateArray().Select(r => new AuthenticationResult
                {
                    Mechanism = r.TryGetProperty("mechanism", out var m) ? m.GetString() ?? string.Empty : string.Empty,
                    Result = r.TryGetProperty("result", out var res) ? res.GetString() ?? string.Empty : string.Empty,
                    VerifierId = r.TryGetProperty("verifierId", out var v) ? v.GetString() ?? string.Empty : string.Empty,
                    FromTrustedVerifier = r.TryGetProperty("fromTrustedVerifier", out var t)
                        && t.ValueKind == JsonValueKind.True,
                    Detail = r.TryGetProperty("detail", out var d) ? d.GetString() : null,
                }),
            ],
            ApprovedSenderIdentities = [],
            ProvenanceIncomplete = false,
        };
    }

    /// <summary>
    /// The window's participants alone, on the serving shape, in both directions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why only half the prediction is left.</b> <c>overview-</c>'s prediction is a conjunction:
    /// changing the window's participants moves the letter and changing the body does not. The second
    /// half is already measured, and refuted, by three committed arms. Arms 2, 5 and 6 share a
    /// byte-identical header block, the same envelope, the same three-turn window and the same
    /// authentication block, and differ only in the body; they answered B, A and B. So the body does
    /// move the letter on the serving shape, and only the participant half is still open.
    /// </para>
    /// <para>
    /// <b>Two directions, because a match and a change are different readings.</b> Setting the
    /// window's senders to the message's own counterparty tests "the envelope address is among the
    /// window's participants". Setting them to a party in neither the window nor the envelope tests
    /// "any change to the window's participants moves it". Both moving is a change-sensitivity rather
    /// than a match, and that distinction is the reason the second arm exists.
    /// </para>
    /// <para>
    /// <b>Only the addresses move.</b> The display names and the bodies of the prior turns are held,
    /// so the change is the participants rather than the window's prose.
    /// </para>
    /// </remarks>
    private static async Task SiteParticipants(Runner runner, bool dry)
    {
        const string ThirdParty = "thirdparty@example.test";

        var window = Threads.All[0].PriorTurns;

        var analysis = Corpus.Analyze("reply-in-thread", window, authenticated: true, HostVerifierId, HostTenantId);

        var capture = await HostInputCapture.CaptureAsync(
            Path.Combine(ResultsDirectory, "capture"),
            analysis.Message!,
            HostTenantId,
            Corpus.Bytes("reply-in-thread"));

        if (capture.Input is null)
        {
            Console.WriteLine("REFUSING: the capture produced no input, so there is no anchor to vary.");
            return;
        }

        var served = ReadE2eDigest("arm2-reply-in-thread-mismatched");
        var anchor = capture.Input;
        var turns = anchor.Message.ConversationContext ?? [];

        Console.WriteLine("== site-participants: the window's participants alone, both directions ==");
        Console.WriteLine($"anchor digest : {capture.Digest ?? "none"}");
        Console.WriteLine($"served digest : {served ?? "NOT FOUND"}");
        Console.WriteLine(capture.Digest is not null && string.Equals(capture.Digest, served, StringComparison.Ordinal)
            ? "the anchor is the Host's input: its key reproduces the digest that arm was served."
            : "THE ANCHOR DOES NOT MATCH: what follows varies an input that is not the Host's.");
        Console.WriteLine($"window turns  : {turns.Count}");
        Console.WriteLine();

        var ownSender = anchor.Message.Envelope.MailFrom;

        var own = RewriteParticipants(turns, ownSender);
        var third = RewriteParticipants(turns, ThirdParty);

        if (own is null || third is null)
        {
            Console.WriteLine(
                "REFUSING: at least one prior turn carries no angle address, so 'the window's "
                + "participants' has no site in it and a rewrite would be a change to nothing.");
            return;
        }

        var participantsOwn = anchor with { Message = anchor.Message with { ConversationContext = own } };
        var participantsThird = anchor with { Message = anchor.Message with { ConversationContext = third } };

        Console.WriteLine(
            "the window's sender addresses are replaced, the display names, the subjects and the turn "
            + "bodies are held. 'own' is the message's own envelope sender; 'third' is a party in "
            + "neither the window nor the envelope.");
        Console.WriteLine();

        // The dry check answers the only question this cell can fail silently on: did the rewrite
        // actually reach every turn? A window with one turn left alone is a change to less than the
        // participants, and the run would read as a statement about participants it never moved.
        var ownChanged = own.Zip(turns).Count(p => !string.Equals(p.First, p.Second, StringComparison.Ordinal));
        var thirdChanged = third.Zip(turns).Count(p => !string.Equals(p.First, p.Second, StringComparison.Ordinal));

        Console.WriteLine($"turns rewritten to the message's own sender : {ownChanged} of {turns.Count}");
        Console.WriteLine($"turns rewritten to a third party             : {thirdChanged} of {turns.Count}");

        if (dry)
        {
            Console.WriteLine(
                ownChanged == turns.Count && thirdChanged == turns.Count
                    ? "DRY: both variants rewrite every turn, so each is a change to the window's "
                        + "participants and nothing else. No call was made."
                    : "DRY: at least one turn was not rewritten, so a run would report a change that did "
                        + "not happen. No call was made.");

            Console.WriteLine();
            return;
        }

        if (ownChanged != turns.Count || thirdChanged != turns.Count)
        {
            Console.WriteLine(
                "REFUSING: at least one prior turn was left unchanged, so a run would report the "
                + "participants as having been moved when they had not been. No call was made.");
            return;
        }

        Console.WriteLine();

        var plan = new (string Label, SemanticMailInput Input)[]
        {
            ("anchor", anchor),
            ("own", participantsOwn), ("own", participantsOwn), ("own", participantsOwn),
            ("third", participantsThird), ("third", participantsThird), ("third", participantsThird),
            ("anchor", anchor),
        };

        var answers = new Dictionary<string, List<(string Continuity, string? Digest)>>(StringComparer.Ordinal)
        {
            ["anchor"] = [],
            ["own"] = [],
            ["third"] = [],
        };

        foreach (var (label, input) in plan)
        {
            var result = await runner.CallAsync(input, CancellationToken.None);

            answers[label].Add((result.Continuity, result.KeyDigest));

            Console.WriteLine(
                $"  {label,-8} continuity={result.Continuity,-15} adapter digest={result.KeyDigest ?? "none"}");
            Console.Out.Flush();
        }

        Console.WriteLine();

        var anchors = answers["anchor"];
        var anchorSet = OneSet(anchors);
        var ownSet = OneSet(answers["own"]);
        var thirdSet = OneSet(answers["third"]);

        string verdict;

        if (!anchorSet || !ownSet || !thirdSet)
        {
            verdict =
                "not comparable: at least one group of calls did not answer one letter at one digest, so "
                + "the groups are not sets of repeats and a difference between them is not the change.";
        }
        else
        {
            var baseline = anchors[0].Continuity;
            var ownMoved = !string.Equals(answers["own"][0].Continuity, baseline, StringComparison.Ordinal);
            var thirdMoved = !string.Equals(answers["third"][0].Continuity, baseline, StringComparison.Ordinal);

            verdict = (ownMoved, thirdMoved) switch
            {
                (true, false) =>
                    $"THE WINDOW'S PARTICIPANTS MOVE THE LETTER: setting the window's senders to the "
                    + $"message's own counterparty moves {baseline} to {answers["own"][0].Continuity} "
                    + $"({answers["own"].Count} of {answers["own"].Count}), while a third party leaves it "
                    + $"at {answers["third"][0].Continuity}. That is a match between the envelope address "
                    + "and the window's participants, and it is the reading the prediction describes.",
                (false, true) =>
                    $"THE LETTER MOVED FOR A THIRD PARTY AND NOT FOR THE MESSAGE'S OWN COUNTERPARTY: "
                    + $"third party {answers["third"][0].Continuity} against own {answers["own"][0].Continuity} "
                    + $"and the anchor's {baseline}. That is the opposite of a matching effect. Reported as "
                    + "measured, and not explained here.",
                (true, true) =>
                    $"THE WINDOW'S PARTICIPANTS MOVE THE LETTER, BUT NOT BY MATCHING: both the message's own "
                    + $"counterparty and a third party move it off {baseline}, to {answers["own"][0].Continuity} "
                    + $"and {answers["third"][0].Continuity}. The letter responds to the window's participants "
                    + "changing at all rather than to the envelope address being among them, which is a "
                    + "change-sensitivity and not the reading the prediction describes.",
                _ =>
                    $"THE WINDOW'S PARTICIPANTS ARE NOT THE SITE ON THIS SHAPE: both a counterparty match and "
                    + $"a third party leave the answer at {baseline}, so the letter is not read against the "
                    + "window's participants on the Host's own input.",
            };
        }

        Console.WriteLine(verdict);

        Write("site-participants", new
        {
            tenant = HostTenantId,
            anchor_digest = capture.Digest,
            served_digest = served,
            anchor_matches_served = capture.Digest is not null
                && string.Equals(capture.Digest, served, StringComparison.Ordinal),
            window_turns = turns.Count,
            anchor_answers = anchors.Select(a => a.Continuity).ToList(),
            anchor_digests = anchors.Select(a => a.Digest).ToList(),
            own_answers = answers["own"].Select(a => a.Continuity).ToList(),
            own_digests = answers["own"].Select(a => a.Digest).ToList(),
            third_answers = answers["third"].Select(a => a.Continuity).ToList(),
            third_digests = answers["third"].Select(a => a.Digest).ToList(),
            verdict,
        });
        Console.WriteLine();
    }

    /// <summary>
    /// The parts of a prior turn held while the window's senders move, named once for the artifact.
    /// </summary>
    /// <remarks>
    /// Held rather than varied because the accepted cell held them too, so every move in this series
    /// of cells is on the same field, the sender address. Recorded in the artifact so the choice is
    /// stated rather than inferred from the letters.
    /// </remarks>
    private static readonly string[] HeldFields = ["display names", "subjects", "turn bodies"];

    /// <summary>
    /// The confound <c>site-participants</c> named: does the letter follow who the window's senders
    /// are, or how many of them there are?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this cell exists.</b> <c>site-participants</c> replaced the window's sender addresses
    /// with one address written into every turn, so both of its variants also collapsed the window
    /// from two distinct senders to one. Its two directions therefore shared a structural change and
    /// the reading it supports is the coarser one: two distinct senders answer B, one answers A. That
    /// is a statement about the count and not about the identity, and this cell separates them.
    /// </para>
    /// <para>
    /// <b>The two conditions differ in one turn's address.</b> The anchor window's senders are mapped
    /// through a bijection onto two new addresses, which preserves the equality pattern across turns
    /// exactly, so the count of distinct senders is held and only who they are moves. The count
    /// condition then writes the first of those new addresses into all three turns. The two
    /// conditions are identical in turn 1 and turn 3 and differ only in turn 2, so a difference
    /// between them is the address of one turn and nothing else.
    /// </para>
    /// <para>
    /// <b>Held fields, stated because they are a choice.</b> Display names, subjects and turn bodies
    /// are held, exactly as in the accepted cell, so what moves is the address in both conditions.
    /// A turn whose display name then disagrees with its address is the same held-field consequence
    /// the accepted cell had, and it is named in the artifact rather than left to be noticed.
    /// </para>
    /// <para>
    /// <b>Read against the anchor, not against each other alone.</b> The anchor is the Host's own
    /// captured input, reproduced in-run by its key. Its letter is already established by six prior
    /// observations, so the discriminator is whether the identity condition moves off the anchor
    /// while the count condition does.
    /// </para>
    /// </remarks>
    private static async Task ParticipantsSharp(Runner runner, bool dry)
    {
        // Two addresses in neither the window nor the envelope. The envelope's own sender is
        // deliberately not one of them: writing it in would make the identity condition the
        // `own` condition of the cell this one is sharpening.
        const string NewFirst = "sharp-one@example.test";
        const string NewSecond = "sharp-two@example.test";

        var window = Threads.All[0].PriorTurns;

        var analysis = Corpus.Analyze("reply-in-thread", window, authenticated: true, HostVerifierId, HostTenantId);

        var capture = await HostInputCapture.CaptureAsync(
            Path.Combine(ResultsDirectory, "capture"),
            analysis.Message!,
            HostTenantId,
            Corpus.Bytes("reply-in-thread"));

        if (capture.Input is null)
        {
            Console.WriteLine("REFUSING: the capture produced no input, so there is no anchor to vary.");
            return;
        }

        var served = ReadE2eDigest("arm2-reply-in-thread-mismatched");
        var anchor = capture.Input;
        var turns = anchor.Message.ConversationContext ?? [];
        var ownSender = anchor.Message.Envelope.MailFrom;

        Console.WriteLine("== participants-sharp: is it who the window's senders are, or how many there are? ==");
        Console.WriteLine($"anchor digest : {capture.Digest ?? "none"}");
        Console.WriteLine($"served digest : {served ?? "NOT FOUND"}");
        Console.WriteLine(capture.Digest is not null && string.Equals(capture.Digest, served, StringComparison.Ordinal)
            ? "the anchor is the Host's input: its key reproduces the digest that arm was served."
            : "THE ANCHOR DOES NOT MATCH: what follows varies an input that is not the Host's.");
        Console.WriteLine($"window turns  : {turns.Count}");
        Console.WriteLine();

        var originals = new List<string>(turns.Count);

        for (var i = 0; i < turns.Count; i++)
        {
            var address = SenderAddress(turns[i]);

            if (address is null)
            {
                Console.WriteLine(
                    $"REFUSING: prior turn {i + 1} carries no sender address, so 'who the window's "
                    + "senders are' has no site in it and a rewrite would be a change to nothing.");
                return;
            }

            originals.Add(address);
        }

        var distinct = originals.Distinct(StringComparer.Ordinal).ToList();

        Console.WriteLine($"distinct senders in the anchor window: {distinct.Count}");

        // A one-sender anchor cannot express "hold the count and move the identity": the bijection
        // would have nothing to map and the identity condition would be the count condition.
        if (distinct.Count < 2)
        {
            Console.WriteLine(
                $"REFUSING: the window carries {distinct.Count} distinct sender address(es), so the "
                + "identity condition cannot hold the count while it moves the identity. No call was made.");
            return;
        }

        // The bijection must not collide with the envelope's sender, or the identity condition becomes
        // the `own` condition of the cell this one exists to sharpen.
        if (string.Equals(NewFirst, NewSecond, StringComparison.Ordinal)
            || string.Equals(NewFirst, ownSender, StringComparison.Ordinal)
            || string.Equals(NewSecond, ownSender, StringComparison.Ordinal)
            || originals.Contains(NewFirst, StringComparer.Ordinal)
            || originals.Contains(NewSecond, StringComparer.Ordinal))
        {
            Console.WriteLine(
                "REFUSING: a replacement address collides with another replacement, with the "
                + "envelope's own sender, or with an address already in the window. No call was made.");
            return;
        }

        // First appearance decides the pairing, so the mapping is a bijection and the per-turn
        // equality pattern survives it unchanged.
        var replacements = new List<string>(turns.Count);

        for (var i = 0; i < originals.Count; i++)
        {
            replacements.Add(distinct.IndexOf(originals[i]) % 2 == 0 ? NewFirst : NewSecond);
        }

        var identityTurns = RewriteSenders(turns, replacements);
        var countTurns = RewriteSenders(turns, [.. turns.Select(_ => NewFirst)]);

        if (identityTurns is null || countTurns is null)
        {
            Console.WriteLine(
                "REFUSING: at least one prior turn was left unchanged, so a run would report the "
                + "window's senders as having moved when they had not been. No call was made.");
            return;
        }

        var identityDistinct = identityTurns
            .Select(t => SenderAddress(t) ?? "?")
            .Distinct(StringComparer.Ordinal)
            .Count();

        var countDistinct = countTurns
            .Select(t => SenderAddress(t) ?? "?")
            .Distinct(StringComparer.Ordinal)
            .Count();

        // Checked rather than assumed: the bijection preserving the equality pattern is the whole
        // basis for calling the first condition a move of identity with the count held, and a
        // replacement that silently collided would make it a second count condition.
        if (identityDistinct != distinct.Count)
        {
            Console.WriteLine(
                $"REFUSING: the identity condition carries {identityDistinct} distinct senders against "
                + $"the anchor's {distinct.Count}, so it moves the count as well as the identity and is "
                + "not the condition it is reported as. No call was made.");
            return;
        }

        if (countDistinct != 1)
        {
            Console.WriteLine(
                $"REFUSING: the count condition carries {countDistinct} distinct senders rather than 1. "
                + "No call was made.");
            return;
        }

        var differing = identityTurns
            .Zip(countTurns)
            .Count(p => !string.Equals(p.First, p.Second, StringComparison.Ordinal));

        // The per-turn equality pattern, not the addresses: which turns share a sender is the whole
        // content of "how many distinct senders there are", and an address is not printed by this
        // lane. A bijection cannot change the pattern, which is why the identity condition can hold
        // the count while it moves who the senders are.
        Console.WriteLine($"  anchor   pattern {Pattern(originals)}");
        Console.WriteLine($"  identity pattern {Pattern(identityTurns.Select(t => SenderAddress(t) ?? "?").ToList())}");
        Console.WriteLine($"  count    pattern {Pattern(countTurns.Select(t => SenderAddress(t) ?? "?").ToList())}");
        Console.WriteLine();
        Console.WriteLine(
            $"the two conditions differ in {differing} of {turns.Count} turn(s); the count is held at "
            + $"{distinct.Count} by the identity condition and moved to 1 by the count condition.");
        Console.WriteLine();

        if (dry)
        {
            Console.WriteLine(
                differing == 1
                    ? "DRY: the identity condition holds the count and moves the identity, the count "
                        + "condition moves the count, and the two differ in exactly one turn's address. "
                        + "No call was made."
                    : "DRY: the two conditions differ in more than one turn's address, so a difference "
                        + "between them would not be the count alone. No call was made.");

            Console.WriteLine();
            return;
        }

        var identityInput = anchor with { Message = anchor.Message with { ConversationContext = identityTurns } };
        var countInput = anchor with { Message = anchor.Message with { ConversationContext = countTurns } };

        var plan = new (string Label, SemanticMailInput Input)[]
        {
            ("anchor", anchor),
            ("identity", identityInput), ("identity", identityInput), ("identity", identityInput),
            ("count", countInput), ("count", countInput), ("count", countInput),
            ("anchor", anchor),
        };

        var answers = new Dictionary<string, List<(string Continuity, string? Digest)>>(StringComparer.Ordinal)
        {
            ["anchor"] = [],
            ["identity"] = [],
            ["count"] = [],
        };

        foreach (var (label, input) in plan)
        {
            var result = await runner.CallAsync(input, CancellationToken.None);

            answers[label].Add((result.Continuity, result.KeyDigest));

            Console.WriteLine(
                $"  {label,-9} continuity={result.Continuity,-15} adapter digest={result.KeyDigest ?? "none"}");
            Console.Out.Flush();
        }

        Console.WriteLine();

        var anchors = answers["anchor"];

        var comparable = OneSet(anchors) && OneSet(answers["identity"]) && OneSet(answers["count"]);

        string verdict;

        if (!comparable)
        {
            verdict =
                "not comparable: at least one group of calls did not answer one letter at one digest, so "
                + "the groups are not sets of repeats and a difference between them is not the change.";
        }
        else
        {
            var baseline = anchors[0].Continuity;
            var identityMoved = !string.Equals(answers["identity"][0].Continuity, baseline, StringComparison.Ordinal);
            var countMoved = !string.Equals(answers["count"][0].Continuity, baseline, StringComparison.Ordinal);

            verdict = (identityMoved, countMoved) switch
            {
                (false, true) =>
                    $"THE LETTER FOLLOWS THE NUMBER OF DISTINCT SENDERS: holding the count at "
                    + $"{distinct.Count} and changing only who the senders are leaves the answer at "
                    + $"{baseline}, while dropping the count to 1 moves it to {answers["count"][0].Continuity} "
                    + $"({answers["count"].Count} of {answers["count"].Count}). So the confound in "
                    + "`site-participants` was the whole effect and its reading survives: two distinct "
                    + "senders answer B, one answers A.",
                (true, true) =>
                    $"THE SENDER IDENTITY MATTERS: changing only who the window's senders are, with the "
                    + $"count held at {distinct.Count}, moves the answer off {baseline} to "
                    + $"{answers["identity"][0].Continuity}, and dropping the count to 1 also reaches "
                    + $"{answers["count"][0].Continuity}. Both conditions are A against the anchor's "
                    + $"{baseline}, so the letter is not following the count alone and the confound in "
                    + "`site-participants` was not the whole effect.",
                (true, false) =>
                    $"THE LETTER FOLLOWS THE IDENTITY AND NOT THE COUNT: changing only who the window's "
                    + $"senders are moves the answer off {baseline} to {answers["identity"][0].Continuity}, "
                    + $"while dropping the count to 1 leaves it at {answers["count"][0].Continuity}. That is "
                    + "the opposite of the count reading and it is reported as measured, not explained.",
                _ =>
                    $"NEITHER CONDITION MOVES IT: with the count held at {distinct.Count} and with the "
                    + $"count at 1 the answer is {baseline}, the anchor's own letter. So the "
                    + "`site-participants` reading does not reproduce on this shape, and the confound it "
                    + "named is not the whole story either.",
            };
        }

        Console.WriteLine(verdict);

        Write("participants-sharp", new
        {
            tenant = HostTenantId,
            anchor_digest = capture.Digest,
            served_digest = served,
            anchor_matches_served = capture.Digest is not null
                && string.Equals(capture.Digest, served, StringComparison.Ordinal),
            window_turns = turns.Count,
            distinct_senders_in_anchor = distinct.Count,
            identity_distinct_senders = identityDistinct,
            count_distinct_senders = countDistinct,
            anchor_pattern = Pattern(originals),
            identity_pattern = Pattern(identityTurns.Select(t => SenderAddress(t) ?? "?").ToList()),
            count_pattern = Pattern(countTurns.Select(t => SenderAddress(t) ?? "?").ToList()),
            conditions_differ_in_turns = differing,
            held_fields = HeldFields,
            anchor_answers = anchors.Select(a => a.Continuity).ToList(),
            anchor_digests = anchors.Select(a => a.Digest).ToList(),
            identity_answers = answers["identity"].Select(a => a.Continuity).ToList(),
            identity_digests = answers["identity"].Select(a => a.Digest).ToList(),
            count_answers = answers["count"].Select(a => a.Continuity).ToList(),
            count_digests = answers["count"].Select(a => a.Digest).ToList(),
            verdict,
        });
        Console.WriteLine();
    }

    /// <summary>The sender address on a prior turn's first line, or null if the turn carries none.</summary>
    /// <remarks>
    /// The read half of <see cref="RewriteSender"/> and deliberately the same parse: a helper that
    /// found an address the rewrite could not replace would let a condition be reported as a move
    /// that never happened.
    /// </remarks>
    private static string? SenderAddress(string turn)
    {
        var newline = turn.IndexOf('\n');
        var firstLine = newline < 0 ? turn : turn[..newline];

        var open = firstLine.IndexOf('<');
        var close = firstLine.IndexOf('>');

        if (open >= 0 && close > open)
        {
            return firstLine[(open + 1)..close];
        }

        var lastSpace = firstLine.LastIndexOf(' ');
        var start = lastSpace + 1;
        var end = firstLine.Length;

        if (start >= end)
        {
            return null;
        }

        var token = firstLine[start..end];

        return token.Contains('@', StringComparison.Ordinal) ? token : null;
    }

    /// <summary>
    /// The window with each turn's sender replaced by the address at the same position, or null if
    /// any turn was left unchanged.
    /// </summary>
    /// <remarks>
    /// One address per turn rather than one address for all of them, because the condition that holds
    /// the number of distinct senders has to write a different address into a turn the anchor gave a
    /// different sender. <b>A turn that is not rewritten is a refusal, not a pass</b>, for the same
    /// reason it is in <see cref="RewriteParticipants"/>.
    /// </remarks>
    private static IReadOnlyList<string>? RewriteSenders(IReadOnlyList<string> turns, IReadOnlyList<string> addresses)
    {
        if (addresses.Count != turns.Count)
        {
            return null;
        }

        var rewritten = new List<string>(turns.Count);

        for (var i = 0; i < turns.Count; i++)
        {
            var changed = RewriteSender(turns[i], addresses[i]);

            if (ReferenceEquals(changed, turns[i]))
            {
                return null;
            }

            rewritten.Add(changed);
        }

        return rewritten;
    }

    /// <summary>
    /// Which turns share a sender, as the index of each turn's sender in order of first appearance.
    /// </summary>
    /// <remarks>
    /// An address is never printed by this lane, so the pattern is how a per-turn sender mapping is
    /// reported: <c>0, 1, 0</c> is two distinct senders alternating. It is also the property the
    /// identity condition has to preserve, since a bijection over the senders leaves this unchanged
    /// while the count of distinct senders stays where it was.
    /// </remarks>
    private static string Pattern(IReadOnlyList<string> senders)
    {
        var seen = new List<string>();

        var indices = new List<int>(senders.Count);

        foreach (var sender in senders)
        {
            var at = seen.IndexOf(sender);

            if (at < 0)
            {
                seen.Add(sender);
                at = seen.Count - 1;
            }

            indices.Add(at);
        }

        return string.Join(", ", indices);
    }

    /// <summary>Whether a group of calls is one letter at one digest, which is what makes it repeatable.</summary>
    private static bool OneSet(List<(string Continuity, string? Digest)> calls)
        => calls.Count > 0
            && calls.Select(c => c.Continuity).Distinct(StringComparer.Ordinal).Count() == 1
            && calls.Select(c => c.Digest).Distinct(StringComparer.Ordinal).Count() == 1;

    /// <summary>
    /// The same window with every prior turn's sender address replaced, or null if any turn has none.
    /// </summary>
    /// <remarks>
    /// <b>A turn that is not rewritten is a refusal, not a pass.</b> Returning the window unchanged
    /// would make "the participants did not move the answer" indistinguishable from "the participants
    /// were never moved", which is the failure this lane keeps refusing to make.
    /// </remarks>
    private static IReadOnlyList<string>? RewriteParticipants(IReadOnlyList<string> turns, string address)
    {
        var rewritten = new List<string>(turns.Count);

        foreach (var turn in turns)
        {
            var changed = RewriteSender(turn, address);

            if (ReferenceEquals(changed, turn))
            {
                return null;
            }

            rewritten.Add(changed);
        }

        return rewritten;
    }

    /// <summary>The turn with the sender address in its first line replaced.</summary>
    /// <remarks>
    /// <b>Both address forms occur in the same window.</b> One prior turn is written
    /// <c>From: Name &lt;address&gt;</c> and another is written <c>From: address</c>, so a rewrite that
    /// only understands angle brackets would silently leave half the window alone and report the
    /// participants as inert. The display name is held in both forms: what moves is the address.
    /// </remarks>
    private static string RewriteSender(string turn, string address)
    {
        var newline = turn.IndexOf('\n');
        var firstLine = newline < 0 ? turn : turn[..newline];
        var rest = newline < 0 ? string.Empty : turn[newline..];

        var open = firstLine.IndexOf('<');
        var close = firstLine.IndexOf('>');

        if (open >= 0 && close > open)
        {
            return string.Concat(firstLine[..(open + 1)], address, firstLine[close..], rest);
        }

        // The bare form: the last whitespace-delimited token of the first line, which is the address
        // because everything before it is the display name.
        var lastSpace = firstLine.LastIndexOf(' ');
        var start = lastSpace + 1;

        var end = firstLine.Length;

        return start >= end || !firstLine[start..end].Contains('@', StringComparison.Ordinal)
            ? turn
            : string.Concat(firstLine[..start], address, firstLine[end..], rest);
    }

    /// <summary>
    /// The continuity answer one arm's Host recorded, read from that run's response artifact.
    /// </summary>
    /// <remarks>
    /// "A" or "B" when the Host answered the dimension, and its availability otherwise, which is a
    /// different statement from an answer and is reported rather than mapped onto one. The value is
    /// read, never re-derived: this is the Host's own account of what it decided.
    /// </remarks>
    private static string ReadE2eContinuity(string arm)
    {
        var directory =
            Environment.GetEnvironmentVariable("CONVERSATION_E2E_RESULTS") is { Length: > 0 } configured
                ? configured
                : Path.Combine(ResultsDirectory, "e2e", "results");

        var path = Path.Combine(directory, arm + ".pass1.json");

        if (!File.Exists(path))
        {
            return "no artifact";
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        if (!document.RootElement.TryGetProperty("evidence", out var evidence)
            || evidence.ValueKind != JsonValueKind.Array)
        {
            return "no evidence";
        }

        foreach (var row in evidence.EnumerateArray())
        {
            if (!row.TryGetProperty("signalId", out var id)
                || id.GetString() != SemanticDimensions.ConversationalContinuityId)
            {
                continue;
            }

            if (!row.TryGetProperty("availability", out var availability)
                || availability.GetString() != "Available")
            {
                return availability.GetString() ?? "unknown";
            }

            return row.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble() == 1.0 ? "A" : "B"
                : "no value";
        }

        return "absent";
    }

    /// <summary>
    /// The fields that differ between two classifier inputs, named and never reproduced.
    /// </summary>
    /// <remarks>
    /// <b>Only fields the cache key is computed over.</b> The canonicaliser writes a fixed list, and a
    /// difference outside it cannot move the digest, so reporting one would send the next probe after
    /// something that cannot be the cause. Fields holding message content are compared and reported
    /// as a length and a short digest rather than a value: the question is which field differs, and
    /// answering it does not require printing anybody's mail.
    /// </remarks>
    private static List<string> DiffInputs(SemanticMailInput? captured, SemanticMailInput mine)
    {
        var differences = new List<string>();

        if (captured is null)
        {
            differences.Add("the assessor reached its classifier with no input at all");
            return differences;
        }

        // A number as text, including the null case, which is a different statement from zero and has
        // to be readable as one in the difference list.
        static string Num<T>(T? value)
            where T : struct, IFormattable =>
            value.HasValue ? value.Value.ToString(null, CultureInfo.InvariantCulture) : "<null>";

        void Compare(string path, string? a, string? b, bool content)
        {
            if (string.Equals(a, b, StringComparison.Ordinal))
            {
                return;
            }

            differences.Add(content
                ? $"{path}: {Describe(a)} vs {Describe(b)}"
                : $"{path}: '{a ?? "<null>"}' vs '{b ?? "<null>"}'");
        }

        var left = captured.Message;
        var right = mine.Message;

        Compare("envelope.tenant", left.Envelope.TenantId, right.Envelope.TenantId, content: false);
        Compare("envelope.direction", left.Envelope.Direction.ToString(), right.Envelope.Direction.ToString(), content: false);
        Compare("envelope.mailFrom", left.Envelope.MailFrom, right.Envelope.MailFrom, content: true);
        Compare("envelope.rcptTo", string.Join(" ", left.Envelope.RcptTo), string.Join(" ", right.Envelope.RcptTo), content: true);

        Compare("dimensions.count", captured.Dimensions.Count.ToString(CultureInfo.InvariantCulture), mine.Dimensions.Count.ToString(CultureInfo.InvariantCulture), content: false);

        var dimensions = Math.Min(captured.Dimensions.Count, mine.Dimensions.Count);
        for (var index = 0; index < dimensions; index++)
        {
            var a = captured.Dimensions[index];
            var b = mine.Dimensions[index];

            Compare($"dimension[{index}].id", a.Id, b.Id, content: false);
            Compare($"dimension[{index}].instructions", a.Instructions, b.Instructions, content: true);
            Compare($"dimension[{index}].criteriaTrue", a.CriteriaTrue, b.CriteriaTrue, content: true);
            Compare($"dimension[{index}].criteriaFalse", a.CriteriaFalse, b.CriteriaFalse, content: true);
        }

        Compare("subject", left.Subject, right.Subject, content: true);
        Compare("bodyText", left.BodyText, right.BodyText, content: true);
        Compare("quotedText", left.QuotedText, right.QuotedText, content: true);

        Compare("links.count", left.Links.Count.ToString(CultureInfo.InvariantCulture), right.Links.Count.ToString(CultureInfo.InvariantCulture), content: false);

        var links = Math.Min(left.Links.Count, right.Links.Count);
        for (var index = 0; index < links; index++)
        {
            Compare($"link[{index}].displayedText", left.Links[index].DisplayedText, right.Links[index].DisplayedText, content: true);
            Compare($"link[{index}].actualTarget", left.Links[index].ActualTarget, right.Links[index].ActualTarget, content: true);
            Compare($"link[{index}].unicodeHost", left.Links[index].UnicodeHost, right.Links[index].UnicodeHost, content: true);
            Compare($"link[{index}].asciiHost", left.Links[index].AsciiHost, right.Links[index].AsciiHost, content: true);
        }

        Compare("attachments.count", left.Attachments.Count.ToString(CultureInfo.InvariantCulture), right.Attachments.Count.ToString(CultureInfo.InvariantCulture), content: false);

        var attachments = Math.Min(left.Attachments.Count, right.Attachments.Count);
        for (var index = 0; index < attachments; index++)
        {
            Compare($"attachment[{index}].fileName", left.Attachments[index].FileName, right.Attachments[index].FileName, content: true);
            Compare($"attachment[{index}].declaredContentType", left.Attachments[index].DeclaredContentType, right.Attachments[index].DeclaredContentType, content: false);
            Compare($"attachment[{index}].sizeBytes", left.Attachments[index].SizeBytes.ToString(CultureInfo.InvariantCulture), right.Attachments[index].SizeBytes.ToString(CultureInfo.InvariantCulture), content: false);
            Compare($"attachment[{index}].contentHash", left.Attachments[index].ContentHash, right.Attachments[index].ContentHash, content: true);
        }

        Compare("conversationContext.count", (left.ConversationContext?.Count ?? 0).ToString(CultureInfo.InvariantCulture), (right.ConversationContext?.Count ?? 0).ToString(CultureInfo.InvariantCulture), content: false);

        var turns = Math.Min(left.ConversationContext?.Count ?? 0, right.ConversationContext?.Count ?? 0);
        for (var index = 0; index < turns; index++)
        {
            Compare($"turn[{index}]", left.ConversationContext![index], right.ConversationContext![index], content: true);
        }

        Compare("coverage.bodyParsed", left.Coverage.BodyParsed.ToString(), right.Coverage.BodyParsed.ToString(), content: false);
        Compare("coverage.htmlPresent", left.Coverage.HtmlPresent.ToString(), right.Coverage.HtmlPresent.ToString(), content: false);
        Compare("coverage.hasAttachments", left.Coverage.HasAttachments.ToString(), right.Coverage.HasAttachments.ToString(), content: false);
        Compare("coverage.htmlTextDisagreement", left.Coverage.HtmlTextDisagreement.ToString(), right.Coverage.HtmlTextDisagreement.ToString(), content: false);
        Compare("coverage.parserLimitExceeded", left.Coverage.ParserLimitExceeded.ToString(), right.Coverage.ParserLimitExceeded.ToString(), content: false);
        Compare("coverage.oversizeRejected", left.Coverage.OversizeRejected.ToString(), right.Coverage.OversizeRejected.ToString(), content: false);
        Compare("coverage.contentEncrypted", left.Coverage.ContentEncrypted.ToString(), right.Coverage.ContentEncrypted.ToString(), content: false);
        Compare("coverage.truncated", left.Coverage.Truncated.ToString(), right.Coverage.Truncated.ToString(), content: false);
        Compare("coverage.conversationContextMissing", left.Coverage.ConversationContextMissing.ToString(), right.Coverage.ConversationContextMissing.ToString(), content: false);

        Compare("authentication.provenanceIncomplete", left.Authentication.ProvenanceIncomplete.ToString(), right.Authentication.ProvenanceIncomplete.ToString(), content: false);
        Compare("authentication.authenticatedAccount", left.Authentication.AuthenticatedAccount, right.Authentication.AuthenticatedAccount, content: true);
        Compare("authentication.connectingIpPresent", (left.Authentication.ConnectingIp is not null).ToString(), (right.Authentication.ConnectingIp is not null).ToString(), content: false);

        var leftTrusted = left.Authentication.Results.Where(r => r.FromTrustedVerifier).ToList();
        var rightTrusted = right.Authentication.Results.Where(r => r.FromTrustedVerifier).ToList();

        Compare("authentication.trustedResults.count", leftTrusted.Count.ToString(CultureInfo.InvariantCulture), rightTrusted.Count.ToString(CultureInfo.InvariantCulture), content: false);

        var trusted = Math.Min(leftTrusted.Count, rightTrusted.Count);
        for (var index = 0; index < trusted; index++)
        {
            Compare($"trusted[{index}].mechanism", leftTrusted[index].Mechanism, rightTrusted[index].Mechanism, content: false);
            Compare($"trusted[{index}].result", leftTrusted[index].Result, rightTrusted[index].Result, content: false);
            Compare($"trusted[{index}].verifierId", leftTrusted[index].VerifierId, rightTrusted[index].VerifierId, content: false);
            Compare($"trusted[{index}].detail", leftTrusted[index].Detail, rightTrusted[index].Detail, content: true);
        }

        Compare("profile.present", (captured.Profile is not null).ToString(), (mine.Profile is not null).ToString(), content: false);

        if (captured.Profile is { } leftProfile && mine.Profile is { } rightProfile)
        {
            Compare("profile.direction", leftProfile.Direction.ToString(), rightProfile.Direction.ToString(), content: false);
            Compare("profile.available", leftProfile.ProfileAvailable.ToString(), rightProfile.ProfileAvailable.ToString(), content: false);
            Compare("profile.coldStart", leftProfile.ColdStart.ToString(), rightProfile.ColdStart.ToString(), content: false);
            Compare("profile.firstSeenDaysAgo", Num(leftProfile.FirstSeenDaysAgo), Num(rightProfile.FirstSeenDaysAgo), content: false);
            Compare("profile.messagesObserved", Num(leftProfile.MessagesObserved), Num(rightProfile.MessagesObserved), content: false);
            Compare("profile.trustedSamples", Num(leftProfile.TrustedSamples), Num(rightProfile.TrustedSamples), content: false);
            Compare("profile.regime", leftProfile.Regime, rightProfile.Regime, content: true);
            Compare("profile.distinctRecipientsLastHour", Num(leftProfile.DistinctRecipientsLastHour), Num(rightProfile.DistinctRecipientsLastHour), content: false);
            Compare("profile.distinctRecipientsLast30Days", Num(leftProfile.DistinctRecipientsLast30Days), Num(rightProfile.DistinctRecipientsLast30Days), content: false);
            Compare("profile.recipientDistinctnessIsFloor", leftProfile.RecipientDistinctnessIsFloor.ToString(), rightProfile.RecipientDistinctnessIsFloor.ToString(), content: false);
            Compare("profile.recipientsNovelToSender", Num(leftProfile.RecipientsNovelToSender), Num(rightProfile.RecipientsNovelToSender), content: false);
            Compare("profile.messagesLastHour", Num(leftProfile.MessagesLastHour), Num(rightProfile.MessagesLastHour), content: false);
            Compare("profile.messagesLast24Hours", Num(leftProfile.MessagesLast24Hours), Num(rightProfile.MessagesLast24Hours), content: false);
            Compare("profile.baselineMessagesPerHour", Num(leftProfile.BaselineMessagesPerHour), Num(rightProfile.BaselineMessagesPerHour), content: false);
            Compare("profile.fanoutLastHour", Num(leftProfile.FanoutLastHour), Num(rightProfile.FanoutLastHour), content: false);
            Compare("profile.baselineFanoutPerHour", Num(leftProfile.BaselineFanoutPerHour), Num(rightProfile.BaselineFanoutPerHour), content: false);
            Compare("profile.trendNarrative", leftProfile.TrendNarrative, rightProfile.TrendNarrative, content: true);
            Compare("profile.dimensionsWithSupport", Num(leftProfile.DimensionsWithSupport), Num(rightProfile.DimensionsWithSupport), content: false);
            Compare("profile.movements.count", Num(leftProfile.Movements?.Count), Num(rightProfile.Movements?.Count), content: false);
        }

        Compare("taggedContext.count", (captured.TaggedContext?.Count ?? 0).ToString(CultureInfo.InvariantCulture), (mine.TaggedContext?.Count ?? 0).ToString(CultureInfo.InvariantCulture), content: false);

        return differences;
    }

    /// <summary>A value's shape without its content: a length and a short digest.</summary>
    private static string Describe(string? value) => value is null
        ? "<null>"
        : $"{value.Length.ToString(CultureInfo.InvariantCulture)} chars, "
            + $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8]}";

    /// <summary>
    /// The cache key digest one end-to-end arm was served, read out of that run's response artifact.
    /// </summary>
    /// <remarks>
    /// Null when the artifact is absent, which is reported rather than guessed at. The digest is not
    /// re-computed here: this lane can only read the value the Host served, which is the value the
    /// comparison is against.
    /// </remarks>
    private static string? ReadE2eDigest(string arm)
    {
        // The directory the targets live in is overridable, because an arm can be re-taken into a
        // different one. A comparison whose target was overwritten by the run that checked it is not
        // re-checkable by anyone, and the earlier run's figures are still the ones on record.
        var directory =
            Environment.GetEnvironmentVariable("CONVERSATION_E2E_RESULTS") is { Length: > 0 } configured
                ? configured
                : Path.Combine(ResultsDirectory, "e2e", "results");

        var path = Path.Combine(directory, arm + ".pass1.json");

        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement.TryGetProperty("cache", out var cache)
            && cache.TryGetProperty("keyDigest", out var digest)
                ? digest.GetString()
                : null;
    }

    private static async Task AxisProse(Runner runner)
    {
        const int repeats = 3;

        var window = Threads.All[0].PriorTurns;
        var pair = Threads.All[2];

        // Short and restating: the committed fixture's reply with its quote block removed. This is
        // M6's A2, carried forward so this sweep is calibrated against the previous one.
        const string ShortRestating = "Thanks for the update. Two working days is fine.";

        // Short and advancing: the same length class, reporting a fact the window does not contain.
        const string ShortAdvancing = "Thanks, the parcel arrived this morning.";

        // Long and restating: every clause is either in the window or a paraphrase of it. Nothing here
        // is a fact the window lacks.
        const string LongRestating =
            "Thanks for the update. Two working days is fine, and I will wait for the tracking "
            + "reference that follows once the carrier scans it. Both of those are noted, and nothing "
            + "in the message needs a reply from me.";

        // Long and advancing: this lane's own B body, which is where the question came from.
        const string LongAdvancing =
            "Thanks, the tracking came through and the parcel arrived this morning. "
            + "Everything is in order, so nothing further is needed from your side.";

        // The guard rows. Unrelated to the thread in content, at both lengths, so a dimension that
        // answers A to anything short is caught rather than read as a restatement effect.
        const string UnrelatedShort = "Please rotate the API credentials before the end of the quarter.";

        const string UnrelatedLong =
            "Please rotate the API credentials before the end of the quarter, and confirm the new "
            + "values in the shared vault. The old ones were used during the migration window, so "
            + "they should be considered exposed and retired rather than reused.";

        Console.WriteLine($"== M6b: length against restatement, {repeats} repeats per cell ==");
        Console.WriteLine(
            "Every condition goes through Threads.Input with the same three prior turns, so the "
            + "envelope and the window are byte-identical and the body is the only variable. "
            + "A2 from M6 is carried here as the short-restating cell.");
        Console.WriteLine();

        var conditions = new (string Name, string Cell, string Body)[]
        {
            ("R1-short-restating", "short, restates the window", ShortRestating),
            ("R2-short-advancing", "short, advances past the window", ShortAdvancing),
            ("R3-long-restating", "long, restates the window", LongRestating),
            ("R4-long-advancing", "long, advances past the window (M6's B body)", LongAdvancing),
            ("G1-unrelated-short", "guard: unrelated, short", UnrelatedShort),
            ("G2-unrelated-long", "guard: unrelated, long", UnrelatedLong),
        };

        var answers = new Dictionary<string, List<CallResult>>(StringComparer.Ordinal);
        var done = 0;

        foreach (var round in Enumerable.Range(0, repeats))
        {
            foreach (var (name, _, body) in conditions)
            {
                var result = await runner.CallAsync(
                    Threads.Input(pair, window, body), CancellationToken.None);

                if (!answers.TryGetValue(name, out var list))
                {
                    answers[name] = list = [];
                }

                list.Add(result);
                done++;

                Console.WriteLine(
                    $"  [{done,2}/{conditions.Length * repeats}] r{round + 1} {name,-20} "
                    + $"chars={body.Length,-5} tokens={result.PromptTokens,-6} "
                    + $"continuity={result.Continuity,-16} answered={result.Answered}/{result.Asked} "
                    + $"{result.ElapsedMs} ms");
                Console.Out.Flush();
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{"condition",-22} {"cell",-38} {"chars",-6} {"continuity",-18} tokens");
        Console.WriteLine(new string('-', 112));

        var rows = new List<object>();

        foreach (var (name, cell, body) in conditions)
        {
            var set = answers[name];
            var distribution = set
                .GroupBy(a => a.Continuity)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            Console.WriteLine(
                $"{name,-22} {cell,-38} {body.Length,-6} {Format(distribution),-18} "
                + $"{string.Join("/", set.Select(a => Show(a.PromptTokens)))}");

            rows.Add(new
            {
                condition = name,
                cell,
                body_characters = body.Length,
                continuity_distribution = distribution,
                prompt_tokens = set.Select(a => a.PromptTokens).ToList(),
                codes = set.Select(a => a.Codes).ToList(),
                elapsed_ms = set.Select(a => a.ElapsedMs).ToList(),
            });
        }

        Write("m6b-axis-prose", new
        {
            repeats,
            window_turns = window.Count,
            design = "2x2 over {short, long} x {restates the window, advances past it}, plus two guard rows that are unrelated at both lengths",
            cells = rows,
        });
        Console.WriteLine();
    }

    /// <summary>
    /// The committed reply-in-thread fixture's body, and the pieces of it the axis sweep varies.
    /// </summary>
    /// <remarks>
    /// Read from the fixture rather than transcribed into a constant. A hand-typed copy of a body is
    /// a second source of truth, and the first time the fixture changes the sweep would be measuring
    /// a message nobody ships while claiming to measure the committed one.
    /// </remarks>
    private static (string Body, string Bare, string Attribution, string Quoted) JevReplyParts()
    {
        var text = File.ReadAllText(Path.Combine(Corpus.FixturesDirectory, "reply-in-thread.eml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var split = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (split < 0)
        {
            throw new InvalidOperationException(
                "The reply-in-thread fixture has no header/body separator, so its body cannot be read.");
        }

        var body = text[(split + 2)..].TrimEnd('\n');

        // The quote block is the paragraph that opens with the attribution marker. Splitting on the
        // paragraph rather than on a line offset keeps this correct if the reply above it reflows.
        var paragraphs = body.Split("\n\n", StringSplitOptions.None);
        var quotedParagraph = paragraphs.FirstOrDefault(p => p.StartsWith("On ", StringComparison.Ordinal));

        if (quotedParagraph is null)
        {
            throw new InvalidOperationException(
                "The reply-in-thread fixture has no 'On ... wrote:' quote block, so the axis sweep has "
                + "nothing to vary. If the fixture changed, the axis has to be re-derived rather than "
                + "this check relaxed.");
        }

        var lines = quotedParagraph.Split('\n');
        var attribution = lines[0];
        var quoted = string.Join("\n", lines.Skip(1));
        var bare = body.Replace("\n\n" + quotedParagraph, string.Empty, StringComparison.Ordinal).TrimEnd();

        return (body, bare, attribution, quoted);
    }

    private static string Show(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    /// <summary>
    /// Ollama's counters for the last response, in milliseconds, plus its done reason.
    /// </summary>
    /// <remarks>
    /// <b>The done reason is read because a truncated prompt reports "stop".</b> If the split is ever
    /// taken on a prompt at the window ceiling, that field is the only sign the server cut it.
    /// </remarks>
    private static (long? PromptN, long? PrefillMs, long? EvalN, long? GenMs, long? LoadMs, long? TotalMs, string Done)
        ReadCounters(string? body)
    {
        static long? Number(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number)
                ? number
                : null;

        static long? Milliseconds(JsonElement element, string name)
            => Number(element, name) is { } nanoseconds ? nanoseconds / 1_000_000 : null;

        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null, null, null, null, null, "no-body");
        }

        // stream=false returns one object; a stream returns one object per line. The last complete
        // line carries the counters either way.
        var text = body.TrimEnd();
        var newline = text.LastIndexOf('\n');

        if (newline >= 0)
        {
            text = text[(newline + 1)..].Trim();
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            var done = root.TryGetProperty("done_reason", out var reason) && reason.ValueKind == JsonValueKind.String
                ? reason.GetString() ?? "unknown"
                : "unknown";

            return (
                Number(root, "prompt_eval_count"),
                Milliseconds(root, "prompt_eval_duration"),
                Number(root, "eval_count"),
                Milliseconds(root, "eval_duration"),
                Milliseconds(root, "load_duration"),
                Milliseconds(root, "total_duration"),
                done);
        }
        catch (JsonException)
        {
            return (null, null, null, null, null, null, "unparseable");
        }
    }

    // ---------------------------------------------------------------- plumbing

    private static string Format(Dictionary<string, int> distribution)
        => string.Join(",", distribution.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} x{p.Value}"));

    private static void Write(string name, object payload)
    {
        var path = Path.Combine(ResultsDirectory, name + ".json");
        var document = new
        {
            measured_at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            tool = "tools/conversation/measure",
            payload,
        };

        // No byte-order mark: an artifact is meant to be read by other tools, and a BOM makes
        // strict JSON readers reject the file rather than parse it.
        File.WriteAllText(path, JsonSerializer.Serialize(document, Indented), new UTF8Encoding(false));
        Console.WriteLine($"wrote {Path.GetRelativePath(RepoRoot, path)}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StyloMail.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root by walking up from '{AppContext.BaseDirectory}'.");
    }

}
