using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StyloMail.Adaptive.Profiles;
using StyloMail.Core;
using StyloMail.Host.Contracts;
using StyloMail.Host.Controls;
using StyloMail.Host.Decisions;
using StyloMail.Host.Profiles;
using StyloMail.Host.Storage;
using StyloMail.Queue;

namespace StyloMail.Host.Tests;

/// <summary>
/// The dashboard's read-only aggregates: flow, queues, storage and the entity directory.
/// </summary>
/// <remarks>
/// <para>
/// The assertions are about what these routes refuse to claim as much as what they compute. A
/// missing source is null and named, a measured zero stays zero, a direction that cannot be
/// recovered is never split, and no response carries an address, a path or a filename.
/// </para>
/// <para>
/// Fixtures seed the real queue and the real ledger, because flow is a join between them and a fake
/// would leave the join, the window predicate and the tenant filter unverified.
/// </para>
/// </remarks>
public sealed class SummaryEndpointsTests
{
    private const string Acme = TestPrincipals.AcmeTenant;
    private const string Globex = TestPrincipals.GlobexTenant;
    private const string PrincipalA = "principal-a";
    private const string PrincipalB = "principal-b";
    private const string ProfileKey = "0123456789abcdef0123456789abcdef";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // ---------------------------------------------------------------------------------------------
    // Authorisation and tenant scope
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/v1/overview/flow?window=last_24_hours")]
    [InlineData("/v1/queues/summary")]
    [InlineData("/v1/storage/summary")]
    [InlineData("/v1/entities?kind=sender")]
    public async Task Every_summary_route_requires_authentication(string path)
    {
        using var host = new TestHost();
        using var client = host.Anonymous();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("/v1/overview/flow?window=last_24_hours")]
    [InlineData("/v1/queues/summary")]
    [InlineData("/v1/storage/summary")]
    [InlineData("/v1/entities?kind=sender")]
    public async Task A_sending_principal_cannot_read_the_dashboard_aggregates(string path)
    {
        // Review is a separate grant from Send. A dashboard is reviewer-level visibility over the
        // tenant's mail, queue and people, and none of it is something a sender may enumerate.
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_window_is_named_not_defaulted()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var response = await client.GetAsync("/v1/overview/flow?window=today");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("unknown_window", body, StringComparison.Ordinal);
        Assert.Contains("last_24_hours", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_entity_kind_is_named_not_defaulted()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var response = await client.GetAsync("/v1/entities?kind=everyone");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("unknown_kind", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_over_long_directory_query_is_refused_not_silently_shortened()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var query = new string('a', EntityDirectoryStore.MaxQueryLength + 1);
        var response = await client.GetAsync($"/v1/entities?kind=sender&query={query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("query_too_long", await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_summary_never_reads_another_tenants_rows()
    {
        using var host = new TestHost().WithClock();
        await AcceptAsync(host, Acme, MailDirection.Inbound, DeliveryState.Held);
        await AcceptAsync(host, Globex, MailDirection.Inbound, DeliveryState.Held);

        using var acme = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        using var globex = host.ClientAs(TestPrincipals.GlobexReviewerKey);

        var acmeFlow = await ReadAsync<FlowSummaryResponse>(
            await acme.GetAsync("/v1/overview/flow?window=last_24_hours"));
        var globexFlow = await ReadAsync<FlowSummaryResponse>(
            await globex.GetAsync("/v1/overview/flow?window=last_24_hours"));

        Assert.Equal(1, acmeFlow.Totals.Inbound);
        Assert.Equal(1, globexFlow.Totals.Inbound);

        var acmeQueues = await ReadAsync<QueueSummaryResponse>(
            await acme.GetAsync("/v1/queues/summary"));

        Assert.Equal(1, acmeQueues.Counts.Held);
        Assert.Equal(1, acmeQueues.Counts.AwaitingDecision);

        var acmeStorage = await ReadAsync<StorageSummaryResponse>(
            await acme.GetAsync("/v1/storage/summary"));
        var itemRows = Assert.Single(
            acmeStorage.RowCounts, row => row.Table == "queue_item");

        // The control for the row count: the listing reports the acting tenant's one row, so a
        // cross-tenant count of two cannot pass as a correct answer.
        Assert.Equal(1, itemRows.Count);
    }

    // ---------------------------------------------------------------------------------------------
    // Flow
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_empty_window_is_measured_zero_not_an_absence()
    {
        using var host = new TestHost().WithClock();
        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var flow = await ReadAsync<FlowSummaryResponse>(
            await client.GetAsync("/v1/overview/flow?window=last_24_hours"));

        Assert.Equal(SummaryAvailability.Available, flow.Availability);
        Assert.Equal("last_24_hours", flow.Window);
        Assert.Equal(60, flow.BucketMinutes);
        Assert.Equal(24, flow.Buckets.Count);
        Assert.Empty(flow.NotProduced);

        Assert.Equal(0, flow.Totals.Inbound);
        Assert.Equal(0, flow.Totals.Outbound);
        Assert.Equal(0, flow.Totals.Held);
        Assert.Equal(0, flow.Totals.Review);
        Assert.Equal(0, flow.Totals.Quarantined);
        Assert.Equal(0, flow.Totals.Delivered);
        Assert.Equal(0, flow.Totals.Rejected);
        Assert.Equal(0, flow.Totals.Other);

        Assert.All(flow.Buckets, bucket => Assert.Equal(0, bucket.Inbound));

        // Rejected is measured zero in every bucket of a successful ledger read. A null here would
        // mean "the ledger could not be read", which is a different fact from "nothing was rejected".
        Assert.All(flow.Buckets, bucket => Assert.Equal(0, bucket.Rejected));
        Assert.Equal(flow.WindowStart, flow.Buckets[0].BucketStart);
        Assert.True(flow.Coverage.Complete);
        Assert.Null(flow.Coverage.Note);
    }

    [Fact]
    public async Task Flow_splits_direction_and_current_disposition_into_one_basis()
    {
        using var host = new TestHost().WithClock();

        await AcceptAsync(host, Acme, MailDirection.Inbound, DeliveryState.Held);
        await AcceptAsync(host, Acme, MailDirection.Inbound, DeliveryState.Delivered);
        await AcceptAsync(host, Acme, MailDirection.Outbound, DeliveryState.Quarantined);
        await AcceptAsync(host, Acme, MailDirection.Outbound, DeliveryState.Review);
        await AcceptAsync(host, Acme, MailDirection.Outbound, DeliveryState.Queued);

        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var flow = await ReadAsync<FlowSummaryResponse>(
            await client.GetAsync("/v1/overview/flow?window=last_24_hours"));

        Assert.Equal(DashboardSummaryStore.FlowBasis, flow.Basis);
        Assert.Equal(DashboardSummaryStore.RejectedBasis, flow.RejectedBasis);
        Assert.Equal(2, flow.Totals.Inbound);
        Assert.Equal(3, flow.Totals.Outbound);
        Assert.Equal(1, flow.Totals.Held);
        Assert.Equal(1, flow.Totals.Review);
        Assert.Equal(1, flow.Totals.Quarantined);
        Assert.Equal(1, flow.Totals.Delivered);
        Assert.Equal(1, flow.Totals.Other);

        // The series reconcile: every accepted message is in exactly one disposition.
        Assert.Equal(
            flow.Totals.Inbound + flow.Totals.Outbound,
            flow.Totals.Held + flow.Totals.Review + flow.Totals.Quarantined
                + flow.Totals.Delivered + flow.Totals.Other);

        // No ledger rows were written, so the ledger was read and every bucket is a measured zero.
        Assert.All(flow.Buckets, bucket => Assert.Equal(0, bucket.Rejected));

        Assert.Equal(5, flow.Coverage.MessagesCounted);
        Assert.True(flow.Coverage.Complete);
        Assert.Contains("queue_item", flow.Coverage.Tables);
        Assert.Contains("host_decision_ledger", flow.Coverage.Tables);
    }

    [Fact]
    public async Task A_rejected_decision_without_a_queue_row_is_counted_and_its_direction_is_named_as_unknown()
    {
        using var host = new TestHost().WithClock();
        await AcceptAsync(host, Acme, MailDirection.Inbound, DeliveryState.Queued);
        await RecordAsync(host, Acme, "rejected-1", MailAction.Reject, host.Clock.GetUtcNow());

        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var flow = await ReadAsync<FlowSummaryResponse>(
            await client.GetAsync("/v1/overview/flow?window=last_24_hours"));

        // The ledger row never reached the queue, so it contributes no direction. It is counted as
        // rejected, and the gap is named rather than split between inbound and outbound.
        Assert.Equal(1, flow.Totals.Rejected);
        Assert.Equal(1, flow.Totals.Inbound);
        Assert.Equal(0, flow.Totals.Outbound);
        Assert.Equal(1, flow.Coverage.DirectionNotRecoverable);
        Assert.Equal(1, flow.Coverage.DecisionsCounted);
        Assert.Contains("direction is not recoverable", flow.Coverage.Note, StringComparison.Ordinal);
        Assert.True(flow.Coverage.Complete);

        // Exactly one bucket carries the reject; every other bucket is zero, not null.
        Assert.Equal(1, flow.Buckets.Sum(bucket => bucket.Rejected ?? 0));
        Assert.Equal(1, flow.Buckets.Count(bucket => bucket.Rejected == 1));
        Assert.Equal(
            flow.Buckets.Count - 1,
            flow.Buckets.Count(bucket => bucket.Rejected == 0));
    }

    [Fact]
    public async Task The_seven_day_window_uses_daily_buckets()
    {
        using var host = new TestHost().WithClock();
        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var flow = await ReadAsync<FlowSummaryResponse>(
            await client.GetAsync("/v1/overview/flow?window=last_7_days"));

        Assert.Equal("last_7_days", flow.Window);
        Assert.Equal(1440, flow.BucketMinutes);
        Assert.Equal(7, flow.Buckets.Count);
        Assert.Equal(TimeSpan.FromDays(7), flow.WindowEnd - flow.WindowStart);
    }

    [Fact]
    public async Task A_queue_source_failure_leaves_the_measured_ledger_series_and_names_the_gap()
    {
        // A host database with the ledger table but no queue table: the exact partial-source state
        // the route has to survive. The ledger row is inserted directly because this is a store
        // test, not an acceptance test; the queue's absence is the condition under test.
        var directory = Path.Combine(
            Path.GetTempPath(), "stylomail-summary-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
            var database = new HostDatabase(Options.Create(new HostStorageOptions
            {
                DatabasePath = Path.Combine(directory, "host.db"),
            }));
            database.EnsureCreated();

            using (var connection = database.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO host_decision_ledger
                        (tenant_id, assessment_id, internal_message_id, action, recorded_at, payload)
                    VALUES ($tenant, $id, $message, 'Reject', $at, '{}');
                    """;
                command.Parameters.AddWithValue("$tenant", Acme);
                command.Parameters.AddWithValue("$id", "reject-only");
                command.Parameters.AddWithValue("$message", "message-reject-only");
                command.Parameters.AddWithValue(
                    "$at", clock.GetUtcNow().ToUniversalTime().ToString("O"));
                command.ExecuteNonQuery();
            }

            var store = new DashboardSummaryStore(
                database,
                new SpoolStore(Path.Combine(directory, "spool")),
                clock);

            var flow = store.ReadFlow(Acme, FlowWindow.Last24Hours, CancellationToken.None);

            Assert.Equal(SummaryAvailability.Partial, flow.Availability);
            Assert.False(flow.Coverage.Complete);
            Assert.Equal(1, flow.Totals.Rejected);
            Assert.Null(flow.Totals.Inbound);
            Assert.Null(flow.Totals.Outbound);
            Assert.Null(flow.Totals.Delivered);
            Assert.Null(flow.Coverage.MessagesCounted);

            Assert.Contains(
                flow.NotProduced,
                entry => entry.Series == "inbound" && entry.Reason == SummaryReasons.QueueUnreadable);
            Assert.Contains(
                flow.NotProduced,
                entry => entry.Series == "delivered" && entry.Reason == SummaryReasons.QueueUnreadable);

            // The gap needs both tables, so it is not produced either. It is named rather than
            // rendered as the zero that would read as "every decision had a recoverable direction".
            Assert.Null(flow.Coverage.DirectionNotRecoverable);
            Assert.Equal(1, flow.Buckets.Sum(bucket => bucket.Rejected ?? 0));
            Assert.Equal(1, flow.Buckets.Count(bucket => bucket.Rejected == 1));
            Assert.All(flow.Buckets, bucket => Assert.Null(bucket.Inbound));
            Assert.Contains(
                flow.NotProduced,
                entry => entry.Series == "directionNotRecoverable"
                    && entry.Reason == SummaryReasons.QueueUnreadable);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_ledger_source_failure_leaves_the_measured_queue_series_and_names_rejected_unknown()
    {
        // The mirror of the store-level queue-missing test: here the queue is readable and the
        // ledger is not. The queue half must stay measured, and every rejected value must be null
        // with a named reason rather than the zero that would read as "nothing was rejected".
        using var host = new TestHost().WithClock();
        await AcceptAsync(host, Acme, MailDirection.Inbound, DeliveryState.Held);

        using (var connection = host.Services.GetRequiredService<HostDatabase>().Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE host_decision_ledger;";
            command.ExecuteNonQuery();
        }

        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var flow = await ReadAsync<FlowSummaryResponse>(
            await client.GetAsync("/v1/overview/flow?window=last_24_hours"));

        Assert.Equal(SummaryAvailability.Partial, flow.Availability);
        Assert.False(flow.Coverage.Complete);

        // The queue half is still true.
        Assert.Equal(1, flow.Totals.Inbound);
        Assert.Equal(0, flow.Totals.Outbound);
        Assert.Equal(1, flow.Totals.Held);
        Assert.Equal(1, flow.Buckets.Sum(bucket => bucket.Inbound ?? 0));

        // The ledger half is absent, not zero.
        Assert.Null(flow.Totals.Rejected);
        Assert.Null(flow.Coverage.DecisionsCounted);
        Assert.Null(flow.Coverage.DirectionNotRecoverable);
        Assert.All(flow.Buckets, bucket => Assert.Null(bucket.Rejected));
        Assert.All(flow.Buckets, bucket => Assert.NotNull(bucket.Inbound));

        Assert.Contains(
            flow.NotProduced,
            entry => entry.Series == "rejected"
                && entry.Reason == SummaryReasons.LedgerUnreadable);
        Assert.Contains(
            flow.NotProduced,
            entry => entry.Series == "directionNotRecoverable"
                && entry.Reason == SummaryReasons.LedgerUnreadable);
    }

    // ---------------------------------------------------------------------------------------------
    // Queues
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Queue_counts_are_the_current_attention_states_and_do_not_add_up()
    {
        using var host = new TestHost().WithClock();

        await AcceptAsync(host, Acme, MailDirection.Outbound, DeliveryState.Held);
        await AcceptAsync(host, Acme, MailDirection.Outbound, DeliveryState.Quarantined);
        await AcceptAsync(host, Acme, MailDirection.Outbound, DeliveryState.Review);

        // One item with recipients in two attention states. This is the case that makes the counts
        // overlap: it belongs to both "being re-checked" and "stopped before delivery", and the
        // union must count it once.
        await AcceptAsync(
            host, Acme, MailDirection.Outbound, DeliveryState.Held,
            secondRecipientState: DeliveryState.Quarantined);

        await AcceptAsync(host, Acme, MailDirection.Outbound, DeliveryState.Queued);

        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var summary = await ReadAsync<QueueSummaryResponse>(
            await client.GetAsync("/v1/queues/summary"));

        Assert.True(summary.Overlapping);
        Assert.Equal(DashboardSummaryStore.QueueBasis, summary.Basis);
        Assert.Equal(4, summary.Counts.AwaitingDecision);
        Assert.Equal(2, summary.Counts.Held);
        Assert.Equal(2, summary.Counts.Quarantined);
        Assert.Equal(1, summary.Counts.Review);

        // AwaitingDecision is the union, so it is not the sum of the other three: the mixed item
        // is counted once in the union and twice across the three states.
        Assert.NotEqual(
            summary.Counts.AwaitingDecision,
            summary.Counts.Held + summary.Counts.Quarantined + summary.Counts.Review);
    }

    // ---------------------------------------------------------------------------------------------
    // Storage
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Storage_reports_sizes_and_capped_rows_with_no_path_in_the_body()
    {
        using var host = new TestHost().WithClock();
        await AcceptAsync(host, Acme, MailDirection.Inbound, DeliveryState.Held, payloadBytes: 128);

        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var response = await client.GetAsync("/v1/storage/summary");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(host.Root, body, StringComparison.Ordinal);
        Assert.DoesNotContain("host.db", body, StringComparison.Ordinal);
        Assert.DoesNotContain("spool/", body, StringComparison.Ordinal);

        var storage = JsonSerializer.Deserialize<StorageSummaryResponse>(body, Json)!;

        Assert.NotNull(storage.DatabaseBytes);
        Assert.True(storage.DatabaseBytes > 0);
        Assert.NotNull(storage.WriteAheadLogBytes);
        Assert.NotNull(storage.SpoolBytes);
        Assert.True(storage.SpoolBytes > 0);
        Assert.Equal(1, storage.SpoolFileCount);
        Assert.True(storage.PayloadBytes >= 128);
        Assert.Null(storage.RetentionHorizon);
        Assert.Equal(SummaryReasons.RetentionHorizonNotRecorded, storage.RetentionReason);

        var itemRows = Assert.Single(storage.RowCounts, row => row.Table == "queue_item");
        Assert.Equal(1, itemRows.Count);
        Assert.False(itemRows.Truncated);

        var ledgerRows = Assert.Single(
            storage.RowCounts, row => row.Table == "host_decision_ledger");
        Assert.Equal(0, ledgerRows.Count);
    }

    [Fact]
    public async Task A_row_count_at_the_cap_reports_the_cap_and_a_truncated_flag()
    {
        // The cap is injected at one so the boundary can be exercised against two real rows. A
        // count that merely stays under ten thousand proves nothing: mutating either the flag or
        // the clamp would still pass. One row is exactly at the cap, two is over it.
        using var host = WithSummaryLimits(new TestHost().WithClock(), rowCountLimit: 1);
        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        await AcceptAsync(host, Acme, MailDirection.Inbound, DeliveryState.Queued);

        var atCap = await ReadAsync<StorageSummaryResponse>(
            await client.GetAsync("/v1/storage/summary"));
        var atCapRow = Assert.Single(atCap.RowCounts, row => row.Table == "queue_item");

        Assert.Equal(1, atCapRow.Count);
        Assert.False(atCapRow.Truncated);

        await AcceptAsync(host, Acme, MailDirection.Inbound, DeliveryState.Queued);

        var overCap = await ReadAsync<StorageSummaryResponse>(
            await client.GetAsync("/v1/storage/summary"));
        var overCapRow = Assert.Single(overCap.RowCounts, row => row.Table == "queue_item");

        // Two rows exist, the reported count is the cap, and the flag says the raw count was larger.
        // The raw count is read directly so the fixture proves what the response is claiming about.
        using (var connection = host.Services.GetRequiredService<HostDatabase>().Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT COUNT(*) FROM queue_item WHERE tenant_id = $tenant;";
            command.Parameters.AddWithValue("$tenant", Acme);
            Assert.Equal(2, Convert.ToInt32(command.ExecuteScalar()));
        }

        Assert.Equal(1, overCapRow.Count);
        Assert.True(overCapRow.Truncated);
    }

    [Fact]
    public void An_unreadable_row_count_is_null_with_a_reason_not_zero()
    {
        // A host database without the queue or persistence tables: the host's own tables read as
        // measured zeroes, the missing ones are null with a reason, and the route still answers.
        var directory = Path.Combine(
            Path.GetTempPath(), "stylomail-summary-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
            var database = new HostDatabase(Options.Create(new HostStorageOptions
            {
                DatabasePath = Path.Combine(directory, "host.db"),
            }));
            database.EnsureCreated();

            var store = new DashboardSummaryStore(
                database,
                new SpoolStore(Path.Combine(directory, "spool")),
                clock);

            var storage = store.ReadStorage(Acme, CancellationToken.None);

            var unreadable = Assert.Single(storage.RowCounts, row => row.Table == "queue_item");
            Assert.Null(unreadable.Count);
            Assert.Equal("table_unreadable", unreadable.Reason);
            Assert.Null(storage.PayloadBytes);

            // The control: a table that exists and is empty is still a measured zero.
            var readable = Assert.Single(
                storage.RowCounts, row => row.Table == "host_decision_ledger");
            Assert.Equal(0, readable.Count);
            Assert.False(readable.Truncated);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_capped_spool_walk_is_flagged_as_a_lower_bound()
    {
        using var host = WithSummaryLimits(new TestHost().WithClock(), spoolFileLimit: 1);
        await AcceptAsync(host, Acme, MailDirection.Inbound, DeliveryState.Queued, payloadBytes: 64);
        await AcceptAsync(host, Acme, MailDirection.Inbound, DeliveryState.Queued, payloadBytes: 64);

        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var storage = await ReadAsync<StorageSummaryResponse>(
            await client.GetAsync("/v1/storage/summary"));

        Assert.True(storage.SpoolTruncated);
        Assert.Equal(1, storage.SpoolFileCount);
        Assert.NotNull(storage.SpoolBytes);
        Assert.True(storage.SpoolBytes > 0);
    }

    // ---------------------------------------------------------------------------------------------
    // Entity directory
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_sender_directory_lists_the_operator_label_company_and_volume()
    {
        using var host = new TestHost().WithClock().WithProfileMasterKey(ProfileKey);
        var at = host.Clock.GetUtcNow().AddHours(-2);

        await host.Services.GetRequiredService<ICompanyStore>().PutAsync(
            Acme,
            new Company
            {
                CompanyId = "co_acme",
                Name = "Acme Corporation",
                UpdatedBy = "test",
                UpdatedAt = at,
            },
            CancellationToken.None);

        await host.Services.GetRequiredService<ISenderProfileStore>().PutAsync(
            Acme,
            new SenderProfile
            {
                PrincipalId = PrincipalA,
                Label = "Acme Billing",
                CompanyId = "co_acme",
                UpdatedBy = "test",
                UpdatedAt = at,
            },
            CancellationToken.None);

        var hasher = new ProfileKeyHasher(Encoding.UTF8.GetBytes(ProfileKey));
        InsertProfile(
            host,
            Acme,
            "OutboundSender",
            hasher.Hash(Acme, PrincipalA) + "#outbound",
            MailDirection.Outbound,
            attempts: 7,
            lastObservedAt: at);

        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var page = await ReadAsync<EntityDirectoryResponse>(
            await client.GetAsync("/v1/entities?kind=sender"));

        var row = Assert.Single(page.Entities, entity => entity.ScopeId == PrincipalA);

        Assert.Equal("sender", row.Kind);
        Assert.Equal(MailDirection.Outbound, row.Direction);
        Assert.Equal("Acme Billing", row.DisplayLabel);
        Assert.Equal(SummaryAvailability.Available, row.DisplayLabelAvailability);
        Assert.Equal("sender_profile", row.DisplayLabelSource);
        Assert.Equal("co_acme", row.CompanyId);
        Assert.Equal("Acme Corporation", row.CompanyName);
        Assert.Equal(7, row.Volume.Messages);
        Assert.Equal(7, row.Volume.Outbound);
        Assert.Equal(0, row.Volume.Inbound);
        Assert.Equal(at.ToUniversalTime(), row.LastContactAt);

        // Evidence is not computed per directory row, and says so rather than reporting zeroes.
        Assert.Equal(SummaryAvailability.NotProduced, row.Evidence.Reply.Availability);
        Assert.Equal(SummaryReasons.DirectoryEvidenceNotComputed, row.Evidence.Reply.Reason);
    }

    [Fact]
    public async Task The_recipient_directory_names_an_unrecoverable_label_instead_of_guessing()
    {
        using var host = new TestHost().WithClock().WithProfileMasterKey(ProfileKey);
        var at = host.Clock.GetUtcNow().AddDays(-1);

        // A profile key with no matching envelope in the queue: the hash cannot be reversed, so the
        // honest answer is a named absence, not a label assembled from the hash.
        InsertProfile(host, Acme, "InboundSenderIdentity", "hash-a#inbound",
            MailDirection.Inbound, attempts: 14, lastObservedAt: at);
        InsertProfile(host, Globex, "InboundSenderIdentity", "hash-g#inbound",
            MailDirection.Inbound, attempts: 99, lastObservedAt: at);

        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var page = await ReadAsync<EntityDirectoryResponse>(
            await client.GetAsync("/v1/entities?kind=recipient"));

        var row = Assert.Single(page.Entities);

        Assert.Equal("hash-a#inbound", row.ScopeId);
        Assert.Equal(MailDirection.Inbound, row.Direction);
        Assert.Null(row.DisplayLabel);
        Assert.Equal(SummaryAvailability.NotProduced, row.DisplayLabelAvailability);
        Assert.Null(row.DisplayLabelSource);
        Assert.Equal(SummaryReasons.AddressNotInBoundedScan, row.DisplayLabelReason);
        Assert.Null(row.Domain);
        Assert.Equal(SummaryReasons.AddressNotInBoundedScan, row.DomainReason);
        Assert.Equal(14, row.Volume.Messages);
        Assert.Equal(14, row.Volume.Inbound);
        Assert.Equal(0, row.Volume.Outbound);
        Assert.Equal(at.ToUniversalTime(), row.LastContactAt);
    }

    [Fact]
    public async Task The_recipient_directory_masks_recovered_envelope_addresses()
    {
        using var host = new TestHost().WithClock().WithProfileMasterKey(ProfileKey);
        var at = host.Clock.GetUtcNow();

        await AcceptAsync(host, Acme, MailDirection.Inbound, DeliveryState.Queued,
            mailFrom: "alice@example.test", recipient: "owner@acme.test");
        await AcceptAsync(host, Acme, MailDirection.Outbound, DeliveryState.Queued,
            mailFrom: "owner@acme.test", recipient: "bob@example.test");

        var hasher = new ProfileKeyHasher(Encoding.UTF8.GetBytes(ProfileKey));

        InsertProfile(host, Acme, "InboundSenderIdentity",
            hasher.Hash(Acme, "alice@example.test") + "|auth=none#inbound",
            MailDirection.Inbound, attempts: 3, lastObservedAt: at);
        InsertProfile(host, Acme, "Recipient",
            hasher.Hash(Acme, "bob@example.test") + "#outbound",
            MailDirection.Outbound, attempts: 2, lastObservedAt: at);

        // The tenant's own local mailbox: a Recipient profile in the inbound direction. It is not
        // a person the tenant deals with, so the directory must exclude it even though it exists.
        var localMailbox = hasher.Hash(Acme, "owner@acme.test") + "#inbound";
        InsertProfile(host, Acme, "Recipient", localMailbox,
            MailDirection.Inbound, attempts: 5, lastObservedAt: at);

        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var response = await client.GetAsync("/v1/entities?kind=recipient");
        var body = await response.Content.ReadAsStringAsync();
        var page = JsonSerializer.Deserialize<EntityDirectoryResponse>(body, Json)!;

        Assert.Equal(2, page.Entities.Count);
        Assert.DoesNotContain(page.Entities, entity => entity.ScopeId == localMailbox);

        var alice = Assert.Single(page.Entities, entity => entity.Direction == MailDirection.Inbound);
        Assert.Equal("a***@example.test", alice.DisplayLabel);
        Assert.Equal(SummaryAvailability.Available, alice.DisplayLabelAvailability);
        Assert.Equal("queue_envelope_masked", alice.DisplayLabelSource);
        Assert.Null(alice.DisplayLabelReason);
        Assert.Equal("example.test", alice.Domain);

        var bob = Assert.Single(page.Entities, entity => entity.Direction == MailDirection.Outbound);
        Assert.Equal("b***@example.test", bob.DisplayLabel);
        Assert.Equal("queue_envelope_masked", bob.DisplayLabelSource);
        Assert.Equal("example.test", bob.Domain);

        // The whole point: the raw envelope addresses are recovery inputs, never response values.
        Assert.DoesNotContain("alice@example.test", body, StringComparison.Ordinal);
        Assert.DoesNotContain("bob@example.test", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_null_sender_profile_is_not_maskable()
    {
        using var host = new TestHost().WithClock().WithProfileMasterKey(ProfileKey);
        var at = host.Clock.GetUtcNow();

        // A bounce has no identity to mask. The queue accepts the null sender inbound; the profile
        // exists, and the row must say "not maskable" rather than "outside the scan" or a guess.
        await AcceptAsync(host, Acme, MailDirection.Inbound, DeliveryState.Queued,
            mailFrom: "<>", recipient: "owner@acme.test");

        var hasher = new ProfileKeyHasher(Encoding.UTF8.GetBytes(ProfileKey));
        InsertProfile(host, Acme, "InboundSenderIdentity",
            hasher.Hash(Acme, "<>") + "|auth=none#inbound",
            MailDirection.Inbound, attempts: 1, lastObservedAt: at);

        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var page = await ReadAsync<EntityDirectoryResponse>(
            await client.GetAsync("/v1/entities?kind=recipient"));

        var row = Assert.Single(page.Entities);
        Assert.Null(row.DisplayLabel);
        Assert.Equal(SummaryReasons.AddressNotMaskable, row.DisplayLabelReason);
    }

    [Fact]
    public async Task A_display_name_candidate_is_extracted_and_malformed_values_are_refused()
    {
        using var host = new TestHost().WithClock().WithProfileMasterKey(ProfileKey);
        var at = host.Clock.GetUtcNow();
        var hasher = new ProfileKeyHasher(Encoding.UTF8.GetBytes(ProfileKey));

        // The envelope column should hold a bare address, but a candidate can arrive in the
        // display-name form. The mask must show the extracted address, never the closing bracket.
        await AcceptAsync(host, Acme, MailDirection.Inbound, DeliveryState.Queued,
            mailFrom: "Zoe <zoe@example.test>", recipient: "owner@acme.test");
        InsertProfile(host, Acme, "InboundSenderIdentity",
            hasher.Hash(Acme, "Zoe <zoe@example.test>") + "|auth=none#inbound",
            MailDirection.Inbound, attempts: 1, lastObservedAt: at);

        // Surrounding whitespace is trimmed, not rendered.
        await AcceptAsync(host, Acme, MailDirection.Inbound, DeliveryState.Queued,
            mailFrom: "  bob@example.test  ", recipient: "owner@acme.test");
        InsertProfile(host, Acme, "InboundSenderIdentity",
            hasher.Hash(Acme, "bob@example.test") + "|auth=none#inbound",
            MailDirection.Inbound, attempts: 1, lastObservedAt: at.AddMinutes(1));

        // The control for the refusal path: a value with no local part and no domain.
        await AcceptAsync(host, Acme, MailDirection.Inbound, DeliveryState.Queued,
            mailFrom: "not an address", recipient: "owner@acme.test");
        InsertProfile(host, Acme, "InboundSenderIdentity",
            hasher.Hash(Acme, "not an address") + "|auth=none#inbound",
            MailDirection.Inbound, attempts: 1, lastObservedAt: at.AddMinutes(2));

        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var response = await client.GetAsync("/v1/entities?kind=recipient");
        var body = await response.Content.ReadAsStringAsync();
        var page = JsonSerializer.Deserialize<EntityDirectoryResponse>(body, Json)!;

        var zoe = Assert.Single(page.Entities, entity => entity.DisplayLabel == "z***@example.test");
        Assert.Equal("example.test", zoe.Domain);

        var bob = Assert.Single(page.Entities, entity => entity.DisplayLabel == "b***@example.test");
        Assert.Equal("example.test", bob.Domain);

        var malformed = Assert.Single(page.Entities, entity =>
            entity.DisplayLabelReason == SummaryReasons.AddressNotMaskable);
        Assert.Null(malformed.DisplayLabel);

        // Neither the raw forms nor the display-name wrapper survives into the response.
        Assert.DoesNotContain("zoe@example.test", body, StringComparison.Ordinal);
        Assert.DoesNotContain("bob@example.test", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Zoe <", body, StringComparison.Ordinal);
        Assert.DoesNotContain("not an address", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_masked_label_scan_is_capped_and_says_when_it_stopped()
    {
        using var host = WithEntityScanCap(new TestHost().WithClock().WithProfileMasterKey(ProfileKey), 1);
        var hasher = new ProfileKeyHasher(Encoding.UTF8.GetBytes(ProfileKey));

        await AcceptAsync(host, Acme, MailDirection.Inbound, DeliveryState.Queued,
            mailFrom: "older@example.test", recipient: "owner@acme.test");
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        await AcceptAsync(host, Acme, MailDirection.Inbound, DeliveryState.Queued,
            mailFrom: "newer@example.test", recipient: "owner@acme.test");

        var at = host.Clock.GetUtcNow();
        InsertProfile(host, Acme, "InboundSenderIdentity",
            hasher.Hash(Acme, "older@example.test") + "|auth=none#inbound",
            MailDirection.Inbound, attempts: 1, lastObservedAt: at.AddMinutes(-1));
        InsertProfile(host, Acme, "InboundSenderIdentity",
            hasher.Hash(Acme, "newer@example.test") + "|auth=none#inbound",
            MailDirection.Inbound, attempts: 1, lastObservedAt: at);

        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var page = await ReadAsync<EntityDirectoryResponse>(
            await client.GetAsync("/v1/entities?kind=recipient"));

        var newer = Assert.Single(page.Entities, entity =>
            entity.DisplayLabelSource == "queue_envelope_masked");
        Assert.Equal("n***@example.test", newer.DisplayLabel);

        var older = Assert.Single(page.Entities, entity =>
            entity.DisplayLabelReason == SummaryReasons.AddressNotInBoundedScan);
        Assert.Null(older.DisplayLabel);
    }

    [Fact]
    public async Task The_domain_directory_uses_the_stored_domain_as_its_label()
    {
        using var host = new TestHost().WithClock().WithProfileMasterKey(ProfileKey);
        var at = host.Clock.GetUtcNow().AddHours(-3);

        InsertProfile(host, Acme, "DomainContext", "example.test", direction: null,
            attempts: 3, lastObservedAt: at);

        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var page = await ReadAsync<EntityDirectoryResponse>(
            await client.GetAsync("/v1/entities?kind=domain&query=example"));

        var row = Assert.Single(page.Entities);

        Assert.Equal("example.test", row.ScopeId);
        Assert.Equal("example.test", row.DisplayLabel);
        Assert.Equal("domain", row.DisplayLabelSource);
        Assert.Equal("example.test", row.Domain);
        Assert.Null(row.Direction);
        Assert.Equal(3, row.Volume.Messages);
        Assert.Null(row.Volume.Inbound);
        Assert.Null(row.Volume.Outbound);
    }

    [Fact]
    public async Task The_company_directory_reads_the_company_table()
    {
        using var host = new TestHost().WithClock();

        await host.Services.GetRequiredService<ICompanyStore>().PutAsync(
            Acme,
            new Company
            {
                CompanyId = "co_acme",
                Name = "Acme Corporation",
                UpdatedBy = "test",
                UpdatedAt = host.Clock.GetUtcNow(),
            },
            CancellationToken.None);

        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var page = await ReadAsync<EntityDirectoryResponse>(
            await client.GetAsync("/v1/entities?kind=company"));

        var row = Assert.Single(page.Entities);

        Assert.Equal("co_acme", row.ScopeId);
        Assert.Equal("Acme Corporation", row.DisplayLabel);
        Assert.Equal("company", row.DisplayLabelSource);
        Assert.Equal(SummaryAvailability.NotProduced, row.Volume.Availability);
    }

    [Fact]
    public async Task The_directory_searches_and_pages_without_reading_another_tenant()
    {
        using var host = new TestHost().WithClock().WithProfileMasterKey(ProfileKey);
        var at = host.Clock.GetUtcNow();

        foreach (var (principal, label) in new[]
                 {
                     (PrincipalA, "Acme Billing"),
                     (PrincipalB, "Acme Support"),
                 })
        {
            await host.Services.GetRequiredService<ISenderProfileStore>().PutAsync(
                Acme,
                new SenderProfile
                {
                    PrincipalId = principal,
                    Label = label,
                    UpdatedBy = "test",
                    UpdatedAt = at,
                },
                CancellationToken.None);
        }

        await host.Services.GetRequiredService<ISenderProfileStore>().PutAsync(
            Globex,
            new SenderProfile
            {
                PrincipalId = "globex-billing",
                Label = "Globex Billing",
                UpdatedBy = "test",
                UpdatedAt = at,
            },
            CancellationToken.None);

        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var first = await ReadAsync<EntityDirectoryResponse>(
            await client.GetAsync("/v1/entities?kind=sender&limit=1"));

        Assert.Single(first.Entities);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextCursor);
        Assert.False(first.Coverage.Complete);
        Assert.Equal(PrincipalA, first.Entities[0].ScopeId);

        // The query returned both sender rows and the page returned one of them. Scanned is the
        // raw count, not the page length, so a client counting what remains can use it.
        Assert.Equal(2, first.Coverage.Scanned);

        var second = await ReadAsync<EntityDirectoryResponse>(
            await client.GetAsync(
                $"/v1/entities?kind=sender&limit=1&after={Uri.EscapeDataString(first.NextCursor!)}"));

        Assert.Single(second.Entities);
        Assert.Equal(PrincipalB, second.Entities[0].ScopeId);
        Assert.False(second.HasMore);
        Assert.True(second.Coverage.Complete);
        Assert.Equal(1, second.Coverage.Scanned);

        // Neither page contains the other tenant's sender, even though its label matches the query.
        var searched = await ReadAsync<EntityDirectoryResponse>(
            await client.GetAsync("/v1/entities?kind=sender&query=billing"));

        Assert.Single(searched.Entities);
        Assert.Equal(PrincipalA, searched.Entities[0].ScopeId);
        Assert.DoesNotContain(searched.Entities, entity => entity.ScopeId == "globex-billing");
    }

    [Fact]
    public async Task A_cursor_minted_for_another_kind_is_refused()
    {
        using var host = new TestHost().WithClock();

        await host.Services.GetRequiredService<ISenderProfileStore>().PutAsync(
            Acme,
            new SenderProfile
            {
                PrincipalId = PrincipalA,
                Label = "Acme Billing",
                UpdatedBy = "test",
                UpdatedAt = host.Clock.GetUtcNow(),
            },
            CancellationToken.None);

        await host.Services.GetRequiredService<ISenderProfileStore>().PutAsync(
            Acme,
            new SenderProfile
            {
                PrincipalId = PrincipalB,
                Label = "Acme Support",
                UpdatedBy = "test",
                UpdatedAt = host.Clock.GetUtcNow(),
            },
            CancellationToken.None);

        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var first = await ReadAsync<EntityDirectoryResponse>(
            await client.GetAsync("/v1/entities?kind=sender&limit=1"));

        var response = await client.GetAsync(
            $"/v1/entities?kind=company&after={Uri.EscapeDataString(first.NextCursor!)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_cursor", await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Query plans
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_aggregate_uses_an_index_and_scans_no_table_across_tenants()
    {
        using var host = new TestHost();
        _ = host.Services; // Builds the host and creates the schema.

        var flowQueue = Plan(host, DashboardSummaryStore.FlowQueueSql, command =>
        {
            command.Parameters.AddWithValue("$tenant", Acme);
            command.Parameters.AddWithValue("$from", 0L);
            command.Parameters.AddWithValue("$bucket", 3600L);
            command.Parameters.AddWithValue("$from_text", "2026-01-01T00:00:00.0000000Z");
            command.Parameters.AddWithValue("$to_text", "2026-01-02T00:00:00.0000000Z");
        });

        Assert.Contains(flowQueue, detail =>
            detail.Contains("ix_queue_item_tenant_created", StringComparison.Ordinal));
        Assert.DoesNotContain(flowQueue, detail =>
            detail.Contains("SCAN queue_item", StringComparison.Ordinal));

        var rejected = Plan(host, DashboardSummaryStore.FlowRejectedSql, command =>
        {
            command.Parameters.AddWithValue("$tenant", Acme);
            command.Parameters.AddWithValue("$from", 0L);
            command.Parameters.AddWithValue("$bucket", 3600L);
            command.Parameters.AddWithValue("$from_text", "2026-01-01T00:00:00.0000000+00:00");
            command.Parameters.AddWithValue("$to_text", "2026-01-02T00:00:00.0000000+00:00");
            command.Parameters.AddWithValue("$reject", MailAction.Reject.ToString());
        });

        Assert.Contains(rejected, detail =>
            detail.Contains("ix_host_decision_ledger_tenant", StringComparison.Ordinal));
        Assert.DoesNotContain(rejected, detail =>
            detail.Contains("SCAN host_decision_ledger", StringComparison.Ordinal));

        var gap = Plan(host, DashboardSummaryStore.FlowDirectionGapSql, command =>
        {
            command.Parameters.AddWithValue("$tenant", Acme);
            command.Parameters.AddWithValue("$from_text", "2026-01-01T00:00:00.0000000+00:00");
            command.Parameters.AddWithValue("$to_text", "2026-01-02T00:00:00.0000000+00:00");
        });

        Assert.Contains(gap, detail =>
            detail.Contains("ix_host_decision_ledger_tenant", StringComparison.Ordinal));
        Assert.Contains(gap, detail =>
            detail.Contains("ix_queue_item_tenant_message", StringComparison.Ordinal));
        Assert.DoesNotContain(gap, detail => detail.Contains("SCAN queue_item", StringComparison.Ordinal));

        var queues = Plan(host, DashboardSummaryStore.QueueSummarySql, command =>
        {
            command.Parameters.AddWithValue("$tenant", Acme);
            command.Parameters.AddWithValue("$held", (int)DeliveryState.Held);
            command.Parameters.AddWithValue("$quarantined", (int)DeliveryState.Quarantined);
            command.Parameters.AddWithValue("$review", (int)DeliveryState.Review);
        });

        Assert.Contains(queues, detail =>
            detail.Contains("ix_queue_item_tenant", StringComparison.Ordinal));
        Assert.DoesNotContain(queues, detail => detail.Contains("SCAN queue_item", StringComparison.Ordinal));

        var payloads = Plan(host, DashboardSummaryStore.PayloadBytesSql, command =>
            command.Parameters.AddWithValue("$tenant", Acme));

        Assert.Contains(payloads, detail =>
            detail.Contains("ix_queue_item_spool", StringComparison.Ordinal));

        foreach (var (table, sql) in DashboardSummaryStore.RowCountSql)
        {
            var plan = Plan(host, sql, command =>
            {
                command.Parameters.AddWithValue("$tenant", Acme);
                command.Parameters.AddWithValue("$cap", DashboardSummaryStore.RowCountCap + 1);
            });

            Assert.DoesNotContain(plan, detail =>
                detail.Contains($"SCAN {table}", StringComparison.Ordinal));
        }

        var senders = Plan(host, EntityDirectoryStore.SenderSql, command =>
        {
            command.Parameters.AddWithValue("$tenant", Acme);
            command.Parameters.AddWithValue("$after", DBNull.Value);
            command.Parameters.AddWithValue("$q", DBNull.Value);
            command.Parameters.AddWithValue("$limit", 51);
        });

        Assert.DoesNotContain(senders, detail =>
            detail.Contains("SCAN sender_profile", StringComparison.Ordinal));

        var companies = Plan(host, EntityDirectoryStore.CompanySql, command =>
        {
            command.Parameters.AddWithValue("$tenant", Acme);
            command.Parameters.AddWithValue("$after", DBNull.Value);
            command.Parameters.AddWithValue("$q", DBNull.Value);
            command.Parameters.AddWithValue("$limit", 51);
        });

        Assert.DoesNotContain(companies, detail =>
            detail.Contains("SCAN company", StringComparison.Ordinal));

        foreach (var sql in new[] { EntityDirectoryStore.RecipientSql, EntityDirectoryStore.DomainSql })
        {
            var plan = Plan(host, sql, command =>
            {
                command.Parameters.AddWithValue("$tenant", Acme);
                command.Parameters.AddWithValue("$scope_a", "InboundSenderIdentity");
                command.Parameters.AddWithValue("$scope_b", "Recipient");
                command.Parameters.AddWithValue("$scope", "DomainContext");
                command.Parameters.AddWithValue("$outbound", (int)MailDirection.Outbound);
                command.Parameters.AddWithValue("$after_at", DBNull.Value);
                command.Parameters.AddWithValue("$after_key", DBNull.Value);
                command.Parameters.AddWithValue("$q", DBNull.Value);
                command.Parameters.AddWithValue("$limit", 51);
            });

            Assert.Contains(plan, detail =>
                detail.Contains("ix_profiles_tenant_scope_observed", StringComparison.Ordinal));
            Assert.DoesNotContain(plan, detail =>
                detail.Contains("SCAN profiles", StringComparison.Ordinal));
        }

        var inboundCandidates = Plan(host, EntityDirectoryStore.InboundSenderCandidatesSql, command =>
        {
            command.Parameters.AddWithValue("$tenant", Acme);
            command.Parameters.AddWithValue("$inbound", (int)MailDirection.Inbound);
            command.Parameters.AddWithValue("$cap", 5_000);
        });

        Assert.Contains(inboundCandidates, detail =>
            detail.Contains("ix_queue_item_tenant_direction_created", StringComparison.Ordinal)
            && detail.Contains("direction=?", StringComparison.Ordinal));
        Assert.DoesNotContain(inboundCandidates, detail =>
            detail.Contains("SCAN queue_item", StringComparison.Ordinal));

        var outboundCandidates = Plan(
            host, EntityDirectoryStore.OutboundRecipientCandidatesSql, command =>
            {
                command.Parameters.AddWithValue("$tenant", Acme);
                command.Parameters.AddWithValue("$outbound", (int)MailDirection.Outbound);
                command.Parameters.AddWithValue("$cap", 5_000);
            });

        Assert.Contains(outboundCandidates, detail =>
            detail.Contains("ix_queue_item_tenant_direction_created", StringComparison.Ordinal)
            && detail.Contains("direction=?", StringComparison.Ordinal));
        Assert.DoesNotContain(outboundCandidates, detail =>
            detail.Contains("SCAN queue_item", StringComparison.Ordinal));
        Assert.Contains(outboundCandidates, detail =>
            detail.Contains("sqlite_autoindex_queue_recipient_1", StringComparison.Ordinal));
        Assert.DoesNotContain(outboundCandidates, detail =>
            detail.Contains("SCAN queue_recipient", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    /// <summary>Replaces the directory store with one whose envelope scan cap the test can reach.</summary>
    private static TestHost WithEntityScanCap(TestHost host, int cap)
        => host.Override(services =>
        {
            services.RemoveAll<EntityDirectoryStore>();
            services.AddSingleton(sp => new EntityDirectoryStore(
                sp.GetRequiredService<HostDatabase>(),
                sp.GetRequiredService<IAddressPseudonymSource>())
            {
                MaskedLabelScanCap = cap,
            });
        });

    /// <summary>Replaces the store with one whose caps the test can reach.</summary>
    private static TestHost WithSummaryLimits(
        TestHost host,
        int? rowCountLimit = null,
        int? spoolFileLimit = null)
        => host.Override(services =>
        {
            services.RemoveAll<DashboardSummaryStore>();
            services.AddSingleton(sp => new DashboardSummaryStore(
                sp.GetRequiredService<HostDatabase>(),
                sp.GetRequiredService<SpoolStore>(),
                sp.GetRequiredService<TimeProvider>())
            {
                RowCountLimit = rowCountLimit ?? DashboardSummaryStore.RowCountCap,
                SpoolFileLimit = spoolFileLimit ?? DashboardSummaryStore.SpoolFileCap,
            });
        });

    private static async Task<string> AcceptAsync(
        TestHost host,
        string tenantId,
        MailDirection direction,
        DeliveryState state,
        int payloadBytes = 32,
        DeliveryState? secondRecipientState = null,
        string mailFrom = "sender@example.test",
        string recipient = "owner@example.test")
    {
        var queue = host.Services.GetRequiredService<QueueStore>();
        var now = host.Clock.GetUtcNow();

        // Admission accepts only Queued, Held and Quarantined. Delivered and Review are written
        // directly afterwards, because the read model under test is the disposition, and driving a
        // real delivery through a transport is not what this fixture is isolating.
        var admission = state is DeliveryState.Held or DeliveryState.Quarantined
            ? state
            : DeliveryState.Queued;

        var accepted = await queue.AcceptAsync(
            new QueueSubmission
            {
                TenantId = tenantId,
                InternalMessageId = $"message-{Guid.NewGuid():N}",
                Direction = direction,
                TrustedPrincipalId = PrincipalA,
                MailFrom = mailFrom,
                MimeDigest = $"digest-{Guid.NewGuid():N}",
                Payload = new byte[payloadBytes],
                Recipients =
                [
                    new RecipientAdmission
                    {
                        Recipient = recipient,
                        State = admission,
                        ReEvaluateBy = admission == DeliveryState.Held ? now.AddHours(1) : null,
                    },
                    .. secondRecipientState is null
                        ? Array.Empty<RecipientAdmission>()
                        :
                        [
                            new RecipientAdmission
                            {
                                Recipient = "second@example.test",
                                State = secondRecipientState.Value,
                                ReEvaluateBy = secondRecipientState == DeliveryState.Held
                                    ? now.AddHours(1)
                                    : null,
                            },
                        ],
                ],
                HopCount = 0,
            },
            CancellationToken.None);

        Assert.True(accepted.IsAccepted, accepted.Detail);

        if (admission != state)
        {
            ForceState(host, accepted.QueueId!, state, now);
        }

        return accepted.QueueId!;
    }

    /// <summary>Writes a disposition the accept path cannot admit directly. Read-model fixture only.</summary>
    private static void ForceState(
        TestHost host,
        string queueId,
        DeliveryState state,
        DateTimeOffset at)
    {
        using var connection = host.Services.GetRequiredService<HostDatabase>().Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE queue_item SET state = $state WHERE queue_id = $id;
            UPDATE queue_recipient
               SET state = $state,
                   delivered_at = CASE WHEN $state = $delivered THEN $at ELSE NULL END,
                   review_deadline = CASE WHEN $state = $review THEN $deadline ELSE NULL END
             WHERE queue_id = $id;
            """;
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$delivered", (int)DeliveryState.Delivered);
        command.Parameters.AddWithValue("$review", (int)DeliveryState.Review);
        command.Parameters.AddWithValue("$deadline", at.AddDays(1).ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$at", at.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$id", queueId);
        command.ExecuteNonQuery();
    }

    private static Task RecordAsync(
        TestHost host,
        string tenantId,
        string assessmentId,
        MailAction action,
        DateTimeOffset at)
        => host.Services.GetRequiredService<IDecisionLedger>().RecordAsync(
            new MailAssessment
            {
                AssessmentId = assessmentId,
                InternalMessageId = $"message-{assessmentId}",
                TenantId = tenantId,
                Channel = ChannelContext.Email,
                Evidence = [],
                RiskDimensions = [],
                RiskIndex = 0,
                RiskIndexDenominator = 0,
                CoveredWeightFraction = 0,
                Action = action,
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
            },
            CancellationToken.None);

    private static void InsertProfile(
        TestHost host,
        string tenantId,
        string scope,
        string key,
        MailDirection? direction,
        int attempts,
        DateTimeOffset lastObservedAt)
    {
        using var connection = host.Services.GetRequiredService<HostDatabase>().Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO profiles
                (tenant_id, profile_scope, profile_key, direction,
                 observed_attempts, last_observed_at, updated_at)
            VALUES ($tenant, $scope, $key, $direction, $attempts, $last, $updated);
            """;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue(
            "$direction", direction is null ? DBNull.Value : (int)direction.Value);
        command.Parameters.AddWithValue("$attempts", attempts);
        command.Parameters.AddWithValue(
            "$last", lastObservedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue(
            "$updated", lastObservedAt.ToUniversalTime().ToString("O"));
        command.ExecuteNonQuery();
    }

    private static List<string> Plan(TestHost host, string sql, Action<SqliteCommand> bind)
    {
        using var connection = host.Services.GetRequiredService<HostDatabase>().Open();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        bind(command);

        using var reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
        {
            details.Add(reader.GetString(3));
        }

        return details;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize<T>(body, Json)
            ?? throw new InvalidOperationException($"The response did not deserialise as {typeof(T).Name}.");
    }
}
