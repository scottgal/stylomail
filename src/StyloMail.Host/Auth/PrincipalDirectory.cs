using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Auth;

/// <summary>
/// Resolves an API key to the principal it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// Shared by every channel that accepts a key, so there is exactly one implementation of "is this
/// key valid and what may it do". Two channels each doing their own lookup is how a host ends up
/// with a cookie path that forgets one of the checks the header path makes.
/// </para>
/// <para>
/// <b>There are two sources of identity and the store wins, wholesale.</b> A key minted on this host
/// is held as a digest in <see cref="MintedPrincipalStore"/>; a principal configured in
/// <c>StyloMail:Auth:Principals</c> is still honoured, so existing deployments and the harness
/// scripts around them keep working. Where the same principal exists in both, the store entry
/// resolves and the configuration entry does not.
/// </para>
/// <para>
/// <b>Wholesale is the load-bearing word.</b> Merging the two would union privileges and union keys,
/// and a union is how a configuration entry silently re-widens a privilege an operator deliberately
/// narrowed when they minted its replacement. So an environment entry whose name the store has
/// claimed does not authenticate at all, with any key, and the claim stands even after the minted key
/// is revoked: revocation that handed authority back to a forgotten configuration entry would not be
/// revocation.
/// </para>
/// </remarks>
public sealed class PrincipalDirectory
{
    private readonly HostAuthOptions _options;
    private readonly MintedPrincipalStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger<PrincipalDirectory> _logger;

    /// <summary>
    /// Resolutions already made, keyed by a digest of the presented key.
    /// </summary>
    /// <remarks>
    /// <b>Keyed by a digest, and only in memory.</b> The presented value is what a slow KDF is meant
    /// to make expensive, and the whole reason verifying one costs 71 ms is that a stored digest must
    /// survive an attacker copying the database. Writing a cheap digest into a cache file would
    /// recreate exactly the artefact the KDF exists to avoid, so this map never leaves the process.
    /// </remarks>
    private readonly ConcurrentDictionary<string, CachedResolution> _cache = new(StringComparer.Ordinal);

    /// <summary>The store version <see cref="_cache"/> was built against.</summary>
    private long _cacheVersion = long.MinValue;

