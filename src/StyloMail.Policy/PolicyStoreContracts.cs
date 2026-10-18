namespace StyloMail.Policy;

/// <summary>Where a pack sits in its lifecycle. A pack is never edited after approval.</summary>
public enum PolicyPackState
{
    Draft = 0,
    Approved = 1,
    Effective = 2,
    Superseded = 3,
    Withdrawn = 4,
}

/// <summary>
/// One pack version as the list surface sees it: enough to choose a version, never enough to
/// decide with. The full payload is read by version when it is actually needed.
/// </summary>
public sealed record PolicyPackSummary
{
    public required string TenantId { get; init; }

    public required string PackId { get; init; }

    public required string Version { get; init; }

    public required PolicyPackState State { get; init; }

    public required string Jurisdiction { get; init; }

    /// <summary>Hash of the immutable payload bytes. An approval names bytes, not a label.</summary>
    public required string ContentSha256 { get; init; }

    public required string Author { get; init; }

    public string? Approver { get; init; }

    public string? SecondApprover { get; init; }

    public DateTimeOffset? EffectiveFrom { get; init; }

    public DateTimeOffset? ReviewDue { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? ApprovedAt { get; init; }

    public string? SupersededBy { get; init; }
}

/// <summary>
/// The decision path's pack port. Read-only on purpose: the evaluator and the assessment
/// composition cannot write a pack, a rule set or a binding.
/// </summary>
public interface IPolicyPackReader
{
    /// <summary>
    /// The pack in force for the tenant and jurisdiction at <paramref name="now"/>, or null when
    /// there is none. Null means "no pack", never "every category Off".
    /// </summary>
    Task<PolicyPack?> GetEffectiveAsync(
        string tenantId,
        string jurisdiction,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<PolicyPack?> GetVersionAsync(
        string tenantId,
        string packId,
        string version,
        CancellationToken cancellationToken = default);

    /// <summary>The lifecycle state of one version, without deserialising its payload.</summary>
    Task<PolicyPackSummary?> GetSummaryAsync(
        string tenantId,
        string packId,
        string version,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PolicyPackSummary>> ListAsync(
        string tenantId,
        CancellationToken cancellationToken = default);
}

/// <summary>The decision path's rule-set port. A pack may only reference a live version.</summary>
public interface IPolicyRuleSetReader
{
    Task<PolicyRuleSet?> GetAsync(
        string tenantId,
        string ruleSetId,
        string version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The live rule set for a version, whatever its id. A pack names the version; a tenant has one
    /// rule-set lineage. Null when the version is not live.
    /// </summary>
    Task<PolicyRuleSet?> FindLiveByVersionAsync(
        string tenantId,
        string version,
        CancellationToken cancellationToken = default);
}
