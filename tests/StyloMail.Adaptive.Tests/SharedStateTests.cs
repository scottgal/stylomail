using System.Reflection;
using StyloMail.Adaptive.Learning;
using StyloMail.Adaptive.Profiles;
using StyloMail.Adaptive.Scoring;
using StyloMail.Adaptive.Signals;
using StyloMail.Adaptive.Storage;

namespace StyloMail.Adaptive.Tests;

/// <summary>
/// How this component behaves when the host shares one instance across threads.
/// </summary>
/// <remarks>
/// These components were written without knowing how they would be hosted, and the failure mode
/// when one is shared unsafely is not a crash in this assembly, it is corrupted accounting that
/// surfaces later as a message-handling bug somewhere else entirely. So the question "may one
/// instance be shared?" is answered here, in tests, rather than left to a comment.
///
/// <para>
/// <c>Task.Run</c> is used deliberately: work has to occupy real threads for a race to exist at
/// all. Nothing here touches the database, so the synchronous-async trap that makes bare
/// <c>WhenAll</c> tests toothless elsewhere in this repo does not apply.
/// </para>
///
/// <para>
/// Two different guarantees, tested two different ways. Types that are <em>immutable</em> are
/// covered by a reflection tripwire. The quota ledger and the incident log are <em>not</em>
/// immutable, they exist to be mutated, and are covered behaviourally under contention, which
/// is the only thing that actually proves a lock is doing its job.
/// </para>
/// </remarks>
public class SharedStateTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Tenants = ["tenant-a", "tenant-b"];
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    [Fact]
    public async Task TheQuotaLedgerNeverOverReservesUnderContention()
    {
        var ledger = new SendingQuotaLedger(recipientsPerWindow: 1000, Hour);
        var granted = 0;

        // 32 threads x 50 attempts x 10 recipients = 16,000 recipients requested against a
        // budget of 1,000.
        var workers = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                if (ledger.TryReserve("tenant-a", "sender", 10, Start))
                {
                    Interlocked.Add(ref granted, 10);
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);

        // A check-then-act race here does not under-grant, it **over-grants**, and the quota is
        // the only thing bounding how much a late detection lets escape. Exactly the budget must
        // be handed out: not a recipient more, and all of it.
        Assert.Equal(1000, granted);
        Assert.Equal(0, ledger.Remaining("tenant-a", "sender", Start));
    }

    [Fact]
    public async Task TheQuotaLedgerAccountsExactlyForEveryReserveAndRelease()
    {
        const int capacity = 500;
        var ledger = new SendingQuotaLedger(capacity, Hour);
        var granted = 0;
        var released = 0;

        var workers = Enumerable.Range(0, 16).Select(thread => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                if (!ledger.TryReserve("tenant-a", "sender", 5, Start))
                {
                    continue;
                }

                Interlocked.Add(ref granted, 5);

                if ((i + thread) % 3 == 0)
                {
                    // Accumulate what Release actually returned, not what we asked for. Under
                    // contention a release can find fewer entries outstanding than it expects,
                    // and using the requested figure would make this invariant hold by
                    // construction rather than by observation.
                    Interlocked.Add(ref released, ledger.Release("tenant-a", "sender", 5, Start));
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);

        // Exact arithmetic, not a range check. Whatever order the interleaving landed in, the
        // budget handed out minus the budget returned must be exactly what is gone, so any
        // update the ledger loses to a race shows up as a mismatch rather than as a slightly
        // wrong number nobody notices. (A range check here would be toothless: sixteen threads
        // holding five recipients each can never approach a budget of five hundred.)
        Assert.Equal(capacity - granted + released, ledger.Remaining("tenant-a", "sender", Start));
    }

    [Fact]
    public async Task TheQuotaLedgerKeepsTenantsApartUnderContention()
    {
        var ledger = new SendingQuotaLedger(recipientsPerWindow: 200, Hour);
        var tasks = Tenants
            .Select(tenant => Task.Run(() =>
            {
                for (var i = 0; i < 100; i++)
                {
                    ledger.TryReserve(tenant, "sender", 2, Start);
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Both tenants exhaust their own budget and neither spends the other's.
        Assert.Equal(0, ledger.Remaining("tenant-a", "sender", Start));
        Assert.Equal(0, ledger.Remaining("tenant-b", "sender", Start));
    }

    [Fact]
    public async Task NoIncidentIsLostUnderContention()
    {
        const int perThread = 250;
        const int threads = 8;
        var log = new IncidentLog();

        var workers = Enumerable.Range(0, threads).Select(thread => Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++)
            {
                log.Record("tenant-a", "sender", $"incident-{thread}-{i}", Start);
            }
        })).ToArray();

        await Task.WhenAll(workers);

        // Containment history records what was done about a suspected compromise. Entries lost
        // to a list race are incidents that happened and are not on the record.
        Assert.Equal(perThread * threads, log.For("tenant-a", "sender").Count);
    }

    [Theory]
    [InlineData(typeof(ProfileKeyHasher))]
    [InlineData(typeof(RobustScaleModel))]
    [InlineData(typeof(DimensionVector))]
    [InlineData(typeof(BehaviouralEvidenceEvaluator))]
    [InlineData(typeof(SqliteAdaptiveProfileStore))]
    public void TypesIntendedForSharingCarryNoReassignableInstanceState(Type type)
    {
        var reassignable = type
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(field => !field.IsInitOnly && !field.IsLiteral)
            .Select(field => field.Name)
            .ToArray();

        Assert.True(
            reassignable.Length == 0,
            $"{type.Name} is shareable, but it now has reassignable instance state "
            + $"({string.Join(", ", reassignable)}). Either make that state immutable, or stop "
            + "sharing one instance per host, a shared instance carrying per-call state corrupts "
            + "under load and gets diagnosed in whatever component touches it first.");
    }
}
