using StyloMail.Core;

namespace StyloMail.Adaptive.Profiles;

/// <summary>
/// The kinds of bounded profile the adaptive engine maintains.
/// </summary>
/// <remarks>
/// Scopes differ in how much authority they carry, not merely in what they key on.
/// An established relationship is evidence about a pair of correspondents; a domain is
/// context about a naming space. Treating the second as though it were the first is how
/// an attacker who registers one address at a reputable domain inherits that domain's
/// reputation: see <see cref="ProfileScopeTrust"/>.
/// </remarks>
public enum ProfileScopeKind
{
    /// <summary>Tenant-wide behaviour within one traffic class. Never about a principal.</summary>
    TenantTrafficClass = 0,

    /// <summary>An authenticated outbound sending principal.</summary>
    OutboundSender = 1,

    /// <summary>A claimed inbound sender identity, qualified by how it authenticated.</summary>
    InboundSenderIdentity = 2,

    /// <summary>A recipient, per direction.</summary>
    Recipient = 3,

    /// <summary>A sender-recipient pair, per direction.</summary>
    Relationship = 4,

    /// <summary>Domain-level context. A fallback only.</summary>
    DomainContext = 5,

    /// <summary>
    /// An author on a chat platform, from outside the workspace this deployment watches.
    /// </summary>
    /// <remarks>
    /// <b>Distinct from <see cref="InboundSenderIdentity"/> rather than a reuse of it, and the
    /// difference is what the identity rests on.</b> An email sender's identity is a <em>claim</em>:
    /// anyone can write anything in <c>From</c>, and the qualification is what separates a claim a
    /// DKIM signature backed from one nothing did. Same address, different trust, so that key has to
    /// say which.
    ///
    /// <para>
    /// A chat author is not a claim in that sense. The platform asserts who posted and the connector
    /// verified the request before an assessment existed, so the identity reaching this path was
    /// asserted and verified rather than merely written down. Putting that into
    /// <see cref="InboundSenderIdentity"/> would place it in a pool whose meaning is "claimed, and
    /// here is how the message proved it", carrying an <c>auth=</c> component holding a value that
    /// means something else.
    /// </para>
    ///
    /// <para>
    /// <b>There is no provenance component, and that is a decision with a consequence.</b> On this
    /// path the qualification is constant, and a key component that is always the same value is
    /// decoration rather than a qualification. If a chat surface ever arrives where the platform
    /// does <em>not</em> assert the author, or where two different assertions are possible, then a
    /// provenance component earns its place and <b>adding one is a migration</b>: every existing key
    /// changes shape and the history behind them does not follow. That is the cost, and it is worth
    /// knowing before the first such platform rather than after.
    /// </para>
    /// </remarks>
    ChatAuthor = 6,
}

/// <summary>
/// An addressable profile. Everything is tenant-scoped, and sender/recipient/relationship
/// profiles additionally carry direction so inbound and outbound statistics never merge.
/// </summary>
/// <remarks>
/// <see cref="Key"/> is a tenant-scoped keyed hash produced by
/// <see cref="ProfileKeyHasher"/>, never a raw address. A store keyed on raw addresses
/// cannot honour a deletion request without knowing every derived copy.
/// </remarks>
public sealed record ProfileKey
{
    public required string TenantId { get; init; }

    public required ProfileScopeKind Scope { get; init; }

    public required string Key { get; init; }

    /// <summary><see langword="null"/> only for scopes that are genuinely direction-agnostic.</summary>
    public MailDirection? Direction { get; init; }
}

/// <summary>
/// What a scope's evidence is allowed to mean.
/// </summary>
public static class ProfileScopeTrust
{
    /// <summary>
    /// True when the scope describes an identified principal or an established pair, and may
    /// therefore contribute to account-trust decisions.
    /// </summary>
    /// <remarks>
    /// <see cref="ProfileScopeKind.DomainContext"/> is deliberately <see langword="false"/>:
    /// domain-level context is a fallback that can widen coverage, and is never equivalent to
    /// account trust.
    /// </remarks>
    public static bool IsAccountTrust(ProfileScopeKind scope) => scope switch
    {
        ProfileScopeKind.OutboundSender => true,
        ProfileScopeKind.InboundSenderIdentity => true,
        ProfileScopeKind.Recipient => true,
        ProfileScopeKind.Relationship => true,
        ProfileScopeKind.TenantTrafficClass => false,
        ProfileScopeKind.DomainContext => false,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown profile scope kind."),
    };

    /// <summary>
    /// True when the scope may only be consulted after the account-trust scopes have been
    /// tried and found wanting.
    /// </summary>
    public static bool IsFallback(ProfileScopeKind scope) => scope switch
    {
        ProfileScopeKind.DomainContext => true,
        ProfileScopeKind.OutboundSender => false,
        ProfileScopeKind.InboundSenderIdentity => false,
        ProfileScopeKind.Recipient => false,
        ProfileScopeKind.Relationship => false,
        ProfileScopeKind.TenantTrafficClass => false,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown profile scope kind."),
    };
}

