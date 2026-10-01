using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Core;
using StyloMail.Host.Decisions;
using StyloMail.Host.Endpoints;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Tests;

/// <summary>
/// Reading a decision that an earlier build wrote.
/// </summary>
/// <remarks>
/// The ledger stores the whole assessment as a JSON document on purpose, so that adding a field to
/// the contract does not become a migration. That trade has a failure mode the trade itself hides:
/// members declared <c>required</c> are enforced by System.Text.Json on <em>deserialisation</em>, so
/// a row written before the member existed cannot be read back at all. The build that added the
/// member, and every build after it, sees an unreadable ledger and no test notices, because every
/// test writes and reads within one build where the member is always present.
/// </remarks>
public sealed class LedgerLegacyRowTests
{
    [Fact]
    public async Task A_decision_written_before_delivery_timing_existed_is_still_readable()
    {
        using var host = new TestHost();
        var assessmentId = await AssessAsync(host);

        // Rewrite the stored row into the shape an older build produced by removing the member,
        // rather than by pasting a whole hand-written document. A hand-written fixture would stop
        // testing this the moment the assessment contract gained another required field, and it
        // would fail for the wrong reason.
        var stored = ReadStoredPayload(host, assessmentId);
        var legacy = RemoveMembers(stored, "deliveryTiming");

        // Guards the test against going vacuous: if the member is ever renamed, the removal above
        // silently stops removing anything and this test would pass while proving nothing.
        Assert.NotEqual(stored, legacy);
        RewritePayload(host, assessmentId, legacy);

        var ledger = host.Services.GetRequiredService<IDecisionLedger>();
        var read = await ledger.FindAsync(
            TestPrincipals.AcmeTenant, assessmentId, CancellationToken.None);

        Assert.NotNull(read);

        // PreAcceptance is what an email assessment was. MailAssessor is the only production
        // construction site and it is the delivery path, so every row already on disk was made
        // before the recipient could be reached. This restores what the row was rather than
        // guessing a default for it.
        Assert.Equal(DeliveryTiming.PreAcceptance, read.DeliveryTiming);
    }

    [Fact]
    public async Task A_decision_written_by_this_build_is_read_back_unchanged()
    {
        // The other half of the contract: tolerating the older shape must not alter the newer one.
        using var host = new TestHost();
        var assessmentId = await AssessAsync(host);

        var ledger = host.Services.GetRequiredService<IDecisionLedger>();
        var read = await ledger.FindAsync(
            TestPrincipals.AcmeTenant, assessmentId, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(assessmentId, read.AssessmentId);
        Assert.Equal(DeliveryTiming.PreAcceptance, read.DeliveryTiming);
        Assert.Equal(TestPrincipals.AcmeTenant, read.TenantId);
    }

    [Fact]
    public async Task A_legacy_decision_is_readable_through_the_console_endpoint()
    {
        // The read path a reviewer actually uses. A converter that only satisfied the ledger's own
        // unit surface would leave the surface this exists for still failing.
        //
        // This asserts the decision is served, not the shape of what is served. The response
        // contract in DecisionResponse does not carry deliveryTiming at all, which is a gap in that
        // contract rather than a defect in this read path, and asserting it here would fail for a
        // reason that has nothing to do with reading an older row.
        using var host = new TestHost();
        var assessmentId = await AssessAsync(host);

        var stored = ReadStoredPayload(host, assessmentId);
        RewritePayload(host, assessmentId, RemoveMembers(stored, "deliveryTiming"));

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var response = await reviewer.GetAsync($"/v1/decisions/{assessmentId}");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(assessmentId, body.RootElement.GetProperty("assessmentId").GetString());
    }

    private static async Task<string> AssessAsync(TestHost host)
    {
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);
        var response = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("assessmentId").GetString()!;
    }

    // ---------------------------------------------------------------------------------------------
    // The chat route
    // ---------------------------------------------------------------------------------------------

    private const string SlackSecret = "test-signing-secret-not-a-real-one";
    private const string OurBotId = "B0OWN";
    private const string WatchedChannel = "C01";

