using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StyloMail.AccessProxy.Hosting;
using StyloMail.Host.Contracts;
using StyloMail.Host.Endpoints;
using StyloMail.Host.Proxy;
using StyloMail.Host.Serialization;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Tests;

public sealed class ProxyImageObservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private const string ThreadA = "thread-imageAB";
    private const string ThreadB = "thread-imageBB";
    private const string Account = "pa_account";

    // ---------------------------------------------------------------------------------------------
    // Epoch, identity, watermark
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task EpochStartsAtOneAndIncrementsOnRestart()
    {
        using var fixture = new StoreFixture();
        Assert.True(await fixture.Store.InitializeEpochAsync());
        Assert.Equal(1, fixture.Store.CurrentEpoch);

        var restarted = new SqliteProxyImageStore(fixture.Database, fixture.Options);
        Assert.True(await restarted.InitializeEpochAsync());
        Assert.Equal(2, restarted.CurrentEpoch);
    }

    [Fact]
    public async Task MissingEpochAfterMigrationFailsClosedEvenWithNoOutcomes()
    {
        using var fixture = new StoreFixture();
        using (var connection = fixture.Database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM proxy_image_epoch;";
            command.ExecuteNonQuery();
        }

        Assert.False(await fixture.Store.InitializeEpochAsync());
        Assert.False(fixture.Store.EpochAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Store.RecordAsync(Observation("k-missing", "m-missing", ThreadA, Now), CancellationToken.None));
        Assert.Equal(1, fixture.Store.EpochUnavailableDrops);
    }

    [Fact]
    public async Task ExactNoOpAdvancesTheWatermarkAndRejectsAnOlderAttempt()
    {
        using var fixture = new StoreFixture();
        Assert.True(await fixture.Store.InitializeEpochAsync());
        await fixture.SeedMessageAsync("m-watermark", ThreadA, Now.AddMinutes(-5));

        await fixture.Store.RecordAsync(
            Observation("k-watermark", "m-watermark", ThreadA, Now.AddMinutes(-5), sequence: 1, digest: "A"),
            CancellationToken.None);
        var usageAfterFirst = fixture.UsageBytes();

        await fixture.Store.RecordAsync(
            Observation("k-watermark", "m-watermark", ThreadA, Now.AddMinutes(-4), sequence: 3, digest: "A"),
            CancellationToken.None);
        Assert.Equal(usageAfterFirst, fixture.UsageBytes());
        Assert.Equal((1L, 3L), fixture.Watermark("k-watermark"));

        await fixture.Store.RecordAsync(
            Observation("k-watermark", "m-watermark", ThreadA, Now.AddMinutes(-3), sequence: 2, digest: "B"),
            CancellationToken.None);
        Assert.Equal((1L, 3L), fixture.Watermark("k-watermark"));
        Assert.Equal(usageAfterFirst, fixture.UsageBytes());
        Assert.Equal(1, fixture.Store.StaleObservationDrops);
    }

    [Fact]
    public async Task DifferentContentUnderTheSameAssociationDoesNotOverwrite()
    {
        using var fixture = new StoreFixture();
        Assert.True(await fixture.Store.InitializeEpochAsync());
        await fixture.SeedMessageAsync("m-collide", ThreadA, Now.AddMinutes(-5));

        await fixture.Store.RecordAsync(
            Observation("k-one", "m-collide", ThreadA, Now.AddMinutes(-5), sequence: 1, digest: "A"),
            CancellationToken.None);
        await fixture.Store.RecordAsync(
            Observation("k-two", "m-collide", ThreadA, Now.AddMinutes(-4), sequence: 2, digest: "B"),
            CancellationToken.None);

        var page = await fixture.Store.ListAsync(
            TestPrincipals.AcmeTenant, ThreadA, Now.AddHours(-1), Now, 50, null, Now, CancellationToken.None);
        var parent = Assert.Single(page.Messages);
        Assert.Equal(2, parent.Observations.Count);
        Assert.Null(parent.ChildrenNext);
    }

    // ---------------------------------------------------------------------------------------------
    // Capacity, expiry, marker state
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CapacityRefusesBeforeGrowth()
    {
        using var fixture = new StoreFixture(maxOutcomes: 1);
        Assert.True(await fixture.Store.InitializeEpochAsync());
        await fixture.SeedMessageAsync("m-cap", ThreadA, Now.AddMinutes(-5));

        await fixture.Store.RecordAsync(
            Observation("k-cap-1", "m-cap", ThreadA, Now.AddMinutes(-5)), CancellationToken.None);
        var bytes = fixture.UsageBytes();

        await fixture.Store.RecordAsync(
            Observation("k-cap-2", "m-cap", ThreadA, Now.AddMinutes(-4)), CancellationToken.None);

        Assert.Equal(1, fixture.Store.CapacityRefusals);
        Assert.Equal(bytes, fixture.UsageBytes());
        Assert.Equal(1L, fixture.Count("proxy_image_outcome"));
    }

    [Fact]
    public async Task DueRowsAreNotServedAndSweepRestoresUsage()
    {
        using var fixture = new StoreFixture(retentionDays: 1);
        Assert.True(await fixture.Store.InitializeEpochAsync());
        await fixture.SeedMessageAsync("m-expire", ThreadA, Now.AddDays(-2));
        await fixture.Store.RecordAsync(
            Observation("k-expire", "m-expire", ThreadA, Now.AddDays(-2)), CancellationToken.None);

        var page = await fixture.Store.ListAsync(
            TestPrincipals.AcmeTenant, ThreadA, Now.AddDays(-3), Now, 50, null, Now, CancellationToken.None);
        var parent = Assert.Single(page.Messages);
        Assert.Empty(parent.Observations);

        var deleted = await fixture.Store.DeleteExpiredAsync(Now, 1000, CancellationToken.None);
        Assert.Equal(1, deleted);
        Assert.Equal(0L, fixture.Count("proxy_image_outcome"));
        Assert.Equal(0L, fixture.UsageBytes());
    }

    [Fact]
    public async Task StorageErrorObservationIsNotProduced()
    {
        using var fixture = new StoreFixture();
        Assert.True(await fixture.Store.InitializeEpochAsync());
        await fixture.SeedMessageAsync("m-error", ThreadA, Now.AddMinutes(-5));
        var observation = Observation("k-error", "m-error", ThreadA, Now.AddMinutes(-5)) with
        {
            Availability = ProxyImageAvailability.Unavailable,
            AvailabilityReason = ProxyImageReason.StorageError,
            ImageParts = 0,
            ImagesSkippedAsChrome = 0,
            ImagesAdmitted = 0,
            ImagesProduced = 0,
            ImagesNotProduced = 0,
            ImagesOverBudget = 0,
            EvidenceRowCount = 0,
            Parts = [],
        };
        await fixture.Store.RecordAsync(observation, CancellationToken.None);

        var page = await fixture.Store.ListAsync(
            TestPrincipals.AcmeTenant, ThreadA, Now.AddHours(-1), Now, 50, null, Now, CancellationToken.None);
        var child = Assert.Single(Assert.Single(page.Messages).Observations);
        Assert.Equal(ProxyImageReason.StorageError, child.AvailabilityReason);
    }

    // ---------------------------------------------------------------------------------------------
    // Route states, children, disabled
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task RouteReportsExhaustiveStatesAndBoundedChildren()
    {
        using var host = ImageHost(imagesEnabled: true);
        var sink = host.Services.GetRequiredService<IProxyImageObservationSink>();
        await SeedAsync(host, "m-clean", ThreadA, Now.AddMinutes(-6));
        await SeedAsync(host, "m-findings", ThreadA, Now.AddMinutes(-5));
        await SeedAsync(host, "m-partial", ThreadA, Now.AddMinutes(-4));
        await SeedAsync(host, "m-noimage", ThreadA, Now.AddMinutes(-3));
        await SeedAsync(host, "m-chrome", ThreadA, Now.AddMinutes(-2));
        await SeedAsync(host, "m-notproduced", ThreadA, Now.AddMinutes(-1));
        await SeedAsync(host, "m-noobs", ThreadA, Now.AddSeconds(-30));

        await sink.RecordAsync(Observation("k-clean", "m-clean", ThreadA, Now.AddMinutes(-6), findings: false), CancellationToken.None);
        await sink.RecordAsync(Observation("k-findings", "m-findings", ThreadA, Now.AddMinutes(-5), findings: true), CancellationToken.None);
        await sink.RecordAsync(Partial("k-partial", "m-partial", ThreadA, Now.AddMinutes(-4)), CancellationToken.None);
        await sink.RecordAsync(NoImages("k-noimage", "m-noimage", ThreadA, Now.AddMinutes(-3)), CancellationToken.None);
        await sink.RecordAsync(AllChrome("k-chrome", "m-chrome", ThreadA, Now.AddMinutes(-2)), CancellationToken.None);
        await sink.RecordAsync(NotProduced("k-notproduced", "m-notproduced", ThreadA, Now.AddMinutes(-1)), CancellationToken.None);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var response = await ReadAsync<ProxyConversationImagesResponse>(
            await reviewer.GetAsync($"/v1/proxy/conversations/{ThreadA}/images?window=last_24_hours"));

        Assert.True(response.ImagesEnabled);
        Assert.Equal(7, response.Messages.Count);
        Assert.Equal("clean", Assert.Single(response.Messages, m => m.MessageToken == "m-clean").Observations[0].State);
        Assert.Equal("findings", Assert.Single(response.Messages, m => m.MessageToken == "m-findings").Observations[0].State);
        Assert.Equal("partial", Assert.Single(response.Messages, m => m.MessageToken == "m-partial").Observations[0].State);
        Assert.Equal("not_applicable", Assert.Single(response.Messages, m => m.MessageToken == "m-noimage").Observations[0].State);
        Assert.Equal("all_chrome", Assert.Single(response.Messages, m => m.MessageToken == "m-chrome").Observations[0].State);
        Assert.Equal("not_produced", Assert.Single(response.Messages, m => m.MessageToken == "m-notproduced").Observations[0].State);
        Assert.Equal("no_observation", Assert.Single(response.Messages, m => m.MessageToken == "m-noobs").State);
    }

    [Fact]
    public async Task ChildrenOverflowAndChildCursorAreExplicit()
    {
        using var host = ImageHost(imagesEnabled: true);
        var sink = host.Services.GetRequiredService<IProxyImageObservationSink>();
        await SeedAsync(host, "m-many", ThreadA, Now.AddMinutes(-5));
        for (var index = 0; index < 9; index++)
        {
            await sink.RecordAsync(
                Observation($"k-many-{index}", "m-many", ThreadA, Now.AddMinutes(-5), sequence: index + 1,
                    findings: false), CancellationToken.None);
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var first = await ReadAsync<ProxyConversationImagesResponse>(
            await reviewer.GetAsync($"/v1/proxy/conversations/{ThreadA}/images?window=last_24_hours"));
        var parent = Assert.Single(first.Messages);
        Assert.Equal(8, parent.Observations.Count);
        Assert.Equal(1, parent.ChildrenOverflowCount);
        Assert.NotNull(parent.ChildrenNext);

        var second = await ReadAsync<ProxyImageChildrenResponse>(
            await reviewer.GetAsync(
                $"/v1/proxy/conversations/{ThreadA}/images?window=last_24_hours&childAfter={Uri.EscapeDataString(parent.ChildrenNext!)}&account={parent.AccountId}&message={parent.MessageToken}"));
        Assert.Single(second.Observations);
    }

    [Fact]
    public async Task ChildJoinIsThreadScoped()
    {
        using var host = ImageHost(imagesEnabled: true);
        var sink = host.Services.GetRequiredService<IProxyImageObservationSink>();
        await SeedAsync(host, "m-same-token", ThreadA, Now.AddMinutes(-5));
        await SeedAsync(host, "m-same-token", ThreadB, Now.AddMinutes(-4));
        await sink.RecordAsync(Observation("k-thread-a", "m-same-token", ThreadA, Now.AddMinutes(-5)), CancellationToken.None);
        await sink.RecordAsync(Observation("k-thread-b", "m-same-token", ThreadB, Now.AddMinutes(-4)), CancellationToken.None);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var a = await ReadAsync<ProxyConversationImagesResponse>(
            await reviewer.GetAsync($"/v1/proxy/conversations/{ThreadA}/images?window=last_24_hours"));
        var b = await ReadAsync<ProxyConversationImagesResponse>(
            await reviewer.GetAsync($"/v1/proxy/conversations/{ThreadB}/images?window=last_24_hours"));

        Assert.Equal("k-thread-a", Assert.Single(Assert.Single(a.Messages).Observations).ObservationKey);
        Assert.Equal("k-thread-b", Assert.Single(Assert.Single(b.Messages).Observations).ObservationKey);
    }

    [Fact]
    public async Task DisabledImagesReportGlobalStateAndWriteNoRows()
    {
        using var host = ImageHost(imagesEnabled: false);
        await SeedAsync(host, "m-disabled", ThreadA, Now.AddMinutes(-5));

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var response = await ReadAsync<ProxyConversationImagesResponse>(
            await reviewer.GetAsync($"/v1/proxy/conversations/{ThreadA}/images?window=last_24_hours"));

        Assert.False(response.ImagesEnabled);
        Assert.Equal("disabled", Assert.Single(response.Messages).State);
        using var connection = host.Services.GetRequiredService<HostDatabase>().Open();
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM proxy_image_outcome;"));
    }

    [Fact]
    public async Task CrossTenantReadIsAbsent()
    {
        using var host = ImageHost(imagesEnabled: true);
        await SeedAsync(host, "m-tenant", ThreadA, Now.AddMinutes(-5));

        using var globex = host.ClientAs(TestPrincipals.GlobexReviewerKey);
        Assert.Equal(HttpStatusCode.NotFound,
            (await globex.GetAsync($"/v1/proxy/conversations/{ThreadA}/images?window=last_24_hours")).StatusCode);
    }

    [Fact]
    public async Task ByteCapRefusesBeforeGrowth()
    {
        var probe = Observation("k-probe", "m-probe", ThreadA, Now.AddMinutes(-5)) with { RetentionDays = 30 };
        var usage = SqliteProxyImageStore.UsageBytes(probe);
        using var fixture = new StoreFixture(maxUsedBytes: usage + 1);
        Assert.True(await fixture.Store.InitializeEpochAsync());
        await fixture.SeedMessageAsync("m-byte", ThreadA, Now.AddMinutes(-5));

        await fixture.Store.RecordAsync(
            Observation("k-byte-1", "m-byte", ThreadA, Now.AddMinutes(-5)), CancellationToken.None);
        var before = fixture.UsageBytes();

        await fixture.Store.RecordAsync(
            Observation("k-byte-2", "m-byte", ThreadA, Now.AddMinutes(-4)), CancellationToken.None);

        Assert.Equal(1, fixture.Store.CapacityRefusals);
        Assert.Equal(before, fixture.UsageBytes());
        Assert.Equal(1L, fixture.Count("proxy_image_outcome"));
    }

    [Fact]
    public async Task SweepRotatesAcrossSixTenantsWithContinuation()
    {
        using var fixture = new StoreFixture(retentionDays: 1);
        Assert.True(await fixture.Store.InitializeEpochAsync());
        for (var index = 1; index <= 6; index++)
        {
            var tenant = "tenant" + index;
            var observation = Observation($"k-sweep-{index}", $"m-sweep-{index}", ThreadA, Now.AddDays(-2)) with
            {
                TenantId = tenant,
                AccountId = "account" + index,
            };
            await fixture.Store.RecordAsync(observation, CancellationToken.None);
        }

        for (var pass = 0; pass < 6; pass++)
        {
            var deleted = await fixture.Store.DeleteExpiredAsync(Now, 1, CancellationToken.None);
            Assert.Equal(1, deleted);
        }

        Assert.Equal(0L, fixture.Count("proxy_image_outcome"));
        using var connection = fixture.Database.Open();
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM proxy_image_tenant_usage WHERE outcomes <> 0;"));
    }

    [Fact]
    public async Task EnsureCreatedDoesNotReseedAMissingEpochAfterTheMarker()
    {
        using var fixture = new StoreFixture();
        Assert.True(await fixture.Store.InitializeEpochAsync());
        using (var connection = fixture.Database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM proxy_image_epoch;";
            command.ExecuteNonQuery();
        }

        fixture.Database.EnsureCreated();
        Assert.False(await fixture.Store.InitializeEpochAsync());
    }

    [Fact]
    public async Task ReplacementShrinkReturnsUsageAndSweepCanZeroIt()
    {
        using var fixture = new StoreFixture(retentionDays: 1);
        Assert.True(await fixture.Store.InitializeEpochAsync());
        await fixture.SeedMessageAsync("m-shrink", ThreadA, Now.AddDays(-2));
        var larger = Observation("k-shrink", "m-shrink", ThreadA, Now.AddDays(-2), partCount: 2);
        await fixture.Store.RecordAsync(larger, CancellationToken.None);
        var largeUsage = fixture.UsageBytes();

        var smaller = Observation("k-shrink", "m-shrink", ThreadA, Now.AddDays(-2), sequence: 2, digest: "B") with
        {
            ImageParts = 1,
            ImagesAdmitted = 1,
            ImagesProduced = 1,
            Parts = [Observation("k-shrink", "m-shrink", ThreadA, Now.AddDays(-2)).Parts[0] with { Findings = [] }],
        };
        await fixture.Store.RecordAsync(smaller, CancellationToken.None);

        Assert.True(fixture.UsageBytes() < largeUsage);
        var deleted = await fixture.Store.DeleteExpiredAsync(Now, 1000, CancellationToken.None);
        Assert.Equal(1, deleted);
        Assert.Equal(0L, fixture.UsageBytes());
    }

    [Fact]
    public async Task SweepWrapsAndDoesNotStarveEarlierTenants()
    {
        using var fixture = new StoreFixture(retentionDays: 1);
        Assert.True(await fixture.Store.InitializeEpochAsync());
        foreach (var tenant in new[] { "tenant-a", "tenant-b" })
        {
            await fixture.Store.RecordAsync(
                Observation($"k-{tenant}", $"m-{tenant}", ThreadA, Now.AddDays(-2)) with
                {
                    TenantId = tenant,
                    AccountId = "account" + tenant,
                }, CancellationToken.None);
        }

        Assert.Equal(1, await fixture.Store.DeleteExpiredAsync(Now, 1, CancellationToken.None));
        Assert.Equal(1, await fixture.Store.DeleteExpiredAsync(Now, 1, CancellationToken.None));

        await fixture.Store.RecordAsync(
            Observation("k-tenant-a2", "m-tenant-a2", ThreadA, Now.AddDays(-2)) with
            {
                TenantId = "tenant-a",
                AccountId = "accounta",
            }, CancellationToken.None);
        Assert.Equal(1, await fixture.Store.DeleteExpiredAsync(Now, 1, CancellationToken.None));
        Assert.Equal(0L, fixture.Count("proxy_image_outcome"));
    }

    [Fact]
    public async Task ParentCursorKeepsTheFixedWindowAcrossPages()
    {
        using var host = ImageHost(imagesEnabled: true);
        await SeedAsync(host, "m-page-1", ThreadA, Now.AddMinutes(-5));
        await SeedAsync(host, "m-page-2", ThreadA, Now.AddMinutes(-4));

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var first = await ReadAsync<ProxyConversationImagesResponse>(
            await reviewer.GetAsync($"/v1/proxy/conversations/{ThreadA}/images?window=last_24_hours&limit=1"));
        Assert.NotNull(first.Next);

        var second = await ReadAsync<ProxyConversationImagesResponse>(
            await reviewer.GetAsync($"/v1/proxy/conversations/{ThreadA}/images?window=last_24_hours&limit=1&after={Uri.EscapeDataString(first.Next!)}"));
        Assert.Equal(first.WindowStart, second.WindowStart);
        Assert.Equal(first.WindowEnd, second.WindowEnd);
    }

    [Fact]
    public async Task ParentCursorKeepsTheWindowWhenTheClockMoves()
    {
        var clock = new MutableTimeProvider(Now);
        using var host = ImageHost(imagesEnabled: true, clock: clock);
        await SeedAsync(host, "m-clock-1", ThreadA, Now.AddMinutes(-5));
        await SeedAsync(host, "m-clock-2", ThreadA, Now.AddMinutes(-4));

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var first = await ReadAsync<ProxyConversationImagesResponse>(
            await reviewer.GetAsync($"/v1/proxy/conversations/{ThreadA}/images?window=last_24_hours&limit=1"));
        Assert.NotNull(first.Next);

        clock.Advance(TimeSpan.FromHours(2));
        var second = await ReadAsync<ProxyConversationImagesResponse>(
            await reviewer.GetAsync($"/v1/proxy/conversations/{ThreadA}/images?window=last_24_hours&limit=1&after={Uri.EscapeDataString(first.Next!)}"));
        Assert.Equal(first.WindowStart, second.WindowStart);
        Assert.Equal(first.WindowEnd, second.WindowEnd);
    }

    [Fact]
    public async Task StorageErrorMarkerIsReplacedByALaterHealthyRetry()
    {
        using var fixture = new StoreFixture();
        Assert.True(await fixture.Store.InitializeEpochAsync());
        await fixture.SeedMessageAsync("m-retry", ThreadA, Now.AddMinutes(-5));
        fixture.Execute(
            "CREATE TRIGGER fail_success BEFORE INSERT ON proxy_image_outcome "
            + "WHEN NEW.observation_key='k-retry' AND NEW.result_digest <> 'storage-error-v1' "
            + "BEGIN SELECT RAISE(ABORT, 'simulated'); END;");

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(async () =>
            await fixture.Store.RecordAsync(
                Observation("k-retry", "m-retry", ThreadA, Now.AddMinutes(-5), sequence: 1, digest: "A"),
                CancellationToken.None));

        using (var connection = fixture.Database.Open())
        {
            using var read = connection.CreateCommand();
            read.CommandText =
                "SELECT availability_reason,result_digest FROM proxy_image_outcome WHERE observation_key='k-retry';";
            using var reader = read.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("StorageError", reader.GetString(0));
            Assert.Equal("storage-error-v1", reader.GetString(1));
        }

        fixture.Execute("DROP TRIGGER fail_success;");
        await fixture.Store.RecordAsync(
            Observation("k-retry", "m-retry", ThreadA, Now.AddMinutes(-4), sequence: 2, digest: "A"),
            CancellationToken.None);

        var page = await fixture.Store.ListAsync(
            TestPrincipals.AcmeTenant, ThreadA, Now.AddHours(-1), Now, 50, null, Now, CancellationToken.None);
        var child = Assert.Single(Assert.Single(page.Messages).Observations);
        Assert.Equal(ProxyImageAvailability.Available, child.Availability);
        Assert.Equal("A", child.ResultDigest);
    }

    [Fact]
    public async Task MissingMarkerWithOutcomeHistoryFailsClosedAndDoesNotSeed()
    {
        using var fixture = new StoreFixture();
        Assert.True(await fixture.Store.InitializeEpochAsync());
        await fixture.SeedMessageAsync("m-history", ThreadA, Now.AddMinutes(-5));
        await fixture.Store.RecordAsync(
            Observation("k-history", "m-history", ThreadA, Now.AddMinutes(-5)), CancellationToken.None);

        fixture.Execute("DELETE FROM proxy_image_epoch; DELETE FROM host_schema_migration WHERE name='proxy-image-v1';");
        fixture.Database.EnsureCreated();

        Assert.Equal(0L, fixture.Count("proxy_image_epoch"));
        Assert.Equal(0L, fixture.Count("host_schema_migration WHERE name='proxy-image-v1'")); 
        Assert.False(await fixture.Store.InitializeEpochAsync());
    }

    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("-1")]
    public async Task ExtremeCursorTicksReturnInvalidCursor(string ticks)
    {
        using var host = ImageHost(imagesEnabled: true);
        await SeedAsync(host, "m-cursor", ThreadA, Now.AddMinutes(-5));
        var key = ticks + "|" + ticks + "|1|" + Account + "|m-cursor";
        var encoded = ProxyCursors.Encode(TestPrincipals.AcmeTenant,
            "conversation-images:" + ThreadA, 50, key);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var response = await reviewer.GetAsync(
            $"/v1/proxy/conversations/{ThreadA}/images?window=last_24_hours&after={Uri.EscapeDataString(encoded)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_cursor", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CanceledWriteCreatesNoRowOrFailureMarker()
    {
        using var fixture = new StoreFixture();
        Assert.True(await fixture.Store.InitializeEpochAsync());
        await fixture.SeedMessageAsync("m-cancel", ThreadA, Now.AddMinutes(-5));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Store.RecordAsync(
                Observation("k-cancel", "m-cancel", ThreadA, Now.AddMinutes(-5)),
                cancellation.Token));

        Assert.Equal(0L, fixture.Count("proxy_image_outcome"));
        Assert.Equal(0L, fixture.UsageBytes());
    }

    [Fact]
    public async Task MidTransactionCancellationRollsBackEverything()
    {
        using var fixture = new StoreFixture();
        Assert.True(await fixture.Store.InitializeEpochAsync());
        await fixture.SeedMessageAsync("m-mid", ThreadA, Now.AddMinutes(-5));
        using var cancellation = new CancellationTokenSource();
        fixture.Store.BeforePartInsert = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Store.RecordAsync(
                Observation("k-mid", "m-mid", ThreadA, Now.AddMinutes(-5)),
                cancellation.Token));

        Assert.Equal(0L, fixture.Count("proxy_image_outcome"));
        Assert.Equal(0L, fixture.Count("proxy_image_part"));
        Assert.Equal(0L, fixture.UsageBytes());
    }

    [Fact]
    public async Task NoOpWatermarkCancellationLeavesTheOldWatermark()
    {
        using var fixture = new StoreFixture();
        Assert.True(await fixture.Store.InitializeEpochAsync());
        await fixture.SeedMessageAsync("m-noop-cancel", ThreadA, Now.AddMinutes(-5));
        await fixture.Store.RecordAsync(
            Observation("k-noop-cancel", "m-noop-cancel", ThreadA, Now.AddMinutes(-5), sequence: 1, digest: "A"),
            CancellationToken.None);
        var before = fixture.UsageBytes();
        using var cancellation = new CancellationTokenSource();
        fixture.Store.BeforeCommit = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Store.RecordAsync(
                Observation("k-noop-cancel", "m-noop-cancel", ThreadA, Now.AddMinutes(-4), sequence: 3, digest: "A"),
                cancellation.Token));

        Assert.Equal((1L, 1L), fixture.Watermark("k-noop-cancel"));
        Assert.Equal(before, fixture.UsageBytes());
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static TestHost ImageHost(bool imagesEnabled, TimeProvider? clock = null)
    {
        var host = new TestHost()
            .Configure("StyloMail:Proxy:Enabled", "true")
            .Configure("StyloMail:Images:Enabled", imagesEnabled ? "true" : "false")
            .Configure("StyloMail:Proxy:ImageObservationRetentionDays", "30");
        host.Override(services =>
        {
            services.RemoveAll<IProxySecretSource>();
            services.AddSingleton<IProxySecretSource>(new TestSecretSource());
            if (clock is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
            }
        });
        return host;
    }

    private static async Task SeedAsync(TestHost host, string messageToken, string thread, DateTimeOffset at)
    {
        var threading = host.Services.GetRequiredService<IProxyThreadingEventSink>();
        await threading.PublishAsync(
            new ProxyThreadingEvent
            {
                TenantId = TestPrincipals.AcmeTenant,
                AccountId = Account,
                Protocol = ProxyListenerProtocol.Imap,
                MessageToken = messageToken,
                ThreadToken = thread,
                ReferenceDepth = 1,
                Timestamp = at,
            }, CancellationToken.None);

        var warnings = host.Services.GetRequiredService<IProxyWarningEventSink>();
        await warnings.RecordOutcomeAsync(
            new ProxyWarningAnalysisOutcome
            {
                TenantId = TestPrincipals.AcmeTenant,
                AccountId = Account,
                TrafficKind = MessageTrafficKind.Retrieval,
                MessageToken = messageToken,
                ThreadToken = thread,
                ObservedAt = at,
                Availability = ProxyWarningAvailability.Available,
                AvailabilityReason = ProxyWarningAvailabilityReason.None,
                MarkerCount = 0,
            }, CancellationToken.None);
    }

    private static ProxyImageObservation Observation(
        string key,
        string message,
        string thread,
        DateTimeOffset at,
        long sequence = 1,
        string digest = "A",
        bool findings = true,
        int partCount = 1) => new()
    {
        TenantId = TestPrincipals.AcmeTenant,
        AccountId = Account,
        MessageToken = message,
        ThreadToken = thread,
        TrafficKind = MessageTrafficKind.Retrieval,
        ObservationKey = key,
        ContentFingerprint = "fp-" + key,
        ObservationEpoch = 1,
        ObservationSequence = sequence,
        ResultDigest = digest,
        ObservedAt = at,
        Availability = ProxyImageAvailability.Available,
        AvailabilityReason = ProxyImageReason.None,
        ImageParts = partCount,
        ImagesSkippedAsChrome = 0,
        ImagesAdmitted = partCount,
        ImagesProduced = partCount,
        ImagesNotProduced = 0,
        ImagesOverBudget = 0,
        EvidenceRowCount = partCount * 5,
        Parts = Enumerable.Range(0, partCount)
            .Select(index => new ProxyImagePartObservation
            {
                Ordinal = 3 + index,
                Disposition = ProxyImageDisposition.Inspected,
                Availability = ProxyImageAvailability.Available,
                AvailabilityReason = ProxyImageReason.None,
                Format = "png",
                DeclaredFormat = "png",
                Width = 200,
                Height = 200,
                PixelCount = 40_000,
                ImageEndOffset = 1_000,
                TrailingByteCount = 24,
                TrailingScanComplete = true,
                TransparencyDeclared = false,
                Findings = findings
                    ? [new ProxyImageFinding { Kind = ProxyImageFindingKind.TrailingData, Offset = 85, Detail = "bytes=24" }]
                    : [],
                SourceVersion = "stylomail-image/2",
            })
            .ToArray(),
        SourceVersion = "stylomail-image/2",
    };

    private static ProxyImageObservation Partial(string key, string message, string thread, DateTimeOffset at) =>
        Observation(key, message, thread, at) with
        {
            ImageParts = 2,
            ImagesAdmitted = 2,
            ImagesProduced = 1,
            ImagesNotProduced = 1,
            Parts =
            [
                Observation(key, message, thread, at).Parts[0],
                new ProxyImagePartObservation
                {
                    Ordinal = 4,
                    Disposition = ProxyImageDisposition.NotProduced,
                    Availability = ProxyImageAvailability.Unavailable,
                    AvailabilityReason = ProxyImageReason.InsufficientDecodedLength,
                    Format = "unknown",
                    DeclaredFormat = "png",
                    Findings = [],
                    SourceVersion = "stylomail-image/2",
                },
            ],
        };

    private static ProxyImageObservation NoImages(string key, string message, string thread, DateTimeOffset at) =>
        Observation(key, message, thread, at) with
        {
            ImageParts = 0,
            ImagesSkippedAsChrome = 0,
            ImagesAdmitted = 0,
            ImagesProduced = 0,
            ImagesNotProduced = 0,
            ImagesOverBudget = 0,
            Availability = ProxyImageAvailability.NotApplicable,
            EvidenceRowCount = 1,
            Parts = [],
        };

    private static ProxyImageObservation AllChrome(string key, string message, string thread, DateTimeOffset at) =>
        Observation(key, message, thread, at) with
        {
            ImageParts = 2,
            ImagesSkippedAsChrome = 2,
            ImagesAdmitted = 0,
            ImagesProduced = 0,
            ImagesNotProduced = 0,
            ImagesOverBudget = 0,
            EvidenceRowCount = 1,
            Parts = [],
        };

    private static ProxyImageObservation NotProduced(string key, string message, string thread, DateTimeOffset at) =>
        Observation(key, message, thread, at) with
        {
            Availability = ProxyImageAvailability.Unavailable,
            AvailabilityReason = ProxyImageReason.ImagesNotProduced,
            ImagesProduced = 0,
            ImagesNotProduced = 1,
            EvidenceRowCount = 1,
            Parts =
            [
                new ProxyImagePartObservation
                {
                    Ordinal = 3,
                    Disposition = ProxyImageDisposition.NotProduced,
                    Availability = ProxyImageAvailability.Unavailable,
                    AvailabilityReason = ProxyImageReason.FormatMismatch,
                    Format = "unknown",
                    DeclaredFormat = "png",
                    Findings = [],
                    SourceVersion = "stylomail-image/2",
                },
            ],
        };

    private static long Scalar(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        return JsonSerializer.Deserialize<T>(raw, HostJson.Options)!;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class TestSecretSource : IProxySecretSource
    {
        public byte[] GetLoginKey() => System.Text.Encoding.UTF8.GetBytes("fixture-login-key-32-bytes-long!!");

        public byte[] GetProtectionKey() => System.Text.Encoding.UTF8.GetBytes("fixture-protection-key-32-bytes!");
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly string _directory;

        public StoreFixture(
            int retentionDays = 30,
            int maxOutcomes = SqliteProxyImageStore.DefaultMaximumOutcomesPerTenant,
            long maxUsedBytes = SqliteProxyImageStore.DefaultMaximumUsedBytesPerTenant)
        {
            _directory = Path.Combine(Path.GetTempPath(), "stylomail-image-store", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Database = new HostDatabase(global::Microsoft.Extensions.Options.Options.Create(new HostStorageOptions
            {
                DatabasePath = Path.Combine(_directory, "host.db"),
            }));
            Database.EnsureCreated();
            Options = global::Microsoft.Extensions.Options.Options.Create(new HostProxyOptions
            {
                ImageObservationRetentionDays = retentionDays,
                ImageObservationMaxOutcomesPerTenant = maxOutcomes,
                ImageObservationMaxUsedBytesPerTenant = maxUsedBytes,
            });
            Store = new SqliteProxyImageStore(Database, Options);
            Events = new SqliteProxyEventStore(Database);
        }

        public HostDatabase Database { get; }

        public IOptions<HostProxyOptions> Options { get; }

        public SqliteProxyImageStore Store { get; }

        public SqliteProxyEventStore Events { get; }

        public async Task SeedMessageAsync(string message, string thread, DateTimeOffset at)
        {
            await Events.RecordOutcomeAsync(
                new ProxyWarningAnalysisOutcome
                {
                    TenantId = TestPrincipals.AcmeTenant,
                    AccountId = Account,
                    TrafficKind = MessageTrafficKind.Retrieval,
                    MessageToken = message,
                    ThreadToken = thread,
                    ObservedAt = at,
                    Availability = ProxyWarningAvailability.Available,
                    AvailabilityReason = ProxyWarningAvailabilityReason.None,
                    MarkerCount = 0,
                }, CancellationToken.None);
        }

        public long UsageBytes()
        {
            using var connection = Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT used_bytes FROM proxy_image_tenant_usage WHERE tenant_id=$tenant;";
            command.Parameters.AddWithValue("$tenant", TestPrincipals.AcmeTenant);
            return command.ExecuteScalar() is long value ? value : 0;
        }

        public (long Epoch, long Sequence) Watermark(string key)
        {
            using var connection = Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT accepted_epoch,accepted_sequence FROM proxy_image_outcome "
                + "WHERE tenant_id=$tenant AND account_id=$account AND direction='Retrieval' AND observation_key=$key;";
            command.Parameters.AddWithValue("$tenant", TestPrincipals.AcmeTenant);
            command.Parameters.AddWithValue("$account", Account);
            command.Parameters.AddWithValue("$key", key);
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : (0, 0);
        }

        public long Count(string table)
        {
            using var connection = Database.Open();
            return Scalar(connection, "SELECT COUNT(*) FROM " + table + ";");
        }

        public void Execute(string sql)
        {
            using var connection = Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
