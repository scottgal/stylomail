using Microsoft.Data.Sqlite;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Controls;

/// <summary>
/// What an operator records about a sending principal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Operator metadata, not policy.</b> Nothing in the assessment pipeline reads any of this. It
/// exists so a human can navigate a tenant: "Acme outbound" is findable where an address is not,
/// and so the console can be joined to the operator's own systems.
/// </para>
/// <para>
/// <b><see cref="Posture"/> and <see cref="NotificationTarget"/> are stored and read by nothing.</b>
/// They are carried because adding a column to a populated store later is worse than carrying two
/// unused ones, and because a console whose stated job is explaining why something was held must not
/// show a control that looks like it works. Both are labelled as not yet acted on in the UI and in the
/// API field documentation, so a client that is not the console cannot be misled either. When the
/// pipeline honours posture, or the host delivers to a notification target, the labels come off, and
/// until then, anything that reads them is a bug rather than a feature.
/// </para>
/// </remarks>
public sealed record SenderProfile
{
    public required string PrincipalId { get; init; }

    /// <summary>A human name for the principal. Null when nobody has set one.</summary>
    public string? Label { get; init; }

    /// <summary>The company this sender is filed under, or null for unfiled.</summary>
    /// <remarks>
    /// A free reference rather than a foreign key. Membership lives here rather than as a list on the
    /// company so that promoting companies to a hierarchy later is a parent link on that table and no
    /// sender has to move.
    /// </remarks>
    public string? CompanyId { get; init; }

    public string? Notes { get; init; }

    /// <summary>The operator's own identifier for this sender, so the console can be joined to theirs.</summary>
    public string? ExternalRef { get; init; }

    /// <summary>Where to tell someone. <b>Stored only: nothing delivers to it yet.</b></summary>
    public string? NotificationTarget { get; init; }

    /// <summary>A visible stance: trusted, normal or watch. <b>Stored only: no pipeline reads it yet.</b></summary>
    public string? Posture { get; init; }

    public required string UpdatedBy { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The profile of a principal nobody has described. Every field is absent, not blank.</summary>
    public static SenderProfile Unset(string principalId) => new()
    {
        PrincipalId = principalId,
        UpdatedBy = string.Empty,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };
}

/// <summary>An operator-side grouping of senders.</summary>
/// <remarks>
/// <b>Flat, and operator-side.</b> Nothing in the pipeline reads a company, so group-wide limits are
/// deliberately not expressible here: a group the pipeline cannot see would make a quota look enforced
/// while it bound nothing. Rate limits wait for that promotion rather than being smuggled in with a
/// table.
/// </remarks>
public sealed record Company
{
    public required string CompanyId { get; init; }

    public required string Name { get; init; }

    public string? Notes { get; init; }