/// <summary>Builds the profile keys the pipeline reads and writes.</summary>
/// <remarks>
/// Identity inputs are already-pseudonymized keys, not raw addresses: hashing belongs at
/// ingress, where the address is unavoidably present, and not in the adaptive engine, where
/// it would spread raw personal data across every call site.
/// </remarks>
public static class ProfileScopes
{
    /// <summary>Provenance marker for an identity that presented no trusted authentication at all.</summary>
    public const string NoAuthenticationProvenance = "none";

    public static ProfileKey TenantTrafficClass(string tenantId, string trafficClass)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(trafficClass);

        return new ProfileKey
        {
            TenantId = tenantId,
            Scope = ProfileScopeKind.TenantTrafficClass,
            Key = trafficClass,
            Direction = null,
        };
    }

    /// <summary>
    /// An author on a chat platform who is outside the workspace this deployment watches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed by platform, workspace and author, and carrying no provenance component. See
    /// <see cref="ProfileScopeKind.ChatAuthor"/> for why the provenance an email sender's key
    /// carries would mean something else here, and for what adding one later would cost.
    /// </para>
    /// <para>
    /// <b><paramref name="authorKey"/> is a pseudonym, not the platform's identifier.</b> The caller
    /// hashes it through <see cref="ProfileKeyHasher"/> for the same reason an email address is
    /// hashed: an author id identifies a person, and a store keyed on the raw value cannot later
    /// honour a deletion request without knowing every derived copy.
    /// </para>
    /// </remarks>
    public static ProfileKey ChatAuthor(
        string tenantId,
        string platform,
        string workspaceId,
        string authorKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorKey);

        return new ProfileKey
        {
            TenantId = tenantId,
            Scope = ProfileScopeKind.ChatAuthor,

            // The platform is part of the key because two platforms can name the same author
            // differently, and a workspace id alone is not unique across them.
            Key = $"{platform}|{workspaceId}|{authorKey}",
            Direction = MailDirection.Inbound,
        };
    }

    public static ProfileKey OutboundSender(string tenantId, string senderKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderKey);

        return new ProfileKey
        {
            TenantId = tenantId,
            Scope = ProfileScopeKind.OutboundSender,
            Key = senderKey,
            Direction = MailDirection.Outbound,
        };
    }

    /// <summary>
    /// An inbound sender identity, qualified by authentication provenance.
    /// </summary>
    /// <remarks>
    /// Only results from configured trusted boundary verifiers shape the qualification. A
    /// message cannot assert its own authentication, so an untrusted <c>dkim=pass</c> leaves
    /// the identity unauthenticated rather than conferring provenance it has not earned.
    /// </remarks>
    public static ProfileKey InboundSender(
        string tenantId,
        string senderKey,
        AuthenticationContext authentication)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderKey);
        ArgumentNullException.ThrowIfNull(authentication);

        return new ProfileKey
        {
            TenantId = tenantId,
            Scope = ProfileScopeKind.InboundSenderIdentity,
            Key = $"{senderKey}|auth={AuthenticationProvenance(authentication)}",
            Direction = MailDirection.Inbound,
        };
    }

    public static ProfileKey Recipient(string tenantId, MailDirection direction, string recipientKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientKey);

        return new ProfileKey
        {
            TenantId = tenantId,
            Scope = ProfileScopeKind.Recipient,
            Key = recipientKey,
            Direction = direction,
        };
    }

    /// <summary>
    /// A sender-recipient pair.
    /// </summary>
    /// <remarks>
    /// The key is deliberately direction-independent so inbound and outbound traffic for the
    /// same pair is explicitly linkable; the statistics stay distinct because direction is a
    /// separate component of profile identity. Linkage is offered, conflation is not.
    /// </remarks>
    public static ProfileKey Relationship(
        string tenantId,
        MailDirection direction,
        string senderKey,
        string recipientKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientKey);

        return new ProfileKey
        {
            TenantId = tenantId,
            Scope = ProfileScopeKind.Relationship,
            Key = $"{senderKey}>{recipientKey}",
            Direction = direction,
        };
    }

    public static ProfileKey Domain(string tenantId, string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        return new ProfileKey
        {
            TenantId = tenantId,
            Scope = ProfileScopeKind.DomainContext,
            Key = domain,
            Direction = null,
        };
    }

    /// <summary>
    /// A stable, comparable qualification of how an inbound identity authenticated.
    /// </summary>
    /// <remarks>
    /// Ordering is canonical so the same set of results always yields the same provenance:
    /// otherwise the same sender would key to several profiles depending on header order.
    /// </remarks>
    public static string AuthenticationProvenance(AuthenticationContext authentication)
    {
        ArgumentNullException.ThrowIfNull(authentication);

        var qualified = authentication.Results
            .Where(result => result.FromTrustedVerifier)
            .Select(result => $"{result.Mechanism.ToLowerInvariant()}={result.Result.ToLowerInvariant()}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToArray();

        return qualified.Length == 0 ? NoAuthenticationProvenance : string.Join(",", qualified);
    }
}
