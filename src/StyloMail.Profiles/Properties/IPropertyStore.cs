namespace StyloMail.Profiles.Properties;

/// <summary>What happened to a property row.</summary>
/// <remarks>
/// Every action here changes what the system will do next, so every action is recorded. There is no
/// "read" member because reading is not a change to the binding: the one read-side write is an
/// expiry, and that is recorded as <see cref="Expire"/>.
/// </remarks>
public enum PropertyAuditAction
{
    Set = 0,
    Confirm = 1,
    Clear = 2,
    Expire = 3,
}

/// <summary>One immutable property history entry.</summary>
/// <remarks>
/// <b>Old and new are both carried, and both are optional.</b> A set from nothing has no old value;
/// a clear has no new one. Carrying both lets an operator reconstruct the sequence without joining
/// rows that no longer exist, which is the point of an audit rather than a log.
/// </remarks>
public sealed record PropertyAuditEvent
{
    public required string EventId { get; init; }

    public required PropertyScope Scope { get; init; }

    public required string Key { get; init; }

    public required PropertyAuditAction Action { get; init; }

    /// <summary>Who did it. For an expiry this is the host acting on the recorded instant.</summary>
    public required string Actor { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }

    public string? OldValue { get; init; }

    public string? NewValue { get; init; }

    public PropertySource? OldSource { get; init; }

    public PropertySource? NewSource { get; init; }

    public string? OldConfirmedBy { get; init; }

    public string? NewConfirmedBy { get; init; }

    public string? Reason { get; init; }
}

/// <summary>The result of a confirm or a clear at one scope.</summary>
/// <remarks>
/// <see cref="Applied"/> is false when the addressed scope held no live row. That is not an outage
/// and not a silent success: a confirm with nothing to confirm must tell the operator, because the
/// alternative is a UI that reports a human event that never happened.
/// </remarks>
public sealed record PropertyMutationResult
{
    public required bool Applied { get; init; }

    /// <summary>The row at the addressed scope afterwards, or null when none remains.</summary>
    public PropertyRecord? Stored { get; init; }

    /// <summary>The effective property at the addressed scope afterwards.</summary>
    public required EffectiveProperty Effective { get; init; }
}

/// <summary>
/// The property read/write port.
/// </summary>
/// <remarks>
/// <para>
/// <b>Narrow on purpose.</b> Policy needs to read effective values; the console needs to read them
/// with their provenance and to write them with an audit trail. Both are here, and nothing else is:
/// there is no raw content, no message body and no cross-tenant handle in this contract.
/// </para>
/// <para>
/// <b>Tenant is a required argument on every call and is never optional.</b> A store that defaulted
/// it, or took it from ambient state, is one call site away from a cross-tenant read.
/// </para>
/// </remarks>
public interface IPropertyStore
{
    /// <summary>
    /// Resolves the effective value of each requested key over <paramref name="chain"/>.
    /// </summary>
    /// <remarks>
    /// Every key is present in the answer, including one with no value at any scope. Expired rows
    /// are excluded and materialise an <see cref="PropertyAuditAction.Expire"/> event, so the
    /// fallback happens once and is recorded rather than happening invisibly on every read.
    /// </remarks>
    Task<IReadOnlyList<EffectiveProperty>> GetEffectiveAsync(
        string tenantId,
        PropertyScopeChain chain,
        IReadOnlyList<string> keys,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>The live row at exactly one scope, or null.</summary>
    Task<PropertyRecord?> GetStoredAsync(
        string tenantId,
        PropertyScope scope,
        string key,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Stores a checked write and records it.</summary>
    Task<PropertyRecord> SetAsync(
        string tenantId,
        PropertyWrite write,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Records a human confirmation of the row at exactly this scope.</summary>
    Task<PropertyMutationResult> ConfirmAsync(
        string tenantId,
        PropertyScope scope,
        string key,
        string actor,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Removes the row at exactly this scope, recording what it was.</summary>
    Task<PropertyMutationResult> ClearAsync(
        string tenantId,
        PropertyScope scope,
        string key,
        string actor,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>History for one scope and key, newest first.</summary>
    Task<IReadOnlyList<PropertyAuditEvent>> GetHistoryAsync(
        string tenantId,
        PropertyScope scope,
        string key,
        int limit,
        CancellationToken cancellationToken);
}
