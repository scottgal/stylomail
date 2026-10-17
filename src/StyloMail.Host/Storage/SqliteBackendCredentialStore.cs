using System.Globalization;
using Microsoft.Data.Sqlite;
using StyloMail.AccessProxy.Credentials;

namespace StyloMail.Host.Storage;

/// <summary>
/// The durable, tenant-scoped backend credential store.
/// </summary>
/// <remarks>
/// <para>
/// <c>revoked_at IS NOT NULL</c> is the only revocation state. There is deliberately no boolean
/// beside it: two representations of one fact eventually disagree, and the one that disagrees will
/// be the one a resolver reads.
/// </para>
/// <para>
/// <see cref="FindAsync"/> returns the revoked row rather than null. A revoked credential exists and
/// must fail closed with a reason ("this was revoked"), which
/// <c>BackendCredentialResolver</c> enforces once, with no retry. Returning null would make
/// "revoked" indistinguishable from "never enrolled" and invite a caller to create it again.
/// </para>
/// </remarks>
public sealed class SqliteBackendCredentialStore : IBackendCredentialStore
{
    private readonly HostDatabase _database;
    private readonly ISecretProtector _protector;
    private readonly TimeProvider _clock;

    public SqliteBackendCredentialStore(HostDatabase database, ISecretProtector protector, TimeProvider clock)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask<BackendCredentialRecord?> FindAsync(
        string tenantId,
        string backendAccountId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(backendAccountId);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT backend_account_id,tenant_id,provider_id,discriminator,protected_secret,"
            + "protection_key_id,backend_username,granted_scopes,created_at,updated_at,revoked_at "
            + "FROM proxy_backend_credential WHERE tenant_id=$tenant AND backend_account_id=$id;";
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$id", backendAccountId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return ValueTask.FromResult<BackendCredentialRecord?>(null);
        }

        return ValueTask.FromResult<BackendCredentialRecord?>(new BackendCredentialRecord
        {
            AccountId = reader.GetString(0),
            TenantId = reader.GetString(1),
            ProviderId = reader.GetString(2),
            Discriminator = reader.GetString(3),
            ProtectedSecret = (byte[])reader[4],
            ProtectionKeyId = reader.GetString(5),
            BackendUsername = reader.GetString(6),
            GrantedScopes = reader.IsDBNull(7) ? null : reader.GetString(7),
            CreatedAt = ParseTimestamp(reader.GetString(8)),
            UpdatedAt = ParseTimestamp(reader.GetString(9)),
            Revoked = !reader.IsDBNull(10),
        });
    }

    public ValueTask<BackendCredentialRecord> StoreAsync(
        BackendCredentialWrite write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();

        var binding = new SecretBinding(write.TenantId, write.AccountId, write.Discriminator);
        var protectedSecret = _protector.Protect(write.Secret, binding);
        var now = write.TimeProvider.GetUtcNow();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        // Upsert rather than insert: a re-enrolment replaces the material and clears the revocation
        // marker, which is the one path allowed to do so. The binding includes tenant, account and
        // discriminator, so a re-labelled row is undecryptable rather than silently reinterpreted.
        command.CommandText =
            "INSERT INTO proxy_backend_credential "
            + "(backend_account_id,tenant_id,provider_id,discriminator,protected_secret,protection_key_id,"
            + "backend_username,granted_scopes,created_at,updated_at,revoked_at) "
            + "VALUES ($id,$tenant,$provider,$kind,$secret,$key,$user,$scopes,$created,$updated,NULL) "
            + "ON CONFLICT DO UPDATE SET provider_id=excluded.provider_id,discriminator=excluded.discriminator,"
            + "protected_secret=excluded.protected_secret,protection_key_id=excluded.protection_key_id,"
            + "backend_username=excluded.backend_username,granted_scopes=excluded.granted_scopes,"
            + "updated_at=excluded.updated_at,revoked_at=NULL;";
        command.Parameters.AddWithValue("$id", write.AccountId);
        command.Parameters.AddWithValue("$tenant", write.TenantId);
        command.Parameters.AddWithValue("$provider", write.ProviderId);
        command.Parameters.AddWithValue("$kind", write.Discriminator);
        command.Parameters.Add("$secret", SqliteType.Blob).Value = protectedSecret.Ciphertext;
        command.Parameters.AddWithValue("$key", protectedSecret.KeyId);
        command.Parameters.AddWithValue("$user", write.BackendUsername);
        command.Parameters.AddWithValue("$scopes", (object?)write.GrantedScopes ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", Timestamp(now));
        command.Parameters.AddWithValue("$updated", Timestamp(now));
        command.ExecuteNonQuery();

        return ValueTask.FromResult(new BackendCredentialRecord
        {
            AccountId = write.AccountId,
            TenantId = write.TenantId,
            ProviderId = write.ProviderId,
            Discriminator = write.Discriminator,
            ProtectedSecret = protectedSecret.Ciphertext,
            ProtectionKeyId = protectedSecret.KeyId,
            BackendUsername = write.BackendUsername,
            GrantedScopes = write.GrantedScopes,
            CreatedAt = now,
            UpdatedAt = now,
            Revoked = false,
        });
    }

    public ValueTask<bool> RevokeAsync(
        string tenantId,
        string backendAccountId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(backendAccountId);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        // COALESCE keeps the first revocation instant, so a repeated revoke is idempotent and does
        // not rewrite when the credential stopped being usable.
        command.CommandText =
            "UPDATE proxy_backend_credential SET revoked_at=COALESCE(revoked_at,$at),updated_at=$at "
            + "WHERE tenant_id=$tenant AND backend_account_id=$id;";
        command.Parameters.AddWithValue("$at", Timestamp(_clock.GetUtcNow()));
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$id", backendAccountId);
        return ValueTask.FromResult(command.ExecuteNonQuery() == 1);
    }

    private static string Timestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