    /// <summary>How long to wait for the drain, which runs off the request path.</summary>
    private static readonly TimeSpan ChatAssessmentTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// A host whose chat intake is wired end to end, so a posted event becomes a recorded decision.
    /// </summary>
    private static TestHost ChatHost() =>
        new TestHost().WithChatIntake(SlackSecret, OurBotId, WatchedChannel);

    /// <summary>
    /// Two link-free turns with different text, so the second is not settled as a near-duplicate of
    /// the first and both are assessed.
    /// </summary>
    private static readonly string ChatTurnA = Turn("Ev-chat-a", "morning, are we still on for tuesday");

    private static readonly string ChatTurnB = Turn("Ev-chat-b", "thanks, that works for me");

    private static string Turn(string eventId, string text) => $$$"""
        {"type":"event_callback","event_id":"{{{eventId}}}","event_time":1760000000,
         "team_id":"T01",
         "event":{"type":"message","channel":"{{{WatchedChannel}}}","user":"U01","text":"{{{text}}}",
                  "ts":"1760000000.000100"}}
        """;

    private static string Stamp(TestHost host) =>
        host.Services.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeSeconds().ToString();

    private static string Sign(string timestamp, string body)
    {
        var mac = new HMACSHA256(Encoding.UTF8.GetBytes(SlackSecret)).ComputeHash(
            Encoding.UTF8.GetBytes($"v0:{timestamp}:{body}"));

        return $"v0={Convert.ToHexString(mac).ToLowerInvariant()}";
    }

    private static HttpRequestMessage SignedSlack(TestHost host, string body)
    {
        var timestamp = Stamp(host);
        var request = new HttpRequestMessage(HttpMethod.Post, SlackEventsEndpoints.Route)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.Add(SlackEventsEndpoints.TimestampHeader, timestamp);
        request.Headers.Add(SlackEventsEndpoints.SignatureHeader, Sign(timestamp, body));
        return request;
    }

    /// <summary>
    /// Posts a signed chat turn and returns the assessment id the drain wrote for it.
    /// </summary>
    /// <remarks>
    /// <b>Polling, because the endpoint's answer is about the intake rather than the assessment.</b>
    /// The Slack surface acknowledges once the event is stored so the platform does not retry, and the
    /// drain assesses it afterwards off the request path. A test that read the ledger the moment the
    /// endpoint answered would be asserting something the endpoint never promised. Ids already seen
    /// are excluded rather than assumed absent, so the second turn cannot be satisfied by the first
    /// turn's row.
    /// </remarks>
    private static async Task<string> AssessChatAsync(
        TestHost host,
        string body,
        params string[] alreadyKnown)
    {
        using (var client = host.Anonymous())
        {
            using var response = await client.SendAsync(SignedSlack(host, body));
            response.EnsureSuccessStatusCode();
        }

        var deadline = DateTimeOffset.UtcNow + ChatAssessmentTimeout;
        var known = alreadyKnown.ToHashSet(StringComparer.Ordinal);

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
            using var listing = JsonDocument.Parse(await reviewer.GetStringAsync("/v1/decisions"));

            var fresh = listing.RootElement.GetProperty("decisions").EnumerateArray()
                .Select(item => item.GetProperty("assessmentId").GetString()!)
                .FirstOrDefault(id => !known.Contains(id));

            if (fresh is not null)
            {
                return fresh;
            }

            await Task.Delay(PollInterval);
        }

