using Microsoft.Data.Sqlite;
using StyloMail.AccessProxy.Accounts;

namespace StyloMail.Host.Storage;

/// <summary>Atomically persists an access-owned, already protected proxy enrolment.</summary>
/// <remarks>
/// <para>
/// Every rejection is one of the declared <see cref="ProxyEnrollmentConflict"/> values. A raw
/// <see cref="SqliteException"/> must not escape: the route layer maps a conflict to a status code,
/// and a database error it has never seen becomes a 500 that says nothing about what went wrong.
/// </para>
/// <para>
/// A missing row and a revoked credential are different answers. Missing means the target does not
/// exist (<see cref="ProxyEnrollmentConflict.AccountNotFound"/> or
/// <see cref="ProxyEnrollmentConflict.CredentialNotFound"/>); revoked means it exists but must not
/// be used (<see cref="ProxyEnrollmentConflict.BackendCredentialUnavailable"/>), which is the case
/// that fails closed and must not be retried.
/// </para>
/// </remarks>
public sealed class SqliteProxyEnrollmentPersistence : IProxyEnrollmentPersistence
{
    private readonly HostDatabase _database;

    public SqliteProxyEnrollmentPersistence(HostDatabase database) => _database = database;

    public ValueTask EnrolAsync(ProxyEnrollment enrollment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        cancellationToken.ThrowIfCancellationRequested();
        var account = enrollment.Account;
        var credential = enrollment.Credential;

        RequireMatchingTenant(account.TenantId, credential.TenantId);
        RequireMatchingBackend(account.BackendAccountId, credential.BackendAccountId);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        RejectDuplicateAccount(connection, transaction, account.TenantId, account.AccountId, account.LoginKey);
        RejectDuplicateCredential(connection, transaction, credential.TenantId, credential.BackendAccountId);

        InsertCredential(connection, transaction, credential);
        InsertAccount(connection, transaction, account);
        transaction.Commit();
        return ValueTask.CompletedTask;
    }

