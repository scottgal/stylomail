using System.Security.Cryptography;

namespace StyloMail.AccessProxy.Accounts;

/// <summary>
/// A StyloMail-issued client credential: the username and password a user configures in their mail
/// client.
/// </summary>
/// <remarks>
/// <b>This is the first of the two separate secrets.</b> Spec §9.4 item 4 requires that the client's
/// StyloMail-side password and the backend credential be separate secrets with separate rotation,
/// and that neither be derivable from the other. That is enforced by construction here rather than
/// by convention:
///
/// <list type="bullet">
/// <item>This record holds a <see cref="PasswordHash"/>, never a password. The plaintext client
/// password is verified and discarded at the session boundary; it is never stored, never forwarded,
/// and never used to derive anything.</item>
/// <item>The backend credential lives in a different store entirely (see
/// <c>IBackendCredentialStore</c>), encrypted under a different scheme with a different key. Nothing
/// in this type can reach it.</item>
/// <item><see cref="BackendAccountId"/> is a <em>reference</em> — a pointer to which backend
/// credential to use — not material. Holding this record tells you nothing about the backend
/// secret.</item>
/// </list>
///
/// <para>
/// The consequence that matters for rotation: revoking or re-issuing either secret does not disturb
/// the other. A user who changes their client password keeps their Gmail connection; a tenant that
/// rotates onto OAuth does not force every user to re-enrol their client. Had the two been derived
/// from a common value — which is the tempting shortcut when both are "the credential for this
/// account" — both rotations would cascade.
/// </para>
/// </remarks>
public sealed record StyloMailAccount
{
    /// <summary>StyloMail's account id. The key the backend credential store is also indexed by.</summary>
    public required string AccountId { get; init; }

    public required string TenantId { get; init; }

    /// <summary>
    /// The full client-facing login, e.g. <c>alice@example.com</c>. This is what the user types into
    /// their client as the username.
    /// </summary>
    public required string Login { get; init; }

    /// <summary>
    /// The verifier for the client password. Never the password, and never reversible to it.
    /// </summary>
    public required PasswordHash PasswordHash { get; init; }

    /// <summary>
    /// Which backend credential this account authenticates with. A reference, not material.
    /// </summary>
    /// <remarks>
    /// Kept as a separate field rather than assuming it equals <see cref="AccountId"/>, so an
    /// account can be migrated between backend credentials — or point at a credential shared within
    /// a tenant — without rewriting client logins.
    /// </remarks>
    public required string BackendAccountId { get; init; }

    /// <summary>True when the account may not start sessions. A disabled account fails closed.</summary>
    public bool Disabled { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// A PBKDF2 password verifier.
/// </summary>
/// <remarks>
/// Iteration count and salt travel with the hash so the cost can be raised over time without a
/// migration: a record written under an older policy still verifies, and can be re-hashed on the
/// next successful login when the stored parameters fall below the current policy.
/// </remarks>
public readonly record struct PasswordHash(int Iterations, byte[] Salt, byte[] Hash)
{
    /// <summary>Redacts the verifier, for the same reason <c>SecretValue</c> does.</summary>
    public override string ToString() => $"PasswordHash {{ Iterations = {Iterations }, Hash = [redacted] }}";
}

/// <summary>Hashes and verifies StyloMail-issued client passwords.</summary>
/// <remarks>
/// Everything here works on UTF-8 bytes rather than on a <see cref="string"/>. That is not
/// incidental: a <c>string</c> is immutable, so a password that reaches one cannot be zeroed and
/// sits in managed memory until the GC happens to collect it — which on a long-lived mail session
/// could be hours. Taking a span lets the caller hand over the bytes it already holds and zero them
/// the moment verification is done.
/// </remarks>
public interface IStyloMailPasswordHasher
{
    PasswordHash Hash(ReadOnlySpan<byte> utf8Password);

    /// <summary>
    /// Verifies <paramref name="utf8Password"/> against <paramref name="stored"/>, in constant time.
    /// </summary>
    bool Verify(ReadOnlySpan<byte> utf8Password, PasswordHash stored);

    /// <summary>
    /// A verifier no password matches, used to equalise timing when the login is unknown.
    /// </summary>
    /// <remarks>
    /// Exposed on the interface so a caller cannot accidentally take the fast path for an unknown
    /// account. See the implementing type's remarks for the oracle this closes.
    /// </remarks>
    PasswordHash DecoyVerifier { get; }
}

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing for StyloMail-issued client credentials.
/// </summary>
/// <remarks>
/// PBKDF2 rather than a memory-hard function (Argon2, scrypt) because the framework ships it and
/// this project takes no third-party dependencies. That is a real trade-off and worth stating
/// plainly: a memory-hard function is meaningfully better against GPU cracking, so the honest
/// position is that this is adequate and not ideal, and the upgrade path is a new hash algorithm
/// behind the same <see cref="IStyloMailPasswordHasher"/> seam — which the stored iteration count
/// and salt already anticipate.
///
/// <para>
/// <b>Verification bounds its own work.</b> The iteration count comes out of the stored record, so a
/// record demanding ten billion iterations would let anyone who can write to the account store turn
/// every login attempt into a CPU denial of service. <see cref="MaxIterations"/> caps that. It is a
/// bound on work, and like every bound in this subsystem it exists to be hit deliberately in a test
/// rather than discovered in production.
/// </para>
/// </remarks>
public sealed class Pbkdf2StyloMailPasswordHasher : IStyloMailPasswordHasher
{
    /// <summary>Current policy cost. Adjusted upward over time; existing records still verify.</summary>
    public const int DefaultIterations = 210_000;

    /// <summary>Ceiling on the iterations a stored record may request. See the type remarks.</summary>
    public const int MaxIterations = 2_000_000;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly int _iterations;

    public Pbkdf2StyloMailPasswordHasher(int iterations = DefaultIterations)
    {
        if (iterations < 1 || iterations > MaxIterations)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), iterations, "Outside the permitted range.");
        }

