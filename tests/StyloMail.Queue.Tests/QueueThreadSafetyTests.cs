using System.Reflection;
using StyloMail.Core;

namespace StyloMail.Queue.Tests;

/// <summary>
/// The queue and its spool are held as one instance and called from whatever thread a delivery
/// worker runs on. These tests make that a stated guarantee rather than an accident.
/// </summary>
public class QueueThreadSafetyTests
{
    /// <summary>
    /// Mutable instance fields that are deliberately allowed, and why.
    /// </summary>
    /// <remarks>
    /// This is an allow-list rather than a rule because the interesting failure is a *new* field.
    /// A scratch buffer or a cached list added here would work perfectly in every single-threaded
    /// test and corrupt state under load, and it would be diagnosed as a message-handling bug
    /// somewhere else entirely. Naming the exceptions forces the next person to decide.
    /// </remarks>
    private static readonly Dictionary<Type, string[]> AllowedMutableState = new()
    {
        // Write-once lazy schema cache. The read outside the gate is a benign stale-null: the
        // worst case is taking the semaphore and finding the task already assigned. Reference
        // assignment is atomic, so no torn read is possible.
        [typeof(QueueStore)] = ["_initialised"],
        [typeof(SpoolStore)] = [],
    };

    [Fact]
    public void Shared_components_hold_no_mutable_state_beyond_the_ones_we_allow()
    {
        var failures = new List<string>();

        foreach (var (type, allowed) in AllowedMutableState)
        {
            var mutable = type
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => !f.IsInitOnly && !f.IsLiteral)
                .Select(f => f.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            var expected = allowed.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            if (!mutable.SequenceEqual(expected, StringComparer.Ordinal))
            {
                failures.Add(
                    $"  {type.Name}: found [{string.Join(", ", mutable)}], allowed [{string.Join(", ", expected)}]");
            }
        }

        Assert.True(
            failures.Count == 0,
            "A component shared across worker threads has gained mutable instance state:\n" +
            string.Join("\n", failures) +
            "\n\nThe host holds one instance and calls it from every delivery thread. If the new " +
            "field is genuinely safe (write-once, immutable-after-construction), add it to " +
            "AllowedMutableState with the reason. Otherwise make it readonly, or register the " +
            "component per-scope, do not leave it unstated.");
    }

    [Fact]
    public async Task The_spool_holds_no_mutable_state_so_one_instance_may_be_shared()
    {
        using var h = new QueueHarness();

        // 64 payloads written concurrently through the ONE spool instance the harness holds.
        var references = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(
            () => h.Spool.WriteAsync("acme", $"q-concurrent-{i}", System.Text.Encoding.UTF8.GetBytes($"body {i}")))));

        // Every write produced a distinct, readable payload with its own content: no shared
        // buffer, no crossed streams, no temp-file collision.
        Assert.Equal(64, references.Distinct().Count());

        for (var i = 0; i < references.Length; i++)
        {
            Assert.True(h.Spool.Exists(references[i]));
        }

        var contents = new HashSet<string>();
        foreach (var reference in references)
        {
            using var stream = h.Spool.OpenRead(reference)!;
            using var reader = new StreamReader(stream);
            contents.Add(await reader.ReadToEndAsync());
        }

        Assert.Equal(64, contents.Count);
    }

    [Fact]
    public async Task Concurrent_delivery_reaches_the_same_state_as_sequential_delivery()
    {
        using var h = new QueueHarness();
        const int count = 24;

        // Identical work for two tenants: one drained by a single worker, one by eight contending.
        for (var i = 0; i < count; i++)
        {
            await h.AcceptAsync(QueueHarness.Submission(tenantId: "seq"));
            await h.AcceptAsync(QueueHarness.Submission(tenantId: "con"));
        }

        await DrainAsync(h, "seq", workers: 1);
        await DrainAsync(h, "con", workers: 8);

        // Not "neither threw", the same end state.
        Assert.Equal(await StateDistributionAsync(h, "seq"), await StateDistributionAsync(h, "con"));

        // And the invariant contention is most likely to break: every message delivered exactly
        // once. Two workers holding the same message would show up here as extra Delivered rows,
        // long before it showed up as a duplicate in someone's inbox.
        Assert.Equal(count, await DeliveredAttemptRowsAsync(h, "seq"));
        Assert.Equal(count, await DeliveredAttemptRowsAsync(h, "con"));
    }

    /// <summary>
    /// Drains a tenant with the given number of contending workers.
    /// </summary>
    /// <remarks>
    /// <b>Bounded on purpose.</b> If claim or completion ever stops making progress, a lease that
    /// is never released, a completion that is silently not applied, an unbounded loop here does
    /// not fail, it <em>hangs</em>, taking the whole run with it and reporting nothing at all. The
    /// cap converts that into a named failure. This was found by mutation: a mutation that dropped
    /// the state change on claim made every completion a no-op, and this test hung the suite for
    /// three minutes instead of going red.
    /// </remarks>
    private static async Task DrainAsync(QueueHarness h, string tenantId, int workers)
    {
        const int maxClaimsPerWorker = 2000;

        await Task.WhenAll(Enumerable.Range(0, workers).Select(worker => Task.Run(async () =>
        {
            var workerId = $"{tenantId}-worker-{worker}";

            for (var i = 0; i < maxClaimsPerWorker; i++)
            {
                var lease = await h.Store.ClaimNextAsync(workerId, tenantId);
                if (lease is null)
                {
                    return;
                }

                await h.Store.CompleteAsync(lease, new DeliveryReport
                {
                    WorkerId = workerId,
                    Recipients = [.. lease.PendingRecipients.Select(r => new RecipientDeliveryResult
                    {
                        Recipient = r.Recipient,
                        Outcome = DeliveryAttemptOutcome.Delivered,
                    })],
                });
            }

            throw new InvalidOperationException(
                $"Worker '{workerId}' made {maxClaimsPerWorker} claims without the queue draining. " +
                "Claim or completion is not making progress, check that a claim actually takes the " +
                "lease and that a completion under a held lease is applied.");
        })));
    }

    private static async Task<string> StateDistributionAsync(QueueHarness h, string tenantId)
    {
        var counts = await h.Store.CountByStateAsync(tenantId);
        return string.Join(
            ",", counts.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"));
    }

    /// <summary>
    /// Counts delivered attempts for a tenant straight from the history table.
    /// </summary>
    /// <remarks>
    /// Read directly rather than through <see cref="QueueStore"/> because the assertion is about
    /// the history itself, and the store deliberately does not expose a "count everything" query
    /// that nothing in production would use.
    /// </remarks>
    private static async Task<long> DeliveredAttemptRowsAsync(QueueHarness h, string tenantId)
    {
        await using var connection = h.Connections.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT COUNT(*) FROM queue_attempt a
              JOIN queue_item i ON i.queue_id = a.queue_id
             WHERE i.tenant_id = $tenant AND a.outcome = $outcome;
            """;
        cmd.Parameters.AddWithValue("$tenant", tenantId);
        cmd.Parameters.AddWithValue("$outcome", nameof(DeliveryAttemptOutcome.Delivered));

        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