    public ValueTask SetAccountEnabledAsync(ProxyAccountStateChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE proxy_account SET disabled=$disabled,updated_at=$at WHERE tenant_id=$tenant AND account_id=$id;";
        command.Parameters.AddWithValue("$tenant", change.TenantId);
        command.Parameters.AddWithValue("$id", change.AccountId);
        command.Parameters.AddWithValue("$disabled", change.Enabled ? 0 : 1);
        command.Parameters.AddWithValue("$at", Timestamp(change.UpdatedAt));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new ProxyEnrollmentConflictException(ProxyEnrollmentConflict.AccountNotFound);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RevokeBackendCredentialAsync(ProxyBackendCredentialRevocation revocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revocation);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        // COALESCE keeps the first revocation instant: a repeated revoke is idempotent and does not
        // rewrite when the credential stopped being usable.
        command.CommandText = "UPDATE proxy_backend_credential SET revoked_at=COALESCE(revoked_at,$at),updated_at=$at WHERE tenant_id=$tenant AND backend_account_id=$id;";
        command.Parameters.AddWithValue("$tenant", revocation.TenantId);
        command.Parameters.AddWithValue("$id", revocation.BackendAccountId);
        command.Parameters.AddWithValue("$at", Timestamp(revocation.RevokedAt));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new ProxyEnrollmentConflictException(ProxyEnrollmentConflict.CredentialNotFound);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ReenrolAsync(ProxyEnrollment enrollment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        cancellationToken.ThrowIfCancellationRequested();
        var account = enrollment.Account;
        var credential = enrollment.Credential;

        RequireMatchingTenant(account.TenantId, credential.TenantId);
        RequireMatchingBackend(account.BackendAccountId, credential.BackendAccountId);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        using var credentialUpdate = connection.CreateCommand();
        credentialUpdate.Transaction = transaction;
        // Re-enrolment replaces the credential material and deliberately clears revoked_at: the
        // operator has supplied a live credential, so the revocation marker no longer describes it.
        // This is the one path that may clear the marker; rewrap never does.
        credentialUpdate.CommandText = "UPDATE proxy_backend_credential SET protected_secret=$secret,protection_key_id=$key,backend_username=$user,granted_scopes=$scopes,updated_at=$at,revoked_at=NULL WHERE tenant_id=$tenant AND backend_account_id=$id;";
        credentialUpdate.Parameters.Add("$secret", SqliteType.Blob).Value = credential.ProtectedSecret;
        credentialUpdate.Parameters.AddWithValue("$key", credential.ProtectionKeyId);
        credentialUpdate.Parameters.AddWithValue("$user", credential.BackendUsername);
        credentialUpdate.Parameters.AddWithValue("$scopes", (object?)credential.GrantedScopes ?? DBNull.Value);
        credentialUpdate.Parameters.AddWithValue("$at", Timestamp(credential.UpdatedAt));
        credentialUpdate.Parameters.AddWithValue("$tenant", credential.TenantId);
        credentialUpdate.Parameters.AddWithValue("$id", credential.BackendAccountId);
        if (credentialUpdate.ExecuteNonQuery() != 1)
        {
            throw new ProxyEnrollmentConflictException(ProxyEnrollmentConflict.CredentialNotFound);
        }

        using var accountUpdate = connection.CreateCommand();
        accountUpdate.Transaction = transaction;
        accountUpdate.CommandText = "UPDATE proxy_account SET password_hash_iterations=$i,password_hash_salt=$salt,password_hash_digest=$digest,disabled=$disabled,updated_at=$at WHERE tenant_id=$tenant AND account_id=$id AND backend_account_id=$backend;";
        accountUpdate.Parameters.AddWithValue("$i", account.PasswordHash.Iterations);
        accountUpdate.Parameters.Add("$salt", SqliteType.Blob).Value = account.PasswordHash.Salt;
        accountUpdate.Parameters.Add("$digest", SqliteType.Blob).Value = account.PasswordHash.Hash;
        accountUpdate.Parameters.AddWithValue("$disabled", account.Enabled ? 0 : 1);
        accountUpdate.Parameters.AddWithValue("$at", Timestamp(account.UpdatedAt));
        accountUpdate.Parameters.AddWithValue("$tenant", account.TenantId);
        accountUpdate.Parameters.AddWithValue("$id", account.AccountId);
        accountUpdate.Parameters.AddWithValue("$backend", account.BackendAccountId);
        if (accountUpdate.ExecuteNonQuery() != 1)
        {
            throw new ProxyEnrollmentConflictException(ProxyEnrollmentConflict.AccountNotFound);
        }

        transaction.Commit();
        return ValueTask.CompletedTask;
    }

    public ValueTask RewrapBackendCredentialAsync(ProxyBackendCredentialRewrap rewrap, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rewrap);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.Open();
        using var state = connection.CreateCommand();
        state.CommandText = "SELECT revoked_at FROM proxy_backend_credential WHERE tenant_id=$tenant AND backend_account_id=$id;";
        state.Parameters.AddWithValue("$tenant", rewrap.TenantId);
        state.Parameters.AddWithValue("$id", rewrap.BackendAccountId);
        var revokedAt = state.ExecuteScalar();
        if (revokedAt is null)
        {
            throw new ProxyEnrollmentConflictException(ProxyEnrollmentConflict.CredentialNotFound);
        }

        if (revokedAt is not DBNull)
        {
            throw new ProxyEnrollmentConflictException(ProxyEnrollmentConflict.BackendCredentialUnavailable);
        }

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE proxy_backend_credential SET protected_secret=$secret,protection_key_id=$key,updated_at=$at WHERE tenant_id=$tenant AND backend_account_id=$id AND revoked_at IS NULL;";
        command.Parameters.Add("$secret", SqliteType.Blob).Value = rewrap.ProtectedSecret;
        command.Parameters.AddWithValue("$key", rewrap.ProtectionKeyId);
        command.Parameters.AddWithValue("$at", Timestamp(rewrap.UpdatedAt));
        command.Parameters.AddWithValue("$tenant", rewrap.TenantId);
        command.Parameters.AddWithValue("$id", rewrap.BackendAccountId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new ProxyEnrollmentConflictException(ProxyEnrollmentConflict.BackendCredentialUnavailable);
        }

        return ValueTask.CompletedTask;
    }

    private static void RequireMatchingTenant(string accountTenant, string credentialTenant)
    {
        if (!string.Equals(accountTenant, credentialTenant, StringComparison.Ordinal))
        {
            throw new ProxyEnrollmentConflictException(ProxyEnrollmentConflict.TenantMismatch);
        }
    }

    private static void RequireMatchingBackend(string accountBackend, string credentialBackend)
    {
        if (!string.Equals(accountBackend, credentialBackend, StringComparison.Ordinal))
        {
            throw new ProxyEnrollmentConflictException(ProxyEnrollmentConflict.TenantMismatch);
        }
    }

