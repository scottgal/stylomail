using System.Text;
using StyloMail.Core;

namespace StyloMail.Queue.Tests;

/// <summary>
/// The acceptance boundary: an SMTP <c>250</c> after <c>DATA</c> transfers delivery responsibility,
/// so nothing here may report success unless the message is genuinely durable.
/// </summary>
public class QueueAcceptanceTests
{
    [Fact]
    public async Task Acceptance_is_refused_when_the_spool_cannot_write()
    {
        using var h = new QueueHarness();

        // A regular file exactly where the tenant's spool directory must be created. Every write
        // beneath it fails the way a full, read-only, or unmounted volume fails.
        await File.WriteAllTextAsync(Path.Combine(h.SpoolRoot, "acme"), "not a directory");

        await Assert.ThrowsAsync<SpoolUnavailableException>(
            () => h.Store.AcceptAsync(QueueHarness.Submission()));

        // The point of the test: no durable record exists, so the caller has nothing it could
        // legitimately answer 250 to. Disk full must never produce a successful acceptance.
        Assert.Empty(await h.Store.CountByStateAsync());
    }

    /// <remarks>
    /// Named for what it proves, not for the mechanism it is aimed at. The admission refusal it
    /// exercises happens after the payload was written, but nothing observable in this test shows
    /// that — a refusal raised before the write would leave the same empty file list. The claim
    /// "the payload is deleted after a refusal that happened post-spool" is pinned instead by
    /// <c>Concurrent_replays_of_one_key_produce_exactly_one_message</c>, which is the only path
    /// where a losing writer definitely spools before it is refused.
    /// </remarks>
    [Fact]
    public async Task A_refusal_does_not_leave_the_payload_behind()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            MaxQueuedItemsPerTenant = 1,
        });

        await h.AcceptAsync(QueueHarness.Submission());

        var refused = await h.Store.AcceptAsync(QueueHarness.Submission());

        Assert.Equal(QueueAdmission.RefusedTenantItemLimit, refused.Admission);
        Assert.False(refused.IsAccepted);

        // The payload is written before the quota is checked, so a refusal must clean it up:
        // otherwise a tenant could fill the disk with messages we never accepted.
        Assert.Single(Directory.GetFiles(h.SpoolRoot, "*.eml", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task The_payload_is_durable_before_the_metadata_that_references_it()
    {
        using var h = new QueueHarness();

        var queueId = await h.AcceptAsync(QueueHarness.Submission(payloadBytes: 256));

        var item = await h.Store.GetItemAsync(queueId);
        Assert.NotNull(item);
        Assert.True(h.Spool.Exists(item!.Envelope.PayloadReference));

        // The invariant, stated as the queue itself can check it.
        var recovery = await h.Store.RecoverAsync();
        Assert.Empty(recovery.MissingPayloads);
    }

    [Fact]
    public async Task A_crash_between_payload_and_metadata_leaves_a_sweepable_orphan_and_sound_metadata()
    {
        using var h = new QueueHarness();

        // Reconstruct the crash: the payload reached the disk, the metadata commit never ran.
        var orphan = await h.Spool.WriteAsync("acme", "q-crashed", Encoding.UTF8.GetBytes("never committed"));
        var orphanPath = h.PayloadPath("q-crashed");
        Assert.True(File.Exists(orphanPath));

        // Old enough to be past the acceptance critical section.
        File.SetLastWriteTimeUtc(orphanPath, QueueHarness.Start.UtcDateTime.AddHours(-5));

        var recovery = await h.Store.RecoverAsync();

        Assert.Contains(orphan, recovery.OrphanPayloads);
        Assert.False(File.Exists(orphanPath));

        // The property the write ordering exists to protect: the only state a crash can produce is
        // a payload nobody references, never metadata pointing at nothing.
        Assert.Empty(recovery.MissingPayloads);
    }

    [Fact]
    public async Task A_temporary_abandoned_by_a_crashed_acceptance_is_swept()
    {
        using var h = new QueueHarness();

        // What a crash between creating the temporary and renaming it leaves behind. This is a
        // *different* path from an unreferenced payload: nothing ever references a temporary, and
        // the payload that was being written never reached its final name.
        var tenantDirectory = Path.Combine(h.SpoolRoot, "acme");
        Directory.CreateDirectory(tenantDirectory);
        var abandoned = Path.Combine(tenantDirectory, $"q-crashed.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(abandoned, "half a message");

        File.SetLastWriteTimeUtc(abandoned, QueueHarness.Start.UtcDateTime.AddHours(-5));

        var recovery = await h.Store.RecoverAsync();

        Assert.False(File.Exists(abandoned));
        Assert.Contains(recovery.OrphanPayloads, p => p.EndsWith(".tmp", StringComparison.Ordinal));

        // And no debris is left anywhere under the spool.
        Assert.Empty(Directory.GetFiles(h.SpoolRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_payload_written_moments_ago_is_never_swept_as_an_orphan()
    {
        using var h = new QueueHarness();

        // Byte-for-byte indistinguishable from an in-flight acceptance: bytes on disk, no metadata
        // yet. Only the age tells them apart.
        await h.Spool.WriteAsync("acme", "q-inflight", Encoding.UTF8.GetBytes("being accepted"));
        var path = h.PayloadPath("q-inflight");
        File.SetLastWriteTimeUtc(path, QueueHarness.Start.UtcDateTime.AddMinutes(1));

        var recovery = await h.Store.RecoverAsync();

        Assert.Empty(recovery.OrphanPayloads);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Metadata_whose_payload_is_gone_is_reported_as_an_integrity_fault()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        // Someone removed the spool file behind the queue's back.
        File.Delete(h.PayloadPath(queueId));

        var recovery = await h.Store.RecoverAsync();

        var fault = Assert.Single(recovery.MissingPayloads);
        Assert.Equal(queueId, fault.QueueId);

        // Reported, never repaired and never quietly discarded: these bytes are mail we accepted.
        Assert.NotNull(await h.Store.GetItemAsync(queueId));

        var lease = await h.Store.ClaimNextAsync("worker-a");
        Assert.NotNull(lease);
        Assert.Throws<QueueIntegrityException>(() => h.Store.OpenPayload(lease!.Item));
    }

    [Fact]
    public async Task An_idempotent_replay_returns_the_original_id_and_a_changed_payload_conflicts()
    {
        using var h = new QueueHarness();
        const string digest = "digest-stable";

        var first = await h.Store.AcceptAsync(
            QueueHarness.Submission(idempotencyKey: "key-1", mimeDigest: digest));
        Assert.True(first.IsAccepted);

        var replay = await h.Store.AcceptAsync(
            QueueHarness.Submission(idempotencyKey: "key-1", mimeDigest: digest));

        Assert.Equal(QueueAdmission.DuplicateSubmission, replay.Admission);
        Assert.Equal(first.QueueId, replay.QueueId);
        Assert.True(replay.IsAccepted);

        var conflict = await h.Store.AcceptAsync(
            QueueHarness.Submission(idempotencyKey: "key-1", mimeDigest: "digest-different"));

        Assert.Equal(QueueAdmission.RefusedIdempotencyConflict, conflict.Admission);
        Assert.False(conflict.IsAccepted);

        // One accepted payload and nothing else — the replay and the conflict wrote no bytes.
        Assert.Single(Directory.GetFiles(h.SpoolRoot, "*.eml", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Concurrent_replays_of_one_key_produce_exactly_one_message()
    {
        using var h = new QueueHarness();

        // Eight writers race the same idempotency key.
        //
        // Task.Run rather than a bare WhenAll: Microsoft.Data.Sqlite's async methods are
        // synchronous under the hood and the spool write often completes inline, so a
        // straightforward WhenAll can run these one after another and let the cheap pre-check
        // catch every replay — quietly turning this into a test of the sequential path. Forcing
        // each onto the thread pool restores the race, which is the only way some of these get
        // past the pre-check and are settled by the unique index instead.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => h.Store.AcceptAsync(
                QueueHarness.Submission(idempotencyKey: "key-race", mimeDigest: "digest-shared")))));

        Assert.All(results, r => Assert.True(r.IsAccepted, $"{r.Admission}: {r.Detail}"));

        // One message, one id, and exactly one payload on disk. A lost race leaves bytes that no
        // metadata row will ever name, and reporting them as "accepted" must not preserve them.
        Assert.Single(results.Select(r => r.QueueId).Distinct());
        Assert.Single(Directory.GetFiles(h.SpoolRoot, "*.eml", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Concurrent_claims_never_hand_one_message_to_two_workers()
    {
        using var h = new QueueHarness();

        for (var i = 0; i < 12; i++)
        {
            await h.AcceptAsync(QueueHarness.Submission());
        }

        // Genuinely parallel, so the claims contend for the same rows rather than queueing up.
        var leases = await Task.WhenAll(
            Enumerable.Range(0, 12).Select(i => Task.Run(() => h.Store.ClaimNextAsync($"worker-{i}"))));

        var claimed = leases.Where(l => l is not null).Select(l => l!.QueueId).ToList();

        // The lease is the mutual-exclusion primitive. Two workers holding the same message would
        // mean two deliveries of it.
        Assert.Equal(12, claimed.Count);
        Assert.Equal(12, claimed.Distinct().Count());
    }

    [Fact]
    public async Task The_same_key_for_a_different_tenant_is_a_different_submission()
    {
        using var h = new QueueHarness();

        var acme = await h.Store.AcceptAsync(
            QueueHarness.Submission(tenantId: "acme", idempotencyKey: "shared", mimeDigest: "d1"));
        var globex = await h.Store.AcceptAsync(
            QueueHarness.Submission(tenantId: "globex", idempotencyKey: "shared", mimeDigest: "d2"));

        Assert.True(acme.IsAccepted);
        Assert.True(globex.IsAccepted);
        Assert.NotEqual(acme.QueueId, globex.QueueId);
    }

    [Fact]
    public async Task Purging_releases_the_bytes_without_erasing_the_record()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            TerminalPayloadRetention = TimeSpan.FromHours(24),
        });

        var queueId = await h.AcceptAsync(QueueHarness.Submission());
        var lease = await h.Store.ClaimNextAsync("worker-a");
        await h.Store.CompleteAsync(lease!, QueueHarness.Delivered("worker-a", "rcpt@example.test"));

        h.Clock.Advance(TimeSpan.FromHours(25));
        var recovery = await h.Store.RecoverAsync();

        var purged = Assert.Single(recovery.PurgedPayloads);
        Assert.Contains(queueId, purged, StringComparison.Ordinal);
        Assert.False(File.Exists(h.PayloadPath(queueId)));

        // The row survives: it is the audit record of a message we took responsibility for.
        var item = await h.Store.GetItemAsync(queueId);
        Assert.NotNull(item);
        Assert.NotNull(item!.PurgedAt);

        // And a deliberately purged payload is not an integrity fault.
        Assert.Empty((await h.Store.RecoverAsync()).MissingPayloads);
    }

    [Fact]
    public async Task Retention_does_not_purge_an_undelivered_message()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            TerminalPayloadRetention = TimeSpan.FromHours(1),
        });

        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        h.Clock.Advance(TimeSpan.FromDays(7));
        var recovery = await h.Store.RecoverAsync();

        Assert.Empty(recovery.PurgedPayloads);
        Assert.True(File.Exists(h.PayloadPath(queueId)));
    }

    /// <remarks>
    /// <b>Consolidating the predicate changed behaviour, and that is how the drift was found.</b>
    /// This lane's copy also accepted <c>"&lt; &gt;"</c> (brackets with a blank inside); Core's is
    /// exact and does not. The stricter answer is the right one — RFC 5321's null reverse-path is
    /// <c>&lt;&gt;</c>, and <c>&lt; &gt;</c> is a malformed address rather than the null sender — but
    /// the point is that two copies had already diverged, silently, before anyone looked.
    ///
    /// <para>
    /// Worth knowing what <c>&lt; &gt;</c> does now: it is not a null sender, so it is not refused
    /// here, and it is not blank, so <c>Require</c> passes it — it is treated as an ordinary
    /// address. That is an address-syntax gap rather than a null-sender one, and it is recorded
    /// rather than fixed in a rule that is not about it.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("<>")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task The_null_sender_is_refused_on_the_OUTBOUND_path(string mailFrom)
    {
        using var h = new QueueHarness();

        // We do not originate bounces, so an outbound null sender is declined — as a *result*, not
        // an exception, so it is distinguishable from a caller construction error.
        var submission = QueueHarness.Submission() with
        {
            MailFrom = mailFrom,
            Direction = MailDirection.Outbound,
        };

        var result = await h.Store.AcceptAsync(submission);

        Assert.Equal(QueueAdmission.RefusedNullSender, result.Admission);
        Assert.False(result.IsAccepted);
        Assert.Empty(await h.Store.CountByStateAsync());
    }

    [Theory]
    [InlineData("<>")]
    [InlineData("")]
    public async Task A_null_sender_is_ACCEPTED_on_the_inbound_path(string mailFrom)
    {
        using var h = new QueueHarness();

        // The defect this test exists for: a DSN being delivered to one of our users is ordinary,
        // legitimate mail. An earlier version refused it outright via an unconditional
        // `Require(MailFrom)`, and a reassuring comment claimed inbound was unaffected — which was
        // false, and stopped the next reader looking.
        var submission = QueueHarness.Submission() with
        {
            MailFrom = mailFrom,
            Direction = MailDirection.Inbound,
        };

        var result = await h.Store.AcceptAsync(submission);

        Assert.True(result.IsAccepted, $"{result.Admission}: {result.Detail}");
    }

    [Fact]
    public async Task An_ordinary_sender_is_unaffected_by_the_null_sender_check()
    {
        using var h = new QueueHarness();

        // The guard must not catch the empty-string-like edge of a legal address.
        var result = await h.Store.AcceptAsync(
            QueueHarness.Submission() with { MailFrom = "s@acme.test" });

        Assert.True(result.IsAccepted);
    }

    [Fact]
    public async Task A_submission_without_recipients_is_rejected_rather_than_silently_narrowed()
    {
        using var h = new QueueHarness();

        var submission = QueueHarness.Submission() with { Recipients = [] };

        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.AcceptAsync(submission));
    }

    [Fact]
    public async Task A_duplicated_recipient_is_rejected_rather_than_silently_deduplicated()
    {
        using var h = new QueueHarness();

        var submission = QueueHarness.Submission(recipients: ["a@example.test", "A@example.test"]);

        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.AcceptAsync(submission));
    }

    [Fact]
    public async Task Payloads_over_the_size_limit_are_refused_before_the_spool_is_touched()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            MaxPayloadBytes = 128,
            MaxLivePayloadBytesPerTenant = 4096,
        });

        var refused = await h.Store.AcceptAsync(QueueHarness.Submission(payloadBytes: 256));

        Assert.Equal(QueueAdmission.RefusedPayloadTooLarge, refused.Admission);
        Assert.False(refused.IsAccepted);
        Assert.Empty(Directory.GetFiles(h.SpoolRoot, "*.eml", SearchOption.AllDirectories));

        // "Before the spool is touched" needs its own observable. An empty file list does not
        // distinguish it from "written and then cleaned up" — that assertion would stay green
        // under a mutation that moved the size check to run *after* the payload write, and the
        // name would then be claiming something the test cannot see. The tenant's spool directory
        // is created only on the first write, so its absence is the difference.
        Assert.False(
            Directory.Exists(Path.Combine(h.SpoolRoot, "acme")),
            "The spool directory for this tenant should never have been created: an oversize " +
            "message must be refused before any payload I/O begins.");
    }
}
