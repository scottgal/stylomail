using Microsoft.Data.Sqlite;
using StyloMail.AccessProxy.Accounts;

namespace StyloMail.Host.Storage;

/// <summary>Atomically persists an access-owned, already protected proxy enrolment.</summary>
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
        if (!string.Equals(account.TenantId, credential.TenantId, StringComparison.Ordinal))
            throw new ArgumentException("Account and credential tenant must match.", nameof(enrollment));
        if (!string.Equals(account.BackendAccountId, credential.BackendAccountId, StringComparison.Ordinal))
            throw new ArgumentException("Account and credential backend account must match.", nameof(enrollment));
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using var credentialInsert = connection.CreateCommand();
        credentialInsert.Transaction = transaction;
        credentialInsert.CommandText = "INSERT INTO proxy_backend_credential (backend_account_id,tenant_id,provider_id,discriminator,protected_secret,protection_key_id,backend_username,granted_scopes,created_at,updated_at,revoked_at) VALUES ($id,$tenant,$provider,$kind,$secret,$key,$user,$scopes,$created,$updated,NULL);";
        credentialInsert.Parameters.AddWithValue("$id", credential.BackendAccountId); credentialInsert.Parameters.AddWithValue("$tenant", credential.TenantId); credentialInsert.Parameters.AddWithValue("$provider", credential.ProviderId); credentialInsert.Parameters.AddWithValue("$kind", credential.Discriminator); credentialInsert.Parameters.Add("$secret", SqliteType.Blob).Value = credential.ProtectedSecret; credentialInsert.Parameters.AddWithValue("$key", credential.ProtectionKeyId); credentialInsert.Parameters.AddWithValue("$user", credential.BackendUsername); credentialInsert.Parameters.AddWithValue("$scopes", (object?)credential.GrantedScopes ?? DBNull.Value); credentialInsert.Parameters.AddWithValue("$created", credential.CreatedAt.ToUniversalTime().ToString("O")); credentialInsert.Parameters.AddWithValue("$updated", credential.UpdatedAt.ToUniversalTime().ToString("O"));
        credentialInsert.ExecuteNonQuery();
        using var accountInsert = connection.CreateCommand(); accountInsert.Transaction = transaction;
        accountInsert.CommandText = "INSERT INTO proxy_account (account_id,tenant_id,login_key,password_hash_iterations,password_hash_salt,password_hash_digest,backend_account_id,disabled,created_at,updated_at) VALUES ($id,$tenant,$login,$iterations,$salt,$digest,$backend,$disabled,$created,$updated);";
        accountInsert.Parameters.AddWithValue("$id", account.AccountId); accountInsert.Parameters.AddWithValue("$tenant", account.TenantId); accountInsert.Parameters.AddWithValue("$login", account.LoginKey); accountInsert.Parameters.AddWithValue("$iterations", account.PasswordHash.Iterations); accountInsert.Parameters.Add("$salt", SqliteType.Blob).Value = account.PasswordHash.Salt; accountInsert.Parameters.Add("$digest", SqliteType.Blob).Value = account.PasswordHash.Hash; accountInsert.Parameters.AddWithValue("$backend", account.BackendAccountId); accountInsert.Parameters.AddWithValue("$disabled", account.Enabled ? 0 : 1); accountInsert.Parameters.AddWithValue("$created", account.CreatedAt.ToUniversalTime().ToString("O")); accountInsert.Parameters.AddWithValue("$updated", account.UpdatedAt.ToUniversalTime().ToString("O")); accountInsert.ExecuteNonQuery();
        transaction.Commit(); return ValueTask.CompletedTask;
    }
}