        throw new InvalidOperationException(
            "The chat intake never produced a decision: the drain assessed no event that the listing "
            + $"did not already carry, within {ChatAssessmentTimeout.TotalSeconds:0} seconds.");
    }

    private static string ReadStoredPayload(TestHost host, string assessmentId)
    {
        var database = host.Services.GetRequiredService<HostDatabase>();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT payload FROM host_decision_ledger WHERE tenant_id = $tenant AND assessment_id = $id;";
        command.Parameters.AddWithValue("$tenant", TestPrincipals.AcmeTenant);
        command.Parameters.AddWithValue("$id", assessmentId);

        return (string)command.ExecuteScalar()!;
    }

    private static void RewritePayload(TestHost host, string assessmentId, string payload)
    {
        var database = host.Services.GetRequiredService<HostDatabase>();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE host_decision_ledger SET payload = $payload WHERE tenant_id = $tenant AND assessment_id = $id;";
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$tenant", TestPrincipals.AcmeTenant);
        command.Parameters.AddWithValue("$id", assessmentId);

        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task A_decision_written_before_the_channel_existed_is_still_readable()
    {
        // The second member to become required after rows already existed, which is why the converter
        // is written as a list of back-fills rather than as one special case for the first.
        using var host = new TestHost();
        var assessmentId = await AssessAsync(host);

        var stored = ReadStoredPayload(host, assessmentId);
        var legacy = RemoveMembers(stored, "deliveryTiming", "channel");
        Assert.NotEqual(stored, legacy);
        RewritePayload(host, assessmentId, legacy);

        var ledger = host.Services.GetRequiredService<IDecisionLedger>();
        var read = await ledger.FindAsync(
            TestPrincipals.AcmeTenant, assessmentId, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(DeliveryTiming.PreAcceptance, read.DeliveryTiming);

        // Email is what those rows were, for the same reason PreAcceptance is: MailAssessor is the
        // only production construction site and every row on disk predates chat.
        Assert.Equal(ChannelKind.Email, read.Channel.Kind);
        Assert.Null(read.Channel.WorkspaceId);
        Assert.Null(read.Channel.ChannelId);
        Assert.Null(read.Channel.ThreadId);
    }

    [Fact]
    public async Task The_console_view_reports_the_channel_and_the_delivery_timing()
    {
        // The design says the console shows deliveryTiming on every chat decision, so an operator can
        // never read a post-hoc hold as a prevention. That sentence is unsatisfiable while the
        // response omits the member, and it was unsatisfiable on every decision, chat or email.
        using var host = new TestHost();
        var assessmentId = await AssessAsync(host);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var response = await reviewer.GetAsync($"/v1/decisions/{assessmentId}");
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(
            nameof(DeliveryTiming.PreAcceptance),
            body.RootElement.GetProperty("deliveryTiming").GetString());

        // Which channel is beside whether we could have stopped it, because either one alone tells a
        // reader less than they need: a post-hoc hold on email would be a contradiction.
        Assert.Equal(
            nameof(ChannelKind.Email),
            body.RootElement.GetProperty("channel").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_decision_written_before_the_index_arithmetic_existed_is_still_readable()
    {
        // The real assessor, so the row actually carries dimensions. A fake that returns none would
        // leave the nested members out of the document entirely and the rewrite below would edit
        // nothing, which is the vacuous-pass shape this file's Assert.NotEqual guards against.
        using var host = new TestHost().WithRealAssessor();
        var assessmentId = await AssessAsync(host);

        var stored = ReadStoredPayload(host, assessmentId);
        var legacy = RemoveDimensionMembers(
            RemoveMembers(stored, "riskIndexDenominator"),
            "weight", "counted", "exclusionReason");

        Assert.NotEqual(stored, legacy);
        RewritePayload(host, assessmentId, legacy);

        var ledger = host.Services.GetRequiredService<IDecisionLedger>();
        var read = await ledger.FindAsync(
            TestPrincipals.AcmeTenant, assessmentId, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(assessmentId, read.AssessmentId);

        // Null, and neither of the two things that would make this row look informative. Not zero: the
        // weight is policy configuration that was never stored beside the decision, so a zero would be
        // a plausible number served on a row that did carry weight. Not derived either: the counted
        // flag is left null with the weight rather than half-filled, because half a pair invites a
        // reader to guess the other half.
        Assert.Null(read.RiskIndexDenominator);
        Assert.All(
            read.RiskDimensions,
            dimension =>
            {
                Assert.Null(dimension.Weight);
                Assert.Null(dimension.Counted);
            });
    }

    [Fact]
    public async Task A_decision_written_before_the_covered_fraction_existed_still_reads_its_index()
    {
        // The sibling above proves the pre-37 shape is readable. THIS is the shape that only exists
        // because of it: a row written after decision 37 and before this member, so it carries the
        // index arithmetic and does not carry the fraction. It is the only class that exercises the
        // fraction's own StateUnrecorded line, and it is the class every build since decision 37
        // writes, so a landing that got that line wrong would leave every such row unreadable while
        // the sibling above stayed green: required members are enforced on presence, and the sibling's
        // fixture removes riskIndexDenominator, so it never reaches a row with one of the two
        // nullables present and the other absent.
        using var host = new TestHost().WithRealAssessor();
        var assessmentId = await AssessAsync(host);

        var stored = ReadStoredPayload(host, assessmentId);

        // The population control, before the removal rather than after: if the fraction the real
        // assessor just recorded were already null, removing it and reading null back would prove
        // nothing about the back-fill. Measured as a NUMBER so the removal below is known to have
        // removed a measured value.
        using (var document = JsonDocument.Parse(stored))
        {
            Assert.Equal(
                JsonValueKind.Number,
                document.RootElement.GetProperty("coveredWeightFraction").ValueKind);
        }

        var legacy = RemoveMembers(stored, "coveredWeightFraction");

        // And the removal landed, which is the same guard the sibling keeps: a renamed member would
        // otherwise leave this test passing while editing nothing.
        Assert.NotEqual(stored, legacy);
        RewritePayload(host, assessmentId, legacy);

        var ledger = host.Services.GetRequiredService<IDecisionLedger>();
        var read = await ledger.FindAsync(
            TestPrincipals.AcmeTenant, assessmentId, CancellationToken.None);

        Assert.NotNull(read);

        // Null, and not the zero that would serve as a plausible measurement on a row that did carry
        // covered weight: the fraction is arithmetic over weights that were never stored beside the
        // decision, so the row is told the build did not record it rather than handed a number.
        Assert.Null(read.CoveredWeightFraction);

        // The sibling SURVIVES, which is what makes this the new class rather than a repeat of the
        // test above: this row recorded its index arithmetic and lacked only the fraction on top of it.
        Assert.NotNull(read.RiskIndexDenominator);

        // And the same on the wire, through the whole read path rather than the ledger alone: the
        // member is PRESENT and null, which is a different document from one that omits it, and the
        // number beside it is what shows the row is otherwise intact.
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        using var served = JsonDocument.Parse(
            await reviewer.GetStringAsync($"/v1/decisions/{assessmentId}"));

        Assert.Equal(
            JsonValueKind.Null,
            served.RootElement.GetProperty("coveredWeightFraction").ValueKind);
        Assert.Equal(
            JsonValueKind.Number,
            served.RootElement.GetProperty("riskIndexDenominator").ValueKind);
    }

    [Fact]
    public async Task A_row_that_never_recorded_its_arithmetic_serves_null_while_a_chat_row_serves_zero()
    {
        // The distinction the nullable shape exists to preserve, made from two rows that are otherwise
        // the same decision: two link-free chat turns, so neither poses a deterministic question and
        // neither has a semantic answer. One was written by this build and recorded the empty
        // arithmetic as a denominator of 0.0; the other is rewritten into the pre-37 shape, where the
        // arithmetic was never written down at all. If a later reader merged the two, the second would
        // read as a measurement it is not.
        //
        // CHAT RATHER THAN EMAIL, and the reason is the whole point of the fixture. An email always
        // poses at least one deterministic question, because the envelope identity and the padding
        // findings are published on every message the MIME analyser sees whatever the message
        // contains, so the smallest denominator an email can serve is their weight and never zero.
        // Asserting "an empty one serves zero" on the email route would assert something false. A chat
        // turn has no such floor: the findings the chat producer emits are all conditional on the
        // message carrying a link, so a link-free turn empties the question set and the numerator and
        // the denominator both come out zero. The distinction is only assertable on a payload that can
        // actually be empty, and this is the route where one can.
        using var host = ChatHost();
        var measuredId = await AssessChatAsync(host, ChatTurnA);
        var legacyId = await AssessChatAsync(host, ChatTurnB, measuredId);

        var stored = ReadStoredPayload(host, legacyId);
        var legacy = RemoveDimensionMembers(
            RemoveMembers(stored, "riskIndexDenominator"),
            "weight", "counted", "exclusionReason");

        Assert.NotEqual(stored, legacy);
        RewritePayload(host, legacyId, legacy);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        using var measuredBody = JsonDocument.Parse(
            await reviewer.GetStringAsync($"/v1/decisions/{measuredId}"));
        using var legacyBody = JsonDocument.Parse(
            await reviewer.GetStringAsync($"/v1/decisions/{legacyId}"));

        var rows = measuredBody.RootElement.GetProperty("riskDimensions").EnumerateArray().ToList();

        // The row is the one this fixture meant to make. Asserted rather than left to the comment,
        // because an email row here would carry a floor under its denominator and the zero below
        // would then be a claim about a surface the fixture never reached.
        Assert.Equal(
            nameof(ChannelKind.Slack),
            measuredBody.RootElement.GetProperty("channel").GetProperty("kind").GetString());

        // Nothing was counted, which is what makes the zero below a measurement rather than a gap.
        Assert.DoesNotContain(rows, row => row.GetProperty("counted").GetBoolean());

        // THE ATTRIBUTION, without which the zero is unattributable. A denominator of 0.0 can come
        // from a shape that asked nothing as easily as from a provider that answered nothing, and
        // those are different rows: one is a message with no questions to pose, the other is a
        // deployment that lost its whole semantic layer. Every semantic dimension is present here, and
        // every one of them says the provider did not answer rather than that the question did not
        // apply. Asserted on the presence and the state together, because a blackout and an empty
        // shape are indistinguishable from the denominator alone.
        var semantic = rows
            .Where(row => row.GetProperty("name").GetString()!
                .StartsWith("semantic.", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(SemanticDimensions.All.Count, semantic.Count);
        Assert.All(
            semantic,
            row => Assert.Equal(
                nameof(EvidenceAvailability.Unavailable),
                row.GetProperty("availability").GetString()));

        // The measurement: a number, and the empty arithmetic this composition produces (nothing was
        // available to count, so there was no index to divide).
        Assert.Equal(
            JsonValueKind.Number,
            measuredBody.RootElement.GetProperty("riskIndexDenominator").ValueKind);
        Assert.Equal(
            0.0,
            measuredBody.RootElement.GetProperty("riskIndexDenominator").GetDouble());

        // The admission: the member is present and null, not absent and not zero.
        Assert.Equal(
            JsonValueKind.Null,
            legacyBody.RootElement.GetProperty("riskIndexDenominator").ValueKind);

        // And the same on the rows, where a null counted flag must not be readable as false.
        Assert.All(
            legacyBody.RootElement.GetProperty("riskDimensions").EnumerateArray(),
            row =>
            {
                Assert.Equal(JsonValueKind.Null, row.GetProperty("weight").ValueKind);
                Assert.Equal(JsonValueKind.Null, row.GetProperty("counted").ValueKind);
            });

        // The listing serves the legacy row rather than dropping it, which is the half of this that a
        // read-by-id cannot show: before the ruling this row could not be deserialised and the page
        // skipped it.
        using var listing = JsonDocument.Parse(await reviewer.GetStringAsync("/v1/decisions"));
        var listed = listing.RootElement.GetProperty("decisions").EnumerateArray()
            .Select(item => item.GetProperty("assessmentId").GetString())
            .ToList();

        Assert.Contains(legacyId, listed);
        Assert.Equal(0, listing.RootElement.GetProperty("skippedCount").GetInt32());
    }

    [Fact]
    public async Task A_row_measured_with_the_semantic_provider_down_still_counts_the_deterministic_findings()
    {
        // Decision 42 gave the risk-shaped MIME findings declared weights, and the MIME analysis runs
        // whether or not the semantic provider answers. So "the semantic provider was unreachable"
        // stopped meaning "nothing was counted", which is what took the sibling above red: the same
        // composition that once served a denominator of 0.0 now serves one built from the findings the
        // message itself poses.
        //
        // The point is that the serving no longer reports a purely semantic quantity. With the
        // provider down no semantic dimension is answerable, so every counted row here is a
        // deterministic one and the denominator is the sum of their weights. Asserted relationally
        // rather than against a literal, because the literal is a property of DimensionWeights while
        // this test is about what the route serves.
        using var host = new TestHost().WithRealAssessor();
        var assessmentId = await AssessAsync(host);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        using var body = JsonDocument.Parse(
            await reviewer.GetStringAsync($"/v1/decisions/{assessmentId}"));

        var rows = body.RootElement.GetProperty("riskDimensions").EnumerateArray().ToList();
        var counted = rows.Where(row => row.GetProperty("counted").GetBoolean()).ToList();

        // Guarded rather than assumed: a semantic row that became answerable here would mean this test
        // is measuring something else, and it should fail rather than silently widen.
        Assert.NotEmpty(counted);
        Assert.All(
            counted,
            row => Assert.StartsWith("deterministic.", row.GetProperty("name").GetString()!));

        Assert.Equal(
            counted.Sum(row => row.GetProperty("weight").GetDouble()),
            body.RootElement.GetProperty("riskIndexDenominator").GetDouble());
    }

    [Fact]
    public async Task One_unreadable_row_does_not_take_the_listing_down_with_it()
    {
        // The listing deserialises every row on the page, so a single payload the build cannot read
        // would otherwise turn a page of decisions into no page at all, taking the rows that are
        // perfectly readable down with it. The row here is truncated rather than merely old: a shape
        // this build cannot parse at all is the case that stays unreadable whatever the contract
        // decides about rows written by an earlier build.
        using var host = new TestHost();
        var readableId = await AssessAsync(host);
        var unreadableId = await AssessAsync(host);

        var stored = ReadStoredPayload(host, unreadableId);
        var truncated = stored[..(stored.Length / 2)];
        Assert.NotEqual(stored, truncated);
        RewritePayload(host, unreadableId, truncated);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var response = await reviewer.GetAsync("/v1/decisions");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var listed = body.RootElement.GetProperty("decisions").EnumerateArray()
            .Select(item => item.GetProperty("assessmentId").GetString())
            .ToList();

        // The readable row is still served, and the unreadable one is absent rather than fatal.
        Assert.Contains(readableId, listed);
        Assert.DoesNotContain(unreadableId, listed);

        // And the skip is stated on the page rather than only logged. A page that dropped the row
        // silently would be a page reporting itself complete when it is not, which is the failure this
        // count exists to make unrepresentable.
        Assert.Equal(1, body.RootElement.GetProperty("skippedCount").GetInt32());
    }

    private static string RemoveMembers(string payload, params string[] members)
    {
        var document = JsonNode.Parse(payload)!.AsObject();
        foreach (var member in members)
        {
            document.Remove(member);
        }

        return document.ToJsonString();
    }

    /// <summary>
    /// Removes members from every entry of the row's dimension list.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RemoveMembers"/> because the required members a row can predate are
    /// not all top-level: the index arithmetic travels inside each dimension, and a helper that only
    /// reached the outer object would leave those entries intact and the test passing while proving
    /// nothing.
    /// </remarks>
    private static string RemoveDimensionMembers(string payload, params string[] members)
    {
        var document = JsonNode.Parse(payload)!.AsObject();
        var dimensions = document["riskDimensions"]?.AsArray()
            ?? throw new InvalidOperationException("The stored row carries no riskDimensions to edit.");

        foreach (var dimension in dimensions)
        {
            foreach (var member in members)
            {
                dimension!.AsObject().Remove(member);
            }
        }

        return document.ToJsonString();
    }
}
