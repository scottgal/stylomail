using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Core;
using StyloMail.Host.Decisions;
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

    private static string RemoveMembers(string payload, params string[] members)
    {
        var document = JsonNode.Parse(payload)!.AsObject();
        foreach (var member in members)
        {
            document.Remove(member);
        }

        return document.ToJsonString();
    }
}