    private static void RejectDuplicateAccount(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string accountId,
        string loginKey)
    {
        using var byId = connection.CreateCommand();
        byId.Transaction = transaction;
        byId.CommandText = "SELECT tenant_id FROM proxy_account WHERE account_id=$id;";
        byId.Parameters.AddWithValue("$id", accountId);
        if (byId.ExecuteScalar() is not null)
        {
            // The account id is the primary key and therefore global, so a reused id is a duplicate
            // whether or not the tenant matches.
            throw new ProxyEnrollmentConflictException(ProxyEnrollmentConflict.DuplicateAccountId);
        }

        using var byLogin = connection.CreateCommand();
        byLogin.Transaction = transaction;
        byLogin.CommandText = "SELECT 1 FROM proxy_account WHERE tenant_id=$tenant AND login_key=$login;";
        byLogin.Parameters.AddWithValue("$tenant", tenantId);
        byLogin.Parameters.AddWithValue("$login", loginKey);
        if (byLogin.ExecuteScalar() is not null)
        {
            throw new ProxyEnrollmentConflictException(ProxyEnrollmentConflict.DuplicateLoginKey);
        }
    }

    private static void RejectDuplicateCredential(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string backendAccountId)
    {
        using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = "SELECT 1 FROM proxy_backend_credential WHERE tenant_id=$tenant AND backend_account_id=$id;";
        existing.Parameters.AddWithValue("$tenant", tenantId);
        existing.Parameters.AddWithValue("$id", backendAccountId);
        if (existing.ExecuteScalar() is not null)
        {
            // One credential per backend account. A second account cannot adopt an existing one
            // without a deliberate re-enrolment, so this is reported as the credential being
            // unavailable to this enrolment rather than as a duplicate account.
            throw new ProxyEnrollmentConflictException(ProxyEnrollmentConflict.BackendCredentialUnavailable);
        }
    }

    private static void InsertCredential(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProxyBackendCredentialEnrollment credential)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO proxy_backend_credential (backend_account_id,tenant_id,provider_id,discriminator,protected_secret,protection_key_id,backend_username,granted_scopes,created_at,updated_at,revoked_at) VALUES ($id,$tenant,$provider,$kind,$secret,$key,$user,$scopes,$created,$updated,NULL);";
        command.Parameters.AddWithValue("$id", credential.BackendAccountId);
        command.Parameters.AddWithValue("$tenant", credential.TenantId);
        command.Parameters.AddWithValue("$provider", credential.ProviderId);
        command.Parameters.AddWithValue("$kind", credential.Discriminator);
        command.Parameters.Add("$secret", SqliteType.Blob).Value = credential.ProtectedSecret;
        command.Parameters.AddWithValue("$key", credential.ProtectionKeyId);
        command.Parameters.AddWithValue("$user", credential.BackendUsername);
        command.Parameters.AddWithValue("$scopes", (object?)credential.GrantedScopes ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", Timestamp(credential.CreatedAt));
        command.Parameters.AddWithValue("$updated", Timestamp(credential.UpdatedAt));
        command.ExecuteNonQuery();
    }

    private static void InsertAccount(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProxyAccountEnrollment account)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO proxy_account (account_id,tenant_id,login_key,password_hash_iterations,password_hash_salt,password_hash_digest,backend_account_id,disabled,created_at,updated_at) VALUES ($id,$tenant,$login,$iterations,$salt,$digest,$backend,$disabled,$created,$updated);";
        command.Parameters.AddWithValue("$id", account.AccountId);
        command.Parameters.AddWithValue("$tenant", account.TenantId);
        command.Parameters.AddWithValue("$login", account.LoginKey);
        command.Parameters.AddWithValue("$iterations", account.PasswordHash.Iterations);
        command.Parameters.Add("$salt", SqliteType.Blob).Value = account.PasswordHash.Salt;
        command.Parameters.Add("$digest", SqliteType.Blob).Value = account.PasswordHash.Hash;
        command.Parameters.AddWithValue("$backend", account.BackendAccountId);
        command.Parameters.AddWithValue("$disabled", account.Enabled ? 0 : 1);
        command.Parameters.AddWithValue("$created", Timestamp(account.CreatedAt));
        command.Parameters.AddWithValue("$updated", Timestamp(account.UpdatedAt));
        command.ExecuteNonQuery();
    }

    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
}