        _iterations = iterations;
    }

    public PasswordHash Hash(ReadOnlySpan<byte> utf8Password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(utf8Password, salt, _iterations, HashAlgorithmName.SHA256, HashBytes);
        return new PasswordHash(_iterations, salt, hash);
    }

    public bool Verify(ReadOnlySpan<byte> utf8Password, PasswordHash stored)
    {
        if (stored.Hash is null || stored.Salt is null || stored.Hash.Length == 0)
        {
            return false;
        }

        var iterations = Math.Clamp(stored.Iterations, 1, MaxIterations);
        var candidate = Rfc2898DeriveBytes.Pbkdf2(
            utf8Password, stored.Salt, iterations, HashAlgorithmName.SHA256, stored.Hash.Length);

        // Constant time, so a wrong password cannot be narrowed down by timing the comparison.
        return CryptographicOperations.FixedTimeEquals(candidate, stored.Hash);
    }

    /// <summary>
    /// A verifier for a password that was never supplied, used to equalise timing.
    /// </summary>
    /// <remarks>
    /// <b>This closes an account-enumeration oracle.</b> The obvious implementation looks the login
    /// up, returns early when it is unknown, and only then runs PBKDF2 — which means an unknown
    /// login answers in microseconds while a known one takes the full derivation cost. An attacker
    /// who can time the response learns which logins exist, and on a mail proxy the login is the
    /// user's email address, so that is a tenant directory leak.
    ///
    /// <para>
    /// Verifying against this constant when the account is missing makes both paths pay the same
    /// derivation. It is a fixed, published value at the current policy cost — not a secret, and
    /// worthless as a credential, since no login is bound to it.
    /// </para>
    /// </remarks>
    public PasswordHash DecoyVerifier { get; } = CreateDecoy();

    private static PasswordHash CreateDecoy()
    {
        // Deterministic, so every process pays exactly the same cost and the value is not a secret.
        var salt = new byte[SaltBytes];
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            "decoy"u8, salt, DefaultIterations, HashAlgorithmName.SHA256, HashBytes);
        return new PasswordHash(DefaultIterations, salt, hash);
    }
}

/// <summary>Where StyloMail-issued client accounts live.</summary>
public interface IStyloMailAccountStore
{
    /// <summary>The account for a client-supplied login, or null when unknown.</summary>
    ValueTask<StyloMailAccount?> FindByLoginAsync(string login, CancellationToken cancellationToken);

    ValueTask<StyloMailAccount?> FindByIdAsync(string accountId, CancellationToken cancellationToken);
}

/// <summary>
/// An in-process account store.
/// </summary>
/// <remarks>
/// Not durable — the same limitation as <c>InMemoryBackendCredentialStore</c>, and reported for the
/// same reason: the durable implementation belongs with the host's persistence layer, not here.
/// What it does exercise is the real hasher, so a test asserting "the stored record is not the
/// password" is testing production code paths.
/// </remarks>
public sealed class InMemoryStyloMailAccountStore : IStyloMailAccountStore
{
    private readonly Dictionary<string, StyloMailAccount> _byLogin = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StyloMailAccount> _byId = new(StringComparer.Ordinal);

    public ValueTask<StyloMailAccount?> FindByLoginAsync(string login, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(login);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_byLogin.TryGetValue(login, out var account) ? account : null);
    }

    public ValueTask<StyloMailAccount?> FindByIdAsync(string accountId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_byId.TryGetValue(accountId, out var account) ? account : null);
    }

    public void Add(StyloMailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        _byLogin[account.Login] = account;
        _byId[account.AccountId] = account;
    }
}
