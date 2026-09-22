using System.Text.Json;
using Microsoft.Data.Sqlite;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Auth;

/// <summary>
/// A principal minted on this host, as the store describes it.
/// </summary>
/// <remarks>
/// <b>There is deliberately no key and no digest on this type.</b> Everything that lists, prints or
/// projects a minted principal reads this, so the credential cannot reach a listing by someone
/// serialising the store's own record. The digest lives on <see cref="StoredKeyMaterial"/>, which
/// only the verification path ever sees, and the value itself exists nowhere but in the response to
/// the <c>key create</c> that produced it.
/// </remarks>
public sealed record MintedPrincipal
{
    /// <summary>The public lookup handle carried in the key. Not a secret.</summary>
    public required string KeyId { get; init; }

    public required string PrincipalId { get; init; }

    public required string TenantId { get; init; }

    /// <summary>Privilege names, the same vocabulary configuration uses.</summary>
    public required IReadOnlyList<string> Privileges { get; init; }

    public required IReadOnlyList<string> ApprovedSenderIdentities { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string CreatedBy { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }

    public string? RevokedBy { get; init; }

    public bool IsRevoked => RevokedAt is not null;
}

/// <summary>
/// The material a digest is verified against.
/// </summary>
/// <remarks>
/// Kept out of <see cref="MintedPrincipal"/> on purpose. These are the bytes an attacker who copied
/// the database file would attack, and nothing that renders a principal has any reason to hold them.
/// </remarks>
public sealed record StoredKeyMaterial
{
    public required string Algorithm { get; init; }

    public required int Iterations { get; init; }

    public required byte[] Salt { get; init; }

    public required byte[] Digest { get; init; }
}

/// <summary>One stored row: what it describes, and what verifies it.</summary>
public sealed record MintedPrincipalRecord
{
    public required MintedPrincipal Principal { get; init; }

    public required StoredKeyMaterial Material { get; init; }
}

/// <summary>
/// The host's store of minted principals, held as key digests.
/// </summary>
/// <remarks>
/// <para>
/// <b>The store never holds a key.</b> A row carries a per-key salt and a slow-KDF digest, and the
/// value is shown once at mint time and is unrecoverable afterwards. That is the whole point of the
/// table: the configuration-held plaintext key it replaces was the one secret in this project kept
/// somewhere everyone else's secrets are carefully kept out of.
/// </para>
/// <para>
/// <b>Revocation is a stamp, not a delete.</b> A revoked row keeps the record of who revoked it and
/// when, because "this credential existed and someone killed it" is a question asked afterwards. The
/// row also goes on claiming its principal id, which is what stops a revoked mint from silently
/// handing authority back to an environment entry of the same name, see
/// <see cref="PrincipalDirectory"/>.
/// </para>
/// </remarks>
public sealed class MintedPrincipalStore
{
    private readonly HostDatabase _database;

    public MintedPrincipalStore(HostDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// The value that changes whenever this store changes, anywhere.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes revocation immediate across processes.</b> The CLI that revokes a key is
    /// not the process serving requests, so it cannot reach into the server's memory to evict a cache
    /// entry. It does not have to: every mint and every revocation bumps this number in the same
    /// transaction as the change, so the next resolution in any process sees a different store and
    /// rebuilds. Local eviction would have been the weaker answer, because it only works when the
    /// revoker happens to be the server.
    /// </remarks>
    public long Version()
    {
        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT version FROM host_principal_store WHERE id = 1;";

            var version = command.ExecuteScalar();

            // A database created before this table gained its row, or one someone edited by hand,
            // must not read as version zero: that is the same value the cache treats as "unchanged".
            return version is null or DBNull ? -1 : Convert.ToInt64(version);
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The principal store's version could not be read.", ex);
        }
    }

    /// <summary>The row a presented key names, or null when no live row carries that id.</summary>
    /// <remarks>
    /// A revoked row is not returned. Revocation that merely marked a row and left it resolving would
    /// be a stamp on a credential that still worked.
    /// </remarks>
    public MintedPrincipalRecord? FindByKeyId(string keyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"{SelectColumns} WHERE key_id = $keyId AND revoked_at IS NULL;";
            command.Parameters.AddWithValue("$keyId", keyId);

            using var reader = command.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The principal store could not be read.", ex);
        }
    }

    /// <summary>
    /// Every principal id the store has ever claimed, revoked rows included.
    /// </summary>
    /// <remarks>
    /// <b>Revoked rows go on claiming their principal id.</b> If they did not, revoking a minted key
    /// would restore whatever environment entry shares its name, so a credential an operator
    /// deliberately killed would quietly come back to life under a name nobody re-examined. The
    /// claim is visible rather than mysterious: <c>key list</c> reports the environment entry as
    /// shadowed.
    /// </remarks>
    public IReadOnlySet<string> ClaimedPrincipalIds()
    {
        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT principal_id FROM host_principal;";

            var claimed = new HashSet<string>(StringComparer.Ordinal);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                claimed.Add(reader.GetString(0));
            }

