namespace StyloMail.Host.Auth;

/// <summary>
/// Where a resolved principal's authority came from.
/// </summary>
/// <remarks>
/// Recorded on every resolution because the host has two sources of identity and the difference
/// between them is not cosmetic: one is revocable on the host, the other is a configuration entry
/// that only an operator can remove. A caller that could not tell them apart could not refuse to
/// edit the one it cannot edit.
/// </remarks>
public enum PrincipalSource
{
    /// <summary>A key minted on this host. The store holds a digest of it, never the value.</summary>
    Store,

    /// <summary>A principal configured in <c>StyloMail:Auth:Principals</c>.</summary>
    Environment,
}

/// <summary>
/// A principal that has resolved from a presented key: who the caller is and what they may do.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only shape an authentication channel is given.</b> Two channels building their own
/// view of a principal from two different sources is how a host ends up with an SMTP path that
/// honours a privilege the HTTP path does not, so both read this and nothing else.
/// </para>
/// <para>
/// <b>The fields are the ones that resolved, from one source.</b> A principal known to both the
/// store and the configuration resolves to exactly one of them, whole: privileges and keys are never
/// unioned across the two, because a union is how an environment entry silently re-widens a
/// privilege an operator deliberately narrowed when they minted the replacement.
/// </para>
/// </remarks>
public sealed record HostPrincipal
{
    public required string PrincipalId { get; init; }

    public required string TenantId { get; init; }

    /// <summary>What this principal may do, already parsed. Never a set of names a caller re-parses.</summary>
    public required HostPrivilege Privileges { get; init; }

    /// <summary>
    /// Sender identities this principal may use in SMTP <c>MAIL FROM</c>, empty by default.
    /// </summary>
    /// <remarks>
    /// Empty authorises nothing beyond the null sender, for the reason
    /// <see cref="HostPrincipalOptions.ApprovedSenderIdentities"/> gives: "no restriction configured"
    /// and "may send as anyone" must not be the same value.
    /// </remarks>
    public required IReadOnlyList<string> ApprovedSenderIdentities { get; init; }

    public required PrincipalSource Source { get; init; }

    /// <summary>
    /// Whether this principal's authority can be changed from the host's own surface.
    /// </summary>
    /// <remarks>
    /// Environment principals are frozen read-only: they are not editable and not revocable from the
    /// CLI or the console, because this host does not own the configuration that describes them. The
    /// refusal names that configuration rather than being silent, which is the difference between an
    /// answer and a no-op.
    /// </remarks>
    public bool IsReadOnly => Source == PrincipalSource.Environment;

    /// <summary>The configuration entry a principal resolved from, named so a refusal can point at it.</summary>
    public required string? ConfiguredAt { get; init; }

    public static HostPrincipal FromConfiguration(HostPrincipalOptions options, int index) => new()
    {
        PrincipalId = options.PrincipalId,
        TenantId = options.TenantId,
        Privileges = options.ResolvePrivileges(),
        ApprovedSenderIdentities = [.. options.ApprovedSenderIdentities],
        Source = PrincipalSource.Environment,
        ConfiguredAt = $"StyloMail:Auth:Principals:{index}",
    };

    public static HostPrincipal FromStore(MintedPrincipal principal) => new()
    {
        PrincipalId = principal.PrincipalId,
        TenantId = principal.TenantId,
        Privileges = HostPrivileges.Parse(principal.Privileges),
        ApprovedSenderIdentities = [.. principal.ApprovedSenderIdentities],
        Source = PrincipalSource.Store,
        ConfiguredAt = null,
    };
}

/// <summary>What became of one principal in one of the two sources.</summary>
public enum PrincipalStatus
{
    /// <summary>A minted key that authenticates.</summary>
    Active,

    /// <summary>A minted key that has been revoked. Its principal id stays claimed by the store.</summary>
    Revoked,

    /// <summary>An environment principal, frozen read-only: this host does not own its configuration.</summary>
    ReadOnly,

    /// <summary>
    /// An environment principal the store has claimed, so it no longer authenticates at all.
    /// </summary>
    /// <remarks>
    /// The visible half of the wholesale precedence rule. An operator who mints a key for a name that
    /// is also configured has to be able to see that the configuration entry went inert, or the
    /// first sign of it is a credential that stops working with nothing on the host saying why.
    /// </remarks>
    ShadowedByStore,
}

/// <summary>
/// One principal as one of the two sources holds it.
/// </summary>
/// <remarks>
/// <b>One row per (source, principal), not one per principal.</b> The two layers are listed as they
/// are rather than reconciled into a single effective view, because the fact worth seeing is exactly
/// the one a reconciliation would hide: that a name is present in both, and that only one of them
/// resolves.
/// </remarks>
public sealed record PrincipalInventoryEntry
{
    public required string PrincipalId { get; init; }

    public required string TenantId { get; init; }

    /// <summary>Privilege names as the source holds them, whether or not they still apply.</summary>
    public required IReadOnlyList<string> Privileges { get; init; }

    public required PrincipalSource Source { get; init; }

    public required PrincipalStatus Status { get; init; }

    /// <summary>The configuration entry that owns this principal, for a source the host cannot edit.</summary>
    public string? ConfiguredAt { get; init; }
}

/// <summary>
/// Turns the stored and configured privilege names into flags.
/// </summary>
/// <remarks>
/// One parser, used by both sources, so a name that works in configuration works in the store and
/// vice versa. Two parsers would let the same string mean different things depending on which side of
/// the precedence rule it arrived from, which is exactly the sort of divergence the wholesale rule
/// exists to prevent.
/// </remarks>
public static class HostPrivileges
{
    /// <summary>The privilege names this host recognises, in a stable order.</summary>
    public static IReadOnlyList<string> Names { get; } =
        [.. Enum.GetValues<HostPrivilege>()
            .Where(value => value != HostPrivilege.None)
            .Select(value => value.ToString())];

    public static HostPrivilege Parse(IEnumerable<string> names)
    {
        var privileges = HostPrivilege.None;

        foreach (var name in names)
        {
            if (Enum.TryParse<HostPrivilege>(name, ignoreCase: true, out var parsed))
            {
                privileges |= parsed;
            }
        }

        return privileges;
    }

}
