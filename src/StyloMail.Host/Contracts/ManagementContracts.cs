using StyloMail.Host.Controls;

namespace StyloMail.Host.Contracts;

/// <summary>An operator's description of a sending principal.</summary>
/// <remarks>
/// <see cref="Posture"/> and <see cref="NotificationTarget"/> are documented here as well as labelled
/// in the console, and that duplication is deliberate: the console is not the only client this API
/// has, and a field that is stored but honoured by nothing has to say so where a non-console client
/// will read it. A gap documented in one screen is a gap; the same gap documented nowhere is a trap.
/// </remarks>
public sealed record SenderSettingsResponse
{
    public required string PrincipalId { get; init; }

    public string? Label { get; init; }

    public string? CompanyId { get; init; }

    public string? Notes { get; init; }

    public string? ExternalRef { get; init; }

    /// <summary>
    /// Where to tell someone. <b>Stored only: nothing delivers to it yet.</b>
    /// </summary>
    public string? NotificationTarget { get; init; }

    /// <summary>
    /// A visible stance: <c>trusted</c>, <c>normal</c> or <c>watch</c>.
    /// <b>Stored only: no pipeline reads it yet.</b>
    /// </summary>
    public string? Posture { get; init; }

    /// <summary>Who last wrote this, and when. Absent for a principal nobody has described.</summary>
    public string? UpdatedBy { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public static SenderSettingsResponse From(SenderProfile profile) => new()
    {
        PrincipalId = profile.PrincipalId,
        Label = profile.Label,
        CompanyId = profile.CompanyId,
        Notes = profile.Notes,
        ExternalRef = profile.ExternalRef,
        NotificationTarget = profile.NotificationTarget,
        Posture = profile.Posture,

        // The unset profile carries a sentinel instant rather than a real one, so it is reported as
        // absent instead of as 1970: which would read as "described a very long time ago".
        UpdatedBy = string.IsNullOrEmpty(profile.UpdatedBy) ? null : profile.UpdatedBy,
        UpdatedAt = profile.UpdatedAt == DateTimeOffset.UnixEpoch ? null : profile.UpdatedAt,
    };
}

/// <summary>The body of a settings write. A full replace.</summary>
/// <remarks>
/// Every field is optional and an absent one <em>clears</em> the stored value rather than preserving
/// it: the console sends the whole form, and a merge would make it impossible to remove a label.
/// </remarks>
public sealed record SenderSettingsRequest
{
    public string? Label { get; init; }

    public string? CompanyId { get; init; }

    public string? Notes { get; init; }

    public string? ExternalRef { get; init; }

    public string? NotificationTarget { get; init; }

    public string? Posture { get; init; }
}

/// <summary>The answer to <c>GET /v1/companies</c>.</summary>
public sealed record CompanyListingResponse
{
    public required string TenantId { get; init; }

    public required IReadOnlyList<CompanyResponse> Companies { get; init; }
}

/// <summary>One company.</summary>
public sealed record CompanyResponse
{
    public required string CompanyId { get; init; }

    public required string Name { get; init; }

    public string? Notes { get; init; }

    public required string UpdatedBy { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public static CompanyResponse From(Company company) => new()
    {
        CompanyId = company.CompanyId,
        Name = company.Name,
        Notes = company.Notes,
        UpdatedBy = company.UpdatedBy,
        UpdatedAt = company.UpdatedAt,
    };
}

/// <summary>The body of a company create or update.</summary>
public sealed record CompanyRequest
{
    public string? Name { get; init; }

    public string? Notes { get; init; }
}