            return claimed;
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The principal store could not be read.", ex);
        }
    }

    /// <summary>Every principal the store has ever held, revoked ones included.</summary>
    /// <remarks>
    /// Revoked rows are listed rather than hidden. A credential inventory that dropped them would
    /// answer "what can authenticate" while looking like it answered "what has been minted", and the
    /// second question is the one an operator auditing access is actually asking.
    /// </remarks>
    public IReadOnlyList<MintedPrincipal> List()
    {
        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"{SelectColumns} ORDER BY principal_id, created_at;";

            var principals = new List<MintedPrincipal>();
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                principals.Add(Read(reader).Principal);
            }

            return principals;
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The principal store could not be read.", ex);
        }
    }

    /// <summary>
    /// Stores a newly minted principal, or returns false when one of that name is already live.
    /// </summary>
    /// <remarks>
    /// <b>Refuses rather than superseding.</b> Minting a second key for a name that already has one
    /// would leave two valid credentials for one identity with nothing on the host saying which is
    /// current. An operator rotating a key revokes first, which is one more command and states what
    /// happened to the old one.
    /// </remarks>
    public bool Create(MintedPrincipal principal, MintedApiKey.Material material)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(material);
        ArgumentException.ThrowIfNullOrWhiteSpace(principal.PrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(principal.TenantId);

        if (!string.Equals(principal.KeyId, material.KeyId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The minted material does not belong to this principal record.", nameof(material));
        }

        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText =
                    """
                    SELECT EXISTS (
                        SELECT 1 FROM host_principal
                        WHERE principal_id = $principal AND revoked_at IS NULL);
                    """;
                existing.Parameters.AddWithValue("$principal", principal.PrincipalId);

                if (Convert.ToInt64(existing.ExecuteScalar()) != 0)
                {
                    // Returning without committing, so the transaction disposes into a rollback.
                    // The read above changed nothing, but leaving the transaction open on a return
                    // path is how a connection goes back to the pool still holding a write lock.
                    return false;
                }
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO host_principal (
                        key_id, principal_id, tenant_id, kdf_algorithm, kdf_iterations, salt, digest,
                        privileges, approved_sender_identities, created_at, created_by)
                    VALUES (
                        $keyId, $principal, $tenant, $algorithm, $iterations, $salt, $digest,
                        $privileges, $senders, $createdAt, $createdBy);
                    """;

                insert.Parameters.AddWithValue("$keyId", principal.KeyId);
                insert.Parameters.AddWithValue("$principal", principal.PrincipalId);
                insert.Parameters.AddWithValue("$tenant", principal.TenantId);
                insert.Parameters.AddWithValue("$algorithm", material.Algorithm);
                insert.Parameters.AddWithValue("$iterations", material.Iterations);
                insert.Parameters.Add("$salt", SqliteType.Blob).Value = material.Salt;
                insert.Parameters.Add("$digest", SqliteType.Blob).Value = material.Digest;
                insert.Parameters.AddWithValue("$privileges", string.Join(',', principal.Privileges));
                insert.Parameters.AddWithValue(
                    "$senders", JsonSerializer.Serialize(principal.ApprovedSenderIdentities));
                insert.Parameters.AddWithValue("$createdAt", principal.CreatedAt.ToString("O"));
                insert.Parameters.AddWithValue("$createdBy", principal.CreatedBy);

                insert.ExecuteNonQuery();
            }

            BumpVersion(connection, transaction);
            transaction.Commit();
            return true;
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The newly minted principal could not be stored.", ex);
        }
    }

    /// <summary>
    /// Revokes the live key for a principal, or returns false when there is none.
    /// </summary>
    /// <remarks>
    /// <b>The stamp and the version bump commit together.</b> A revocation that committed without
    /// bumping the version would leave every process serving requests from a cache built before it,
    /// which is the same defect one layer down: a credential that still works for as long as a cache
    /// says so.
    /// </remarks>
    public bool Revoke(string principalId, string revokedBy, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(revokedBy);

        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            int revoked;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE host_principal
                    SET revoked_at = $at, revoked_by = $by
                    WHERE principal_id = $principal AND revoked_at IS NULL;
                    """;
                command.Parameters.AddWithValue("$at", now.ToString("O"));
                command.Parameters.AddWithValue("$by", revokedBy);
                command.Parameters.AddWithValue("$principal", principalId);

                revoked = command.ExecuteNonQuery();
            }

            if (revoked == 0)
            {
                return false;
            }

            BumpVersion(connection, transaction);
            transaction.Commit();
            return true;
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The principal could not be revoked.", ex);
        }
    }

    private static void BumpVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var bump = connection.CreateCommand();
        bump.Transaction = transaction;
        bump.CommandText = "UPDATE host_principal_store SET version = version + 1 WHERE id = 1;";
        bump.ExecuteNonQuery();
    }

    private const string SelectColumns =
        """
        SELECT key_id, principal_id, tenant_id, kdf_algorithm, kdf_iterations, salt, digest,
               privileges, approved_sender_identities, created_at, created_by, revoked_at, revoked_by
        FROM host_principal
        """;

    private static MintedPrincipalRecord Read(SqliteDataReader reader) => new()
    {
        Principal = new MintedPrincipal
        {
            KeyId = reader.GetString(0),
            PrincipalId = reader.GetString(1),
            TenantId = reader.GetString(2),
            Privileges = Split(reader.GetString(7)),
            ApprovedSenderIdentities = Deserialize(reader.GetString(8)),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(9), null),
            CreatedBy = reader.GetString(10),
            RevokedAt = reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11), null),
            RevokedBy = reader.IsDBNull(12) ? null : reader.GetString(12),
        },
        Material = new StoredKeyMaterial
        {
            Algorithm = reader.GetString(3),
            Iterations = reader.GetInt32(4),
            Salt = reader.GetFieldValue<byte[]>(5),
            Digest = reader.GetFieldValue<byte[]>(6),
        },
    };

    private static IReadOnlyList<string> Split(string value) => value.Length == 0
        ? []
        : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static IReadOnlyList<string> Deserialize(string json) =>
        JsonSerializer.Deserialize<string[]>(json) ?? [];
}
