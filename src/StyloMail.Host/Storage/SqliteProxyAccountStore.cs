using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using StyloMail.AccessProxy.Accounts;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Proxy;

/// <summary>
/// The durable, tenant-qualified account store the access proxy authenticates clients against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lookup uses the same derivation as enrolment.</b> The login is normalized exactly as
/// <c>ProxyEnrollmentService</c> normalizes it (trim, Unicode FormKC, upper invariant) and then
/// passed through the one shared <see cref="IProxyLoginKeyDeriver"/>. A second derivation, or a
/// second normalization rule, would make every correct password look unknown and every row
/// unusable, and the failure would present as "wrong password" rather than as a configuration
/// mismatch.
/// </para>
/// <para>
/// The store deliberately returns a disabled account rather than null. The authentication boundary
/// (<c>AccessProxySessionBase</c>) runs a decoy PBKDF2 verification for both a missing and a disabled
/// account and returns one undifferentiated failure, so timing and reason cannot separate "no such
/// account" from "disabled". Filtering here would move that decision into storage and quietly make
/// the two paths distinguishable again.
/// </para>
/// </remarks>
public sealed class SqliteProxyAccountStore : IStyloMailAccountStore, IProxyAccountDirectory
{
    private readonly HostDatabase _database;
    private readonly IProxyLoginKeyDeriver _loginKeys;

    public SqliteProxyAccountStore(HostDatabase database, IProxyLoginKeyDeriver loginKeys)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _loginKeys = loginKeys ?? throw new ArgumentNullException(nameof(loginKeys));
    }

    public ValueTask<StyloMailAccount?> FindByLoginAsync(
        string tenantId,
        string login,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(login);
        cancellationToken.ThrowIfCancellationRequested();

        // Must match ProxyEnrollmentService.EnrolAsync exactly. Kept as one expression with a test
        // that enrols and authenticates through the same path, because two copies of a normalization
        // rule are two rules waiting to differ.
        var normalized = login.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        var loginKey = _loginKeys.Derive(tenantId, normalized);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT account_id,tenant_id,backend_account_id,password_hash_iterations,password_hash_salt,"
            + "password_hash_digest,disabled,created_at,updated_at "
            + "FROM proxy_account WHERE tenant_id=$tenant AND login_key=$login;";
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$login", loginKey);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return ValueTask.FromResult<StyloMailAccount?>(null);
        }

        // The raw login is not stored, so the record carries the pseudonym where the type's Login
        // field sits. Nothing in the session path echoes it: it is used as a lookup handle only.
        return ValueTask.FromResult<StyloMailAccount?>(new StyloMailAccount
        {
            AccountId = reader.GetString(0),
            TenantId = reader.GetString(1),
            Login = loginKey,
            BackendAccountId = reader.GetString(2),
            PasswordHash = new PasswordHash(
                reader.GetInt32(3),
                (byte[])reader[4],
                (byte[])reader[5]),
            Disabled = reader.GetInt32(6) != 0,
            CreatedAt = ParseTimestamp(reader.GetString(7)),
            UpdatedAt = ParseTimestamp(reader.GetString(8)),
        });
    }

    public ValueTask<StyloMailAccount?> FindByIdAsync(
        string tenantId,
        string accountId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT account_id,tenant_id,login_key,backend_account_id,password_hash_iterations,"
            + "password_hash_salt,password_hash_digest,disabled,created_at,updated_at "
            + "FROM proxy_account WHERE tenant_id=$tenant AND account_id=$id;";
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$id", accountId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return ValueTask.FromResult<StyloMailAccount?>(null);
        }

        return ValueTask.FromResult<StyloMailAccount?>(new StyloMailAccount
        {
            AccountId = reader.GetString(0),
            TenantId = reader.GetString(1),
            Login = reader.GetString(2),
            BackendAccountId = reader.GetString(3),
            PasswordHash = new PasswordHash(
                reader.GetInt32(4),
                (byte[])reader[5],
                (byte[])reader[6]),
            Disabled = reader.GetInt32(7) != 0,
            CreatedAt = ParseTimestamp(reader.GetString(8)),
            UpdatedAt = ParseTimestamp(reader.GetString(9)),
        });
    }

    public ValueTask<ProxyAccountRecord?> GetAsync(
        string tenantId,
        string accountId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectAccounts
            + " WHERE a.tenant_id=$tenant AND a.account_id=$id;";
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$id", accountId);

        using var reader = command.ExecuteReader();
        return ValueTask.FromResult<ProxyAccountRecord?>(reader.Read() ? ReadAccount(reader) : null);
    }

    public ValueTask<ProxyAccountPage> ListAsync(
        string tenantId,
        int limit,
        string? afterAccountId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectAccounts
            + " WHERE a.tenant_id=$tenant AND ($after IS NULL OR a.account_id > $after)"
            + " ORDER BY a.account_id LIMIT $limit;";
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$after", (object?)afterAccountId ?? DBNull.Value);
        // One extra row is the page's lookahead: it answers "is there more" without a second query
        // whose answer could disagree with the page it describes.
        command.Parameters.AddWithValue("$limit", limit + 1);

        using var reader = command.ExecuteReader();
        var rows = new List<ProxyAccountRecord>();
        while (reader.Read())
        {
            rows.Add(ReadAccount(reader));
        }

        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return ValueTask.FromResult(new ProxyAccountPage(rows, hasMore));
    }

    private const string SelectAccounts =
        "SELECT a.account_id,a.login_key,a.disabled,c.provider_id,c.discriminator,c.revoked_at,"
        + "a.created_at,a.updated_at,a.backend_account_id "
        + "FROM proxy_account a "
        + "JOIN proxy_backend_credential c "
        + "ON c.backend_account_id=a.backend_account_id AND c.tenant_id=a.tenant_id";

    private static ProxyAccountRecord ReadAccount(SqliteDataReader reader) => new()
    {
        AccountId = reader.GetString(0),
        LoginKey = reader.GetString(1),
        Enabled = reader.GetInt32(2) == 0,
        ProviderId = reader.GetString(3),
        CredentialDiscriminator = reader.GetString(4),
        RevokedAt = reader.IsDBNull(5) ? null : ParseTimestamp(reader.GetString(5)),
        CreatedAt = ParseTimestamp(reader.GetString(6)),
        UpdatedAt = ParseTimestamp(reader.GetString(7)),
        BackendAccountId = reader.GetString(8),
        EnrollmentState = reader.IsDBNull(5) ? "active" : "revoked",
    };

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