    public required string UpdatedBy { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Sender profiles, scoped by tenant and principal together.</summary>
/// <remarks>
/// The same scoping rule as <see cref="ISenderControlStore"/>: a principal identifier is unique only
/// within its tenant, so an unscoped lookup would let one tenant's metadata reach another's senders.
/// The tenant is an explicit argument on every method rather than a field on the record, so a write
/// cannot be misdirected by whatever a caller happened to put in the object it passed.
/// </remarks>
public interface ISenderProfileStore
{
    /// <summary>One principal's profile, or null when nobody has described it.</summary>
    Task<SenderProfile?> GetAsync(string tenantId, string principalId, CancellationToken cancellationToken);

    /// <summary>
    /// Every profile this tenant holds.
    /// </summary>
    /// <remarks>
    /// One query rather than one per principal, because the sender listing joins these to build the
    /// sidebar. Doing that with <see cref="GetAsync"/> per row would turn a bounded listing into N
    /// round trips whose count the caller does not control.
    /// </remarks>
    Task<IReadOnlyList<SenderProfile>> ListAsync(string tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Records an operator's description of a principal, replacing whatever was there.
    /// </summary>
    /// <remarks>
    /// A full replace rather than a merge, because the console sends the whole form and a merge would
    /// make clearing a field impossible. Omission is not silent: a null field is written as null, and
    /// the audit stamp records who decided that.
    /// </remarks>
    Task PutAsync(string tenantId, SenderProfile profile, CancellationToken cancellationToken);
}

/// <summary>Companies, scoped by tenant.</summary>
public interface ICompanyStore
{
    Task<Company?> GetAsync(string tenantId, string companyId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Company>> ListAsync(string tenantId, CancellationToken cancellationToken);

    /// <summary>Creates or replaces a company. Idempotent by id.</summary>
    Task PutAsync(string tenantId, Company company, CancellationToken cancellationToken);
}

/// <summary>SQLite-backed sender profiles.</summary>
public sealed class SqliteSenderProfileStore : ISenderProfileStore
{
    private readonly HostDatabase _database;

    public SqliteSenderProfileStore(HostDatabase database) => _database = database;

    public Task<SenderProfile?> GetAsync(
        string tenantId,
        string principalId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"{SelectColumns} WHERE tenant_id = $tenant AND principal_id = $principal;";
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$principal", principalId);

            using var reader = command.ExecuteReader();

            return Task.FromResult(reader.Read() ? Read(reader) : null);
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The sender profile could not be read.", ex);
        }
    }

    public Task<IReadOnlyList<SenderProfile>> ListAsync(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"{SelectColumns} WHERE tenant_id = $tenant ORDER BY principal_id;";
            command.Parameters.AddWithValue("$tenant", tenantId);

            var profiles = new List<SenderProfile>();

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                profiles.Add(Read(reader));
            }

            return Task.FromResult<IReadOnlyList<SenderProfile>>(profiles);
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The sender profiles could not be read.", ex);
        }
    }

    public Task PutAsync(string tenantId, SenderProfile profile, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.PrincipalId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO sender_profile
                    (tenant_id, principal_id, label, company_id, notes, external_ref,
                     notification_target, posture, updated_by, updated_at)
                VALUES ($tenant, $principal, $label, $company, $notes, $ref, $target, $posture, $by, $at)
                ON CONFLICT (tenant_id, principal_id) DO UPDATE SET
                    label               = $label,
                    company_id          = $company,
                    notes               = $notes,
                    external_ref        = $ref,
                    notification_target = $target,
                    posture             = $posture,
                    updated_by          = $by,
                    updated_at          = $at;
                """;

            // Written through a helper rather than conditionally: a null must *clear* the column, and
            // `COALESCE`-style "keep the old value when absent" would make it impossible for an
            // operator to remove a label they no longer want.
            Bind(command, "$label", profile.Label);
            Bind(command, "$company", profile.CompanyId);
            Bind(command, "$notes", profile.Notes);
            Bind(command, "$ref", profile.ExternalRef);
            Bind(command, "$target", profile.NotificationTarget);
            Bind(command, "$posture", profile.Posture);

            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$principal", profile.PrincipalId);
            command.Parameters.AddWithValue("$by", profile.UpdatedBy);
            command.Parameters.AddWithValue("$at", profile.UpdatedAt.ToString("O"));

            command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The sender profile could not be written.", ex);
        }

        return Task.CompletedTask;
    }

    private const string SelectColumns =
        """
        SELECT principal_id, label, company_id, notes, external_ref, notification_target, posture,
               updated_by, updated_at
        FROM sender_profile
        """;

    private static SenderProfile Read(SqliteDataReader reader) => new()
    {
        PrincipalId = reader.GetString(0),
        Label = reader.IsDBNull(1) ? null : reader.GetString(1),
        CompanyId = reader.IsDBNull(2) ? null : reader.GetString(2),
        Notes = reader.IsDBNull(3) ? null : reader.GetString(3),
        ExternalRef = reader.IsDBNull(4) ? null : reader.GetString(4),
        NotificationTarget = reader.IsDBNull(5) ? null : reader.GetString(5),
        Posture = reader.IsDBNull(6) ? null : reader.GetString(6),
        UpdatedBy = reader.GetString(7),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(8)),
    };

    private static void Bind(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
}

/// <summary>SQLite-backed companies.</summary>
public sealed class SqliteCompanyStore : ICompanyStore
{
    private readonly HostDatabase _database;

    public SqliteCompanyStore(HostDatabase database) => _database = database;

    public Task<Company?> GetAsync(string tenantId, string companyId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(companyId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"{SelectColumns} WHERE tenant_id = $tenant AND company_id = $company;";
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$company", companyId);

            using var reader = command.ExecuteReader();

            return Task.FromResult(reader.Read() ? Read(reader) : null);
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The company could not be read.", ex);
        }
    }

    public Task<IReadOnlyList<Company>> ListAsync(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"{SelectColumns} WHERE tenant_id = $tenant ORDER BY name;";
            command.Parameters.AddWithValue("$tenant", tenantId);

            var companies = new List<Company>();

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                companies.Add(Read(reader));
            }

            return Task.FromResult<IReadOnlyList<Company>>(companies);
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The companies could not be read.", ex);
        }
    }

    public Task PutAsync(string tenantId, Company company, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(company);
        ArgumentException.ThrowIfNullOrWhiteSpace(company.CompanyId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO company (tenant_id, company_id, name, notes, updated_by, updated_at)
                VALUES ($tenant, $company, $name, $notes, $by, $at)
                ON CONFLICT (tenant_id, company_id) DO UPDATE SET
                    name       = $name,
                    notes      = $notes,
                    updated_by = $by,
                    updated_at = $at;
                """;

            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$company", company.CompanyId);
            command.Parameters.AddWithValue("$name", company.Name);
            command.Parameters.AddWithValue("$notes", (object?)company.Notes ?? DBNull.Value);
            command.Parameters.AddWithValue("$by", company.UpdatedBy);
            command.Parameters.AddWithValue("$at", company.UpdatedAt.ToString("O"));

            command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The company could not be written.", ex);
        }

        return Task.CompletedTask;
    }

    private const string SelectColumns =
        "SELECT company_id, name, notes, updated_by, updated_at FROM company";

    private static Company Read(SqliteDataReader reader) => new()
    {
        CompanyId = reader.GetString(0),
        Name = reader.GetString(1),
        Notes = reader.IsDBNull(2) ? null : reader.GetString(2),
        UpdatedBy = reader.GetString(3),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(4)),
    };
}
