using System.Text;
using System.Security.Cryptography;
using System.Buffers.Binary;
using StyloMail.AccessProxy.Credentials;

namespace StyloMail.AccessProxy.Accounts;

/// <summary>Derives the tenant-keyed pseudonym used for durable login lookup.</summary>
/// <remarks>
/// The same deriver instance and normalization rule must be used at enrolment and client-login
/// lookup. A mismatch makes every valid password appear unknown. Rotation is an operator migration:
/// it invalidates stored keys until every row is re-derived under the old and new keyed-HMAC policy;
/// it is never a configuration toggle.
/// </remarks>
public interface IProxyLoginKeyDeriver
{
    string Derive(string tenantId, string normalizedLogin);
}

/// <summary>HMAC-SHA-256 login pseudonym derivation using Host-held key material.</summary>
/// <remarks>The key must come from a Host secret provider and must not be persisted beside login keys.</remarks>
public sealed class HmacProxyLoginKeyDeriver : IProxyLoginKeyDeriver, IDisposable
{
    private byte[]? _key;

    public HmacProxyLoginKeyDeriver(byte[] hostSecretKey)
    {
        ArgumentNullException.ThrowIfNull(hostSecretKey);
        if (hostSecretKey.Length < 32) throw new ArgumentException("HMAC key must be at least 256 bits.", nameof(hostSecretKey));
        _key = hostSecretKey.ToArray();
    }

    public string Derive(string tenantId, string normalizedLogin)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(normalizedLogin);
        var key = _key ?? throw new ObjectDisposedException(nameof(HmacProxyLoginKeyDeriver));
        var tenant = Encoding.UTF8.GetBytes(tenantId);
        var login = Encoding.UTF8.GetBytes(normalizedLogin);
        var input = new byte[8 + tenant.Length + login.Length];
        try
        {
            BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(), tenant.Length);
            tenant.CopyTo(input, 4);
            BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(4 + tenant.Length), login.Length);
            login.CopyTo(input, 8 + tenant.Length);
            using var hmac = new HMACSHA256(key);
            return Convert.ToHexString(hmac.ComputeHash(input));
        }
        finally { CryptographicOperations.ZeroMemory(input); }
    }

    public void Dispose()
    {
        if (_key is { } key) CryptographicOperations.ZeroMemory(key);
        _key = null;
    }
}

/// <summary>Converts secret-bearing enrolment input into one protected atomic persistence request.</summary>
public sealed class ProxyEnrollmentService : IProxyEnrollmentService
{
    private readonly IProxyEnrollmentPersistence _persistence;
    private readonly IStyloMailPasswordHasher _passwordHasher;
    private readonly ISecretProtector _protector;
    private readonly IProxyLoginKeyDeriver _loginKeys;
    private readonly TimeProvider _clock;

    public ProxyEnrollmentService(IProxyEnrollmentPersistence persistence, IStyloMailPasswordHasher passwordHasher,
        ISecretProtector protector, IProxyLoginKeyDeriver loginKeys, TimeProvider clock)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _loginKeys = loginKeys ?? throw new ArgumentNullException(nameof(loginKeys));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask EnrolAsync(ProxyEnrollmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.TenantId);
        ArgumentException.ThrowIfNullOrEmpty(request.AccountId);
        ArgumentException.ThrowIfNullOrEmpty(request.BackendAccountId);
        ArgumentException.ThrowIfNullOrEmpty(request.ProviderId);
        ArgumentException.ThrowIfNullOrEmpty(request.Discriminator);
        ArgumentException.ThrowIfNullOrEmpty(request.BackendUsername);
        ArgumentNullException.ThrowIfNull(request.ClientPassword);
        ArgumentNullException.ThrowIfNull(request.BackendSecret);

        using (request.ClientPassword)
        using (request.BackendSecret)
        {
            var normalizedLogin = request.Login.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
            var now = _clock.GetUtcNow();
            var encrypted = _protector.Protect(request.BackendSecret,
                new SecretBinding(request.TenantId, request.BackendAccountId, request.Discriminator));
            var enrollment = new ProxyEnrollment
            {
                Account = new ProxyAccountEnrollment
                {
                    TenantId = request.TenantId,
                    AccountId = request.AccountId,
                    LoginKey = _loginKeys.Derive(request.TenantId, normalizedLogin),
                    PasswordHash = _passwordHasher.Hash(request.ClientPassword.Utf8),
                    BackendAccountId = request.BackendAccountId,
                    Enabled = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                Credential = new ProxyBackendCredentialEnrollment
                {
                    TenantId = request.TenantId,
                    BackendAccountId = request.BackendAccountId,
                    ProviderId = request.ProviderId,
                    Discriminator = request.Discriminator,
                    ProtectedSecret = encrypted.Ciphertext,
                    ProtectionKeyId = encrypted.KeyId,
                    BackendUsername = request.BackendUsername,
                    GrantedScopes = request.GrantedScopes,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
            };
            await _persistence.EnrolAsync(enrollment, cancellationToken).ConfigureAwait(false);
        }
    }
}
