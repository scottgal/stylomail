using Microsoft.Data.Sqlite;
using StyloMail.Assessment;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Controls;

/// <summary>One engage or disengage, who did it, and when.</summary>
public sealed record KillSwitchEvent
{
    public required bool Engaged { get; init; }

    public required string Actor { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}

/// <summary>
/// The emergency stop, held in the host's own database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Persisted rather than held in memory, because the stop has to outlive the process.</b> An
/// emergency stop that silently disengages when the host restarts is worse than one that was never
/// wired: it teaches an operator to trust it, and the restart is exactly when nobody is looking.
/// </para>
/// <para>
/// <b>State is the latest event, not a mutable flag.</b> A single row that changed value would
/// answer "is it engaged" and nothing else, while an append-only event list answers "who pulled it,
/// and when", which is the question asked afterwards. The cost is one indexed read per assessment,
/// paid deliberately: see <see cref="IEmergencyKillSwitch"/> for why a cached answer is a stop that
/// has not stopped yet.
/// </para>
/// </remarks>
public sealed class SqliteEmergencyKillSwitch : IEmergencyKillSwitch
{
    private readonly HostDatabase _database;

    public SqliteEmergencyKillSwitch(HostDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// True while the most recent event engaged the stop.
    /// </summary>
    /// <remarks>
    /// False for a deployment that has never engaged or disengaged anything, which is the correct
    /// reading of an empty history rather than a default standing in for a missing answer.
    /// </remarks>
    public bool IsEngaged
    {
        get
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT engaged FROM kill_switch_event ORDER BY id DESC LIMIT 1;";

            return command.ExecuteScalar() is long engaged && engaged != 0;
        }
    }

    /// <summary>Engages the stop, recording who asked. Returns false when it was already engaged.</summary>
    /// <remarks>
    /// A repeated engage is not an error and is not recorded. The history is a list of transitions,
    /// so a caller holding the button down does not bury the one entry that says when the system
    /// actually stopped.
    /// </remarks>
    public bool Engage(string actor, DateTimeOffset occurredAt) => Record(true, actor, occurredAt);

    /// <summary>Disengages the stop, recording who asked. Returns false when it was not engaged.</summary>
    public bool Disengage(string actor, DateTimeOffset occurredAt) => Record(false, actor, occurredAt);

    /// <summary>The transitions, most recent first.</summary>
    public IReadOnlyList<KillSwitchEvent> History(int limit)
    {
        if (limit < 1)
        {
            return [];
        }

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT engaged, actor, occurred_at
              FROM kill_switch_event
             ORDER BY id DESC
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var events = new List<KillSwitchEvent>(limit);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            events.Add(new KillSwitchEvent
            {
                Engaged = reader.GetInt64(0) != 0,
                Actor = reader.GetString(1),
                OccurredAt = DateTimeOffset.Parse(
                    reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
            });
        }

        return events;
    }

    private bool Record(bool engaged, string actor, DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        if (IsEngaged == engaged)
        {
            return false;
        }

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO kill_switch_event (engaged, actor, occurred_at)
            VALUES ($engaged, $actor, $occurred);
            """;
        command.Parameters.AddWithValue("$engaged", engaged ? 1 : 0);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$occurred", occurredAt.ToString("O"));

        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The emergency kill switch could not be written.", ex);
        }

        return true;
    }
}
