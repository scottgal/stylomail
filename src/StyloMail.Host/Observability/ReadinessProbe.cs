using Microsoft.Extensions.Options;
using StyloMail.Host.Storage;

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
/// messages that will be refused — turning a local storage fault into a delivery outage.
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

    public ReadinessProbe(HostDatabase database, IOptions<HostStorageOptions> storage)
    {
        _database = database;
        _storage = storage.Value;
    }

    public ReadinessResult Check()
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
