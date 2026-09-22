using Microsoft.Extensions.Options;
using StyloMail.Host.Hosting;
using StyloMail.Host.Storage;
using StyloMail.Host.Traffic;

namespace StyloMail.Host.Observability;

/// <summary>The outcome of a readiness check. Names the failed check, never its contents.</summary>
public sealed record ReadinessResult(bool Ready, IReadOnlyList<string> FailedChecks)
{
    public static readonly ReadinessResult ReadyResult = new(true, []);
}

/// <summary>
/// Answers "can this host durably accept mail right now?"
/// </summary>
/// <remarks>
/// Readiness is not a liveness check with more steps. It is a claim about the ability to keep the
/// system's central promise: accept a message and not lose it. A host whose spool cannot be written
/// must stop advertising itself, because the alternative is a load balancer continuing to hand it
/// messages that will be refused, turning a local storage fault into a delivery outage.
///
/// <para>
/// It probes rather than assumes. Reporting ready because a directory existed at startup would miss
/// the case that actually happens: a volume that filled or was unmounted while the process ran.
/// </para>
/// </remarks>
public sealed class ReadinessProbe
{
    private readonly HostDatabase _database;
    private readonly HostStorageOptions _storage;
    private readonly ProviderCredentialHealth _credentials;
    private readonly ITrafficEvents _events;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    private ReadinessResult? _lastObserved;

    public ReadinessProbe(
        HostDatabase database,
        IOptions<HostStorageOptions> storage,
        ProviderCredentialHealth credentials,
        ITrafficEvents events,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);

        _database = database;
        _storage = storage.Value;
        _credentials = credentials;
        _events = events;
        _clock = clock;
    }

    public ReadinessResult Check()
    {
        var result = Evaluate();

        AnnounceIfChanged(result);

        return result;
    }

    /// <summary>
    /// Announces a change in the answer, and only a change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A poll is not a change.</b> This route is polled continuously by whatever routes mail to
    /// this host, so announcing every sample would put a notice per liveness request on the wire and
    /// teach the console to ignore the one channel that carries a real transition.
    /// </para>
    /// <para>
    /// <b>The first answer is a baseline, not a transition.</b> Nothing has changed at that point,
    /// only started being observed, and the console reads the current answer from
    /// <c>/health/ready</c>, which is where state belongs. The baseline is per probe instance, and
    /// there is one probe per host, so a restart begins observing afresh rather than announcing a
    /// transition it never saw.
    /// </para>
    /// <para>
    /// Compared under a lock, because this route answers concurrent callers: two requests racing a
    /// transition would otherwise both decide they were the one that saw it and announce it twice.
    /// </para>
    /// </remarks>
    private void AnnounceIfChanged(ReadinessResult result)
    {
        bool changed;

        lock (_gate)
        {
            changed = _lastObserved is { } previous && !SameAs(previous, result);
            _lastObserved = result;
        }

        if (changed)
        {
            _events.Publish(TrafficEvent.ReadinessChanged(_clock.GetUtcNow()));
        }
    }

    private static bool SameAs(ReadinessResult left, ReadinessResult right)
        => left.Ready == right.Ready
           && left.FailedChecks.Order(StringComparer.Ordinal)
               .SequenceEqual(right.FailedChecks.Order(StringComparer.Ordinal));

    private ReadinessResult Evaluate()
    {
        var failed = new List<string>();

        if (!CanReadDatabase())
        {
            failed.Add("database");
        }

        if (!CanWriteSpool())
        {
            failed.Add("spool");
        }

        // A rejected provider credential is a **not-ready** condition, not a per-request failure.
        //
        // This host cannot assess a message without semantic evidence, and a host that cannot assess
        // must stop advertising itself: otherwise a load balancer keeps delivering mail to it while
        // every assessment fails, turning one deployment's rotated key into a delivery outage. The
        // failure this closes looked healthy: 200 from this route while every request 500ed.
        //
        // Checked here rather than at startup because a credential can be rejected at any point
        // during a run, and a deployment that passed its boot checks is exactly the one that gets
        // surprised. The state is not inferred from configuration: it is what the provider actually
        // answered, so a key that is merely *configured* never affects readiness.
        if (_credentials.IsRejected)
        {
            failed.Add("provider_credential");
        }

        return failed.Count == 0 ? ReadinessResult.ReadyResult : new ReadinessResult(false, failed);
    }

    private bool CanReadDatabase()
    {
        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            command.ExecuteScalar();
            return true;
        }
        catch (Exception)
        {
            // Deliberately swallowed into a check name. The exception text can carry a filesystem
            // path, and this result is served without credentials.
            return false;
        }
    }

    private bool CanWriteSpool()
    {
        try
        {
            Directory.CreateDirectory(_storage.SpoolRoot);

            var probe = Path.Combine(_storage.SpoolRoot, $".readiness-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
