namespace StyloMail.AccessProxy.Credentials;

/// <summary>Which protocol the backend session is speaking. Selects the wire framing, never the secret.</summary>
public enum BackendProtocol
{
    Imap = 0,
    Pop3 = 1,
    Smtp = 2,
}

/// <summary>
/// A stored backend credential, in the shape the store keeps it.
/// </summary>
/// <remarks>
/// <b>The discriminator is the whole point of this type.</b> Spec §9.5 requires that the OAuth
/// migration be an implementation swap and not a redesign, and that the credential store "carry a
/// discriminator rather than assuming a password shape".
///
/// <para>
/// So there is deliberately <em>no</em> <c>Password</c> property here, and no
/// <c>RefreshToken</c> property either. There is a <see cref="Discriminator"/> naming what kind of
/// credential this is, and <see cref="ProtectedSecret"/> holding opaque ciphertext whose shape only
/// the matching <see cref="IBackendCredentialProvider"/> understands. A new credential kind adds a
/// discriminator value and a provider; it does not add a column, and it does not touch a caller.
/// </para>
///
/// <para>
/// Had this type been modelled with a password field — the obvious first move, since app passwords
/// are the transitional path we are shipping first — then adding OAuth would have meant either a
/// nullable refresh-token field sitting next to a nullable password field (two shapes, one of them
/// always empty, and every consumer left to guess which), or a schema migration that invalidates
/// every stored credential. Both are the rewrite §9.5 exists to prevent.
/// </para>
///
/// <para>
/// <b>The secret is stored encrypted, never in plaintext.</b> <see cref="ProtectedSecret"/> is
/// ciphertext produced by <see cref="ISecretProtector"/>, bound to this record's tenant, account and
/// discriminator as additional authenticated data. That binding is why tampering with the
/// discriminator is a decryption failure rather than a silent misread of an OAuth token as an app
/// password.
/// </para>
/// </remarks>
public sealed record BackendCredentialRecord
{
    /// <summary>StyloMail's account id this credential belongs to. Tenant-scoped and never reused.</summary>
    public required string AccountId { get; init; }

    /// <summary>Owning tenant. Part of the encryption binding.</summary>
    public required string TenantId { get; init; }

    /// <summary>The provider this credential authenticates against, e.g. <c>gmail</c>.</summary>
    public required string ProviderId { get; init; }

    /// <summary>
    /// What kind of credential this is. Selects the <see cref="IBackendCredentialProvider"/>.
    /// </summary>
    /// <remarks>
    /// An opaque, provider-owned string rather than an enum. An enum would mean adding a credential
    /// kind requires editing this assembly and every consumer that switches on it — the coupling
    /// the seam exists to remove. A string keeps the decision with whoever ships the provider, and
    /// an unrecognised value fails closed at selection time rather than defaulting to a guess.
    /// </remarks>
    public required string Discriminator { get; init; }

    /// <summary>
    /// The encrypted credential. Opaque above the seam — nothing outside a
    /// <see cref="IBackendCredentialProvider"/> may interpret these bytes.
    /// </summary>
    public required byte[] ProtectedSecret { get; init; }

    /// <summary>
    /// Which key encrypted <see cref="ProtectedSecret"/>. Recorded per credential so a key rotation
    /// can re-wrap in place and so a credential encrypted under a retired key is identifiable
    /// rather than merely undecryptable.
    /// </summary>
    public required string ProtectionKeyId { get; init; }

    /// <summary>
    /// The backend account name this credential authenticates as, e.g. the Gmail address.
    /// </summary>
    /// <remarks>
    /// Not a secret — it is the user's own address, and the provider needs it in the SASL exchange.
    /// Kept out of <see cref="ProtectedSecret"/> so the store stays queryable by account without
    /// decrypting anything.
    /// </remarks>
    public required string BackendUsername { get; init; }

    /// <summary>Scopes the credential was granted, when the credential kind has them. Null for app passwords.</summary>
    public string? GrantedScopes { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// True when the operator has revoked this credential on the StyloMail side. A revoked
    /// credential must fail closed — never be used, and never retried against the provider.
    /// </summary>
    public bool Revoked { get; init; }

    /// <summary>
    /// Redacts the ciphertext. A record's generated <c>ToString</c> prints every property, which is
    /// exactly how a credential store ends up in a log by accident — here it would print key
    /// material-adjacent ciphertext. Overriding is cheap; discovering the leak is not.
    /// </summary>
    public override string ToString() =>
        $"{nameof(BackendCredentialRecord)} {{ AccountId = {AccountId}, TenantId = {TenantId}, " +
        $"ProviderId = {ProviderId}, Discriminator = {Discriminator}, ProtectionKeyId = {ProtectionKeyId}, " +
        $"BackendUsername = {BackendUsername}, Revoked = {Revoked}, ProtectedSecret = [redacted] }}";
}
