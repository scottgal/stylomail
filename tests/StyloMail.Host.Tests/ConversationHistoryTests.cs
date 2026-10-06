using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StyloMail.Core;
using StyloMail.Host.Contracts;
using StyloMail.Host.Decisions;
using StyloMail.Host.Serialization;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Tests;

public sealed class ConversationHistoryTests
{
    [Fact]
    public async Task History_requires_review_and_a_workspace_and_channel()
    {
        using var host = new TestHost();
        using var anonymous = host.Anonymous();
        using var sender = host.ClientAs(TestPrincipals.AcmeSenderKey);
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/v1/conversations/history?workspaceId=W1&channelId=C1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await sender.GetAsync("/v1/conversations/history?workspaceId=W1&channelId=C1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await reviewer.GetAsync("/v1/conversations/history?channelId=C1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await reviewer.GetAsync("/v1/conversations/history?workspaceId=W1")).StatusCode);
    }

    [Fact]
    public async Task History_is_tenant_scoped_filtered_and_pages_equal_timestamps_without_skips()
    {
        using var host = new TestHost();
        var at = new DateTimeOffset(2026, 10, 6, 14, 0, 0, TimeSpan.Zero);
        var slack = new ChannelContext
        {
            Kind = ChannelKind.Slack,
            WorkspaceId = "W1",
            ChannelId = "C1",
            ThreadId = "T1",
        };

        await RecordAsync(host, Assessment("acme", "asm_0002", at, slack));
        await RecordAsync(host, Assessment("acme", "asm_0003", at, slack));
        await RecordAsync(host, Assessment("acme", "asm_0004", at, slack with { ThreadId = "T2" }));
        await RecordAsync(host, Assessment("acme", "asm_0005", at, slack with { ChannelId = "C2" }));
        await RecordAsync(host, Assessment("globex", "asm_0006", at, slack));

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var first = await HistoryAsync(reviewer, threadId: "T1", limit: 1);
        Assert.Equal(TestPrincipals.AcmeTenant, first.TenantId);
        Assert.True(first.HasMore);
        Assert.Equal("asm_0003", Assert.Single(first.Decisions).AssessmentId);

        var bounded = await HistoryAsync(
            reviewer,
            threadId: "T1",
            limit: 10,
            from: at,
            to: at.AddSeconds(1));
        Assert.Equal(2, bounded.Decisions.Count);

        var exclusiveUpperBound = await HistoryAsync(
            reviewer,
            threadId: "T1",
            limit: 10,
            to: at);
        Assert.Empty(exclusiveUpperBound.Decisions);

        var second = await HistoryAsync(reviewer, threadId: "T1", limit: 1, after: first.NextCursor);
        Assert.False(second.HasMore);
        Assert.Equal("asm_0002", Assert.Single(second.Decisions).AssessmentId);
        Assert.Equal(first.Decisions[0].AssessedAt, second.Decisions[0].AssessedAt);

        // A cursor is tied to its tenant and canonical filter shape. Reusing it for another thread
        // is refused instead of silently shifting the page and losing matching decisions.
        var changedFilter = await reviewer.GetAsync(
            $"/v1/conversations/history?workspaceId=W1&channelId=C1&threadId=T2&limit=1&after={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, changedFilter.StatusCode);
        using var error = JsonDocument.Parse(await changedFilter.Content.ReadAsStringAsync());
        Assert.Equal("invalid_cursor", error.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Email_decisions_are_not_returned_as_conversation_history()
    {
        using var host = new TestHost();
        using var sender = host.ClientAs(TestPrincipals.AcmeSenderKey);
        var assessmentResponse = await sender.PostAsync(
            "/v1/assessments",
            JsonContent.Create(TestMessages.Request()));
        Assert.Equal(HttpStatusCode.OK, assessmentResponse.StatusCode);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var history = await HistoryAsync(reviewer, threadId: null, limit: 10);
        Assert.Empty(history.Decisions);
    }

    [Fact]
    public async Task Legacy_offsets_are_normalized_for_bounds_order_and_equal_instant_paging()
    {
        using var host = new TestHost();
        var path = Path.Combine(host.Root, "host.db");
        var channel = new ChannelContext
        {
            Kind = ChannelKind.Slack,
            WorkspaceId = "W1",
            ChannelId = "C1",
            ThreadId = "T1",
        };
        var earlierEqualInstant = new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.FromHours(2));
        var later = new DateTimeOffset(2026, 10, 6, 8, 30, 0, TimeSpan.Zero);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE host_decision_ledger (tenant_id TEXT NOT NULL, assessment_id TEXT NOT NULL, "
                + "internal_message_id TEXT NOT NULL, action TEXT NOT NULL, recorded_at TEXT NOT NULL, "
                + "payload TEXT NOT NULL, PRIMARY KEY (tenant_id, assessment_id));";
            command.ExecuteNonQuery();
            InsertLegacy(connection, Assessment("acme", "asm_0001", earlierEqualInstant, channel),
                JsonSerializer.Serialize(Assessment("acme", "asm_0001", earlierEqualInstant, channel), HostJson.Options));
            InsertLegacy(connection, Assessment("acme", "asm_0002", later, channel),
                JsonSerializer.Serialize(Assessment("acme", "asm_0002", later, channel), HostJson.Options));
            InsertLegacy(connection, Assessment("acme", "asm_0003", earlierEqualInstant, channel),
                JsonSerializer.Serialize(Assessment("acme", "asm_0003", earlierEqualInstant, channel), HostJson.Options));
            var invalidTime = Assessment("acme", "asm_bad_time", later.AddMinutes(-1), channel);
            InsertLegacy(connection, invalidTime, JsonSerializer.Serialize(invalidTime, HostJson.Options));
            using var invalidate = connection.CreateCommand();
            invalidate.CommandText = "UPDATE host_decision_ledger SET recorded_at = '0000-invalid' WHERE assessment_id = 'asm_bad_time';";
            invalidate.ExecuteNonQuery();
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var from = new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
        var first = await HistoryAsync(reviewer, threadId: "T1", limit: 1, from: from, to: to);
        Assert.Equal("asm_0002", Assert.Single(first.Decisions).AssessmentId);
        Assert.True(first.HasMore);

        var second = await HistoryAsync(reviewer, threadId: "T1", limit: 1, after: first.NextCursor, from: from, to: to);
        Assert.Equal("asm_0003", Assert.Single(second.Decisions).AssessmentId);
        var third = await HistoryAsync(reviewer, threadId: "T1", limit: 1, after: second.NextCursor, from: from, to: to);
        Assert.Equal("asm_0001", Assert.Single(third.Decisions).AssessmentId);
        Assert.False(third.HasMore);

        var equalInstantRange = await HistoryAsync(
            reviewer,
            threadId: "T1",
            limit: 10,
            from: from,
            to: later);
        Assert.Collection(
            equalInstantRange.Decisions,
            firstDecision => Assert.Equal("asm_0003", firstDecision.AssessmentId),
            secondDecision => Assert.Equal("asm_0001", secondDecision.AssessmentId));

        var allValid = await HistoryAsync(reviewer, threadId: "T1", limit: 10);
        Assert.Equal(1, allValid.SkippedCount);
        Assert.Equal(3, allValid.Decisions.Count);
    }

    [Fact]
    public void Existing_database_is_upgraded_and_valid_legacy_channel_context_is_backfilled()
    {
        var root = Path.Combine(Path.GetTempPath(), "stylomail-history-migration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "host.db");
        var slack = Assessment("acme", "asm_legacy_slack", DateTimeOffset.UtcNow, new ChannelContext
        {
            Kind = ChannelKind.Slack,
            WorkspaceId = "W-old",
            ChannelId = "C-old",
            ThreadId = "T-old",
        });
        var email = Assessment("acme", "asm_legacy_email", DateTimeOffset.UtcNow, ChannelContext.Email);

        var slackPayload = JsonSerializer.Serialize(slack, HostJson.Options);
        var emailPayload = JsonSerializer.Serialize(email, HostJson.Options);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE host_decision_ledger (tenant_id TEXT NOT NULL, assessment_id TEXT NOT NULL, "
                + "internal_message_id TEXT NOT NULL, action TEXT NOT NULL, recorded_at TEXT NOT NULL, "
                + "payload TEXT NOT NULL, PRIMARY KEY (tenant_id, assessment_id));";
            command.ExecuteNonQuery();
            InsertLegacy(connection, slack, slackPayload);
            InsertLegacy(connection, email, emailPayload);
        }

        var database = new HostDatabase(Options.Create(new HostStorageOptions { DatabasePath = path }));
        database.EnsureCreated();
        database.EnsureCreated();

        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT assessment_id, channel_kind, workspace_id, channel_id, thread_id, recorded_at, payload "
                + "FROM host_decision_ledger ORDER BY assessment_id;";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("asm_legacy_email", reader.GetString(0));
            Assert.Equal("Email", reader.GetString(1));
            Assert.True(reader.IsDBNull(2));
            Assert.True(reader.IsDBNull(3));
            Assert.True(reader.IsDBNull(4));
            Assert.Equal(email.AssessedAt.ToUniversalTime().ToString("O"), reader.GetString(5));
            Assert.Equal(emailPayload, reader.GetString(6));

            Assert.True(reader.Read());
            Assert.Equal("asm_legacy_slack", reader.GetString(0));
            Assert.Equal("Slack", reader.GetString(1));
            Assert.Equal("W-old", reader.GetString(2));
            Assert.Equal("C-old", reader.GetString(3));
            Assert.Equal("T-old", reader.GetString(4));
            Assert.Equal(slack.AssessedAt.ToUniversalTime().ToString("O"), reader.GetString(5));
            Assert.Equal(slackPayload, reader.GetString(6));
            Assert.False(reader.Read());
        }
    }

    private static async Task RecordAsync(TestHost host, MailAssessment assessment) =>
        await host.Services.GetRequiredService<IDecisionLedger>()
            .RecordAsync(assessment, CancellationToken.None);

    private static async Task<ConversationHistoryResponse> HistoryAsync(
        HttpClient client,
        string? threadId,
        int limit,
        string? after = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var query = $"workspaceId=W1&channelId=C1&limit={limit}";
        if (threadId is not null)
        {
            query += $"&threadId={Uri.EscapeDataString(threadId)}";
        }

        if (from is not null)
        {
            query += $"&from={Uri.EscapeDataString(from.Value.ToString("O"))}";
        }

        if (to is not null)
        {
            query += $"&to={Uri.EscapeDataString(to.Value.ToString("O"))}";
        }

        if (after is not null)
        {
            query += $"&after={Uri.EscapeDataString(after)}";
        }

        var response = await client.GetAsync($"/v1/conversations/history?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = JsonSerializer.Deserialize<ConversationHistoryResponse>(
            await response.Content.ReadAsStringAsync(), HostJson.Options);
        return Assert.IsType<ConversationHistoryResponse>(result);
    }

    private static void InsertLegacy(SqliteConnection connection, MailAssessment assessment, string payload)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO host_decision_ledger "
            + "(tenant_id, assessment_id, internal_message_id, action, recorded_at, payload) "
            + "VALUES ($tenant, $id, $message, $action, $recorded, $payload);";
        command.Parameters.AddWithValue("$tenant", assessment.TenantId);
        command.Parameters.AddWithValue("$id", assessment.AssessmentId);
        command.Parameters.AddWithValue("$message", assessment.InternalMessageId);
        command.Parameters.AddWithValue("$action", assessment.Action.ToString());
        command.Parameters.AddWithValue("$recorded", assessment.AssessedAt.ToString("O"));
        command.Parameters.AddWithValue("$payload", payload);
        command.ExecuteNonQuery();
    }

    private static MailAssessment Assessment(
        string tenantId,
        string assessmentId,
        DateTimeOffset at,
        ChannelContext channel) => new()
    {
        AssessmentId = assessmentId,
        InternalMessageId = $"message-{assessmentId}",
        TenantId = tenantId,
        Channel = channel,
        Evidence = [],
        RiskDimensions = [],
        RiskIndex = 0,
        RiskIndexDenominator = 0,
        CoveredWeightFraction = 0,
        Action = MailAction.Allow,
        DeliveryTiming = DeliveryTiming.PostDelivery,
        Reasons = [],
        Versions = new AssessmentVersions
        {
            PolicyVersion = "policy/1",
            QuestionSchemaVersion = "questions/1",
            PreprocessingVersion = "preprocess/1",
        },
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
        RecipientDispositions = [],
        AssessedAt = at,
    };
}