    public PrincipalDirectory(
        IOptions<HostAuthOptions> options,
        MintedPrincipalStore store,
        TimeProvider clock,
        ILogger<PrincipalDirectory> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _store = store;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Whether any principal can authenticate at all.</summary>
    public bool HasPrincipals => _options.Principals.Any(p => !string.IsNullOrEmpty(p.Key));

    public bool BrowserChannelEnabled => _options.EnableBrowserCookieChannel;

    /// <summary>
    /// The principals configured for one tenant, ordered by identifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Returns the configuration's view of a principal, never its credential.</b> Callers project
    /// what they need from <see cref="HostPrincipalOptions"/>; nothing here hands out a key, and a
    /// listing built from this must not either. The ordering is fixed so that a listing is stable
    /// across calls, an operator reading a sidebar should not see rows move because a dictionary
    /// enumeration changed.
    /// </para>
    /// <para>
    /// A principal configured without a key is skipped. It cannot authenticate, so it cannot send,
    /// and listing it as a sender would advertise an account that does not exist. An empty tenant
    /// returns an empty list rather than null: "this tenant has no senders" and "that question has
    /// no answer" must not look the same to a caller.
    /// </para>
    /// <para>
    /// <b>A principal the store has claimed is skipped for the same reason, and it is the same
    /// rule.</b> "Cannot authenticate, so it cannot send" is exactly what wholesale precedence makes
    /// of an environment entry with a minted twin, and a listing that went on presenting the
    /// configuration's version of it would describe privileges that no longer apply.
    /// </para>
    /// </remarks>
    public IReadOnlyList<HostPrincipalOptions> ForTenant(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var claimed = _store.ClaimedPrincipalIds();

        return
        [
            .. _options.Principals
                .Where(p => !string.IsNullOrEmpty(p.Key)
                    && string.Equals(p.TenantId, tenantId, StringComparison.Ordinal)
                    && !claimed.Contains(p.PrincipalId))
                .OrderBy(p => p.PrincipalId, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Every principal this host holds, from both sources, with which of them resolves.
    /// </summary>
    /// <remarks>
    /// The inventory behind <c>key list</c>. It reports provenance rather than reconciling the two
    /// sources, because the two sources exist and pretending otherwise is what makes a
    /// half-migrated deployment unreadable: an operator has to be able to see that a name is in both
    /// places and that only one of them is answering.
    /// </remarks>
    public IReadOnlyList<PrincipalInventoryEntry> Inventory()
    {
        var claimed = _store.ClaimedPrincipalIds();

        var entries = new List<PrincipalInventoryEntry>();

        foreach (var stored in _store.List())
        {
            entries.Add(new PrincipalInventoryEntry
            {
                PrincipalId = stored.PrincipalId,
                TenantId = stored.TenantId,
                Privileges = stored.Privileges,
                Source = PrincipalSource.Store,
                Status = stored.IsRevoked ? PrincipalStatus.Revoked : PrincipalStatus.Active,
            });
        }

        entries.AddRange(_options.Principals
            .Select((configured, index) => (configured, index))
            .Where(entry => !string.IsNullOrEmpty(entry.configured.Key))
            .OrderBy(entry => entry.configured.PrincipalId, StringComparer.Ordinal)
            .ThenBy(entry => entry.index)
            .Select(entry => new PrincipalInventoryEntry
            {
                PrincipalId = entry.configured.PrincipalId,
                TenantId = entry.configured.TenantId,
                Privileges = entry.configured.Privileges,
                Source = PrincipalSource.Environment,
                Status = claimed.Contains(entry.configured.PrincipalId)
                    ? PrincipalStatus.ShadowedByStore
                    : PrincipalStatus.ReadOnly,
                ConfiguredAt = Configure(index: entry.index),
            }));

        return entries;
    }

    /// <summary>
    /// Finds the principal a key belongs to, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The store is consulted first and the configuration is a fallback</b>, never a second chance
    /// for a principal the store already owns. Keys are described in full on the two paths that do
    /// the work: <see cref="ResolveFromStore"/> and <see cref="ResolveFromConfiguration"/>.
    /// </para>
    /// <para>
    /// <b>An unreadable store resolves nothing at all, including from configuration.</b> Without it
    /// there is no way to tell whether a configured principal has been replaced by a minted one or
    /// revoked, so answering from configuration alone would hand authority back to exactly the
    /// entries the store exists to override. Authentication that cannot be decided must not succeed.
    /// </para>
    /// </remarks>
    public HostPrincipal? Resolve(string? presentedKey)
    {
        if (string.IsNullOrEmpty(presentedKey))
        {
            return null;
        }

        long version;

        try
        {
            version = _store.Version();
        }
        catch (StorageUnavailableException ex)
        {
            _logger.LogError(
                ex,
                "The principal store could not be read, so no credential can be resolved. Every " +
                "request is refused rather than resolved from configuration alone: without the " +
                "store there is no way to tell whether a configured principal has been replaced or " +
                "revoked.");
            return null;
        }

        // A change anywhere invalidates the whole cache, and the change may well have happened in
        // another process: the CLI that revokes a key is not the server that served it. This is why
        // the cache validates against the store's own counter rather than trying to evict on write.
        if (Volatile.Read(ref _cacheVersion) != version)
        {
            _cache.Clear();
            Volatile.Write(ref _cacheVersion, version);
        }

        var cacheKey = CacheKey(presentedKey);
        var lifetime = _options.ResolutionCacheLifetime;
        var now = _clock.GetUtcNow();

        if (_cache.TryGetValue(cacheKey, out var cached) && now - cached.ResolvedAt < lifetime)
        {
            return cached.Principal;
        }

        HostPrincipal? resolved;

        try
        {
            resolved = ResolveFromStore(presentedKey) ?? ResolveFromConfiguration(presentedKey);
        }
        catch (StorageUnavailableException ex)
        {
            _logger.LogError(
                ex,
                "The principal store could not be read while resolving a credential; refusing it.");

            return null;
        }

        if (resolved is not null && lifetime > TimeSpan.Zero)
        {
            _cache[cacheKey] = new CachedResolution(resolved, now);
        }

        return resolved;
    }

    /// <summary>
    /// Resolves against the minted keys, which is the source that wins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One derivation, not one per principal.</b> A minted key names its own row, so the expensive
    /// KDF is reached only by a caller who already knows which row to attack, and a flood of
    /// unrecognised keys costs a failed lookup and no hashing at all. The alternative, deriving
    /// against every stored salt, would make the cost of a wrong key grow with the size of the
    /// deployment.
    /// </para>
    /// <para>
    /// A wrong secret and a row that does not verify return the same null. The caller is
    /// unauthenticated on both, and must not learn which one it produced.
    /// </para>
    /// </remarks>
    private HostPrincipal? ResolveFromStore(string presentedKey)
    {
        if (!MintedApiKey.TryReadKeyId(presentedKey, out var keyId))
        {
            return null;
        }

        var record = _store.FindByKeyId(keyId);

        if (record is null)
        {
            return null;
        }

        // Refused rather than attempted. A row naming a derivation this build does not implement is
        // either a store written by a newer host or an edited one, and in both cases the only honest
        // answer is that this host cannot verify it.
        if (!string.Equals(record.Material.Algorithm, MintedApiKey.Algorithm, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "A stored principal names an unrecognised key derivation ({Algorithm}); it cannot be "
                + "verified by this build and is refused.",
                record.Material.Algorithm);

            return null;
        }

        if (record.Material.Iterations <= 0)
        {
            _logger.LogError(
                "A stored principal names {Iterations} derivation iterations, which is not a cost; it "
                + "is refused rather than verified with a weaker one.",
                record.Material.Iterations);

            return null;
        }

        var derived = MintedApiKey.DeriveDigest(presentedKey, record.Material.Salt, record.Material.Iterations);

        return MintedApiKey.DigestMatches(record.Material.Digest, derived)
            ? HostPrincipal.FromStore(record.Principal)
            : null;
    }

    /// <summary>
    /// Resolves against the configured principals, for names the store has not claimed.
    /// </summary>
    /// <remarks>
    /// Keys are compared by SHA-256 digest using a fixed-time comparison, and the loop runs to
    /// completion even after a match. Without both, the time taken to reject a key would say
    /// something about how much of it was correct and which configured entry it was closest to. The
    /// digest here is a fast one on purpose: a configured key is a deployment secret an operator
    /// supplies, not something this host minted, and it is not stored by us at all.
    /// </remarks>
    private HostPrincipal? ResolveFromConfiguration(string presentedKey)
    {
        var presentedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey));
        HostPrincipalOptions? match = null;
        var index = -1;

        for (var i = 0; i < _options.Principals.Count; i++)
        {
            var candidate = _options.Principals[i];

            if (string.IsNullOrEmpty(candidate.Key))
            {
                continue;
            }

            var candidateDigest = SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Key));

            // Deliberately not an early exit: the loop runs to completion either way.
            if (CryptographicOperations.FixedTimeEquals(presentedDigest, candidateDigest) && match is null)
            {
                match = candidate;
                index = i;
            }
        }

        if (match is null)
        {
            return null;
        }

        if (_store.ClaimedPrincipalIds().Contains(match.PrincipalId))
        {
            // Says which entry was inert and why. The alternative, refusing silently, leaves an
            // operator holding a key that configuration says is valid and the host will not accept.
            _logger.LogInformation(
                "The configuration entry for {PrincipalId} was ignored: the store holds a minted "
                + "principal of that name, and the store wins wholesale. {ConfiguredAt} no longer "
                + "authenticates anything; remove it or mint again under a different name.",
                match.PrincipalId,
                Configure(index));

            return null;
        }

        return HostPrincipal.FromConfiguration(match, index);
    }

    /// <summary>
    /// A digest of the presented key, used only to address the in-memory cache.
    /// </summary>
    /// <remarks>
    /// Never written anywhere, never logged. See <see cref="_cache"/> for why a cheap digest is
    /// acceptable here and only here.
    /// </remarks>
    private static string CacheKey(string presentedKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey)));

    private static string Configure(int index) => $"{HostAuthOptions.SectionName}:Principals:{index}";

    private sealed record CachedResolution(HostPrincipal Principal, DateTimeOffset ResolvedAt);
}
