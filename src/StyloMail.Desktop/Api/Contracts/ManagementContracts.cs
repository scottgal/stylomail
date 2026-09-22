namespace StyloMail.Desktop.Api.Contracts;

/// <summary>
/// An operator's description of a sending principal.
/// </summary>
/// <remarks>
/// <b>Two of these fields are stored and read by nothing yet</b>, and the type
/// says so because the Host's own contract says so. The console is not the only
/// client: a field that nothing honours has to declare that where any client
/// will read it, not only in one screen's label.
/// </remarks>
public sealed record SenderSettingsResponse
{
    public required string PrincipalId { get; init; }

    public string? Label { get; init; }

    /// <summary>Which company this sender belongs to, or null for "nobody has said".</summary>
    public string? CompanyId { get; init; }

    public string? Notes { get; init; }

    public string? ExternalRef { get; init; }

    /// <summary>Where to tell someone. <b>Stored only; nothing delivers to it yet.</b></summary>
    public string? NotificationTarget { get; init; }

    /// <summary>A visible stance. <b>Stored only; no pipeline reads it yet.</b></summary>
    public string? Posture { get; init; }

    /// <summary>Who last wrote this, and when. Null for a principal nobody has described.</summary>
    public string? UpdatedBy { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>
    /// Whether anyone has described this sender.
    /// </summary>
    /// <remarks>
    /// Derived rather than sent. A sender with no profile answers 200 with
    /// nulls, because the principal exists and is in the listing; a 404 would
    /// read as "no such sender" and send an operator looking for something
    /// that is right there. So the console has to tell "undescribed" from
    /// "described as blank", and this is how.
    /// </remarks>
    public bool IsDescribed => UpdatedAt is not null;
}

/// <summary>
/// The body of a settings write. <b>A full replace, not a merge.</b>
/// </summary>
/// <remarks>
/// Omitting a field clears it. That is the Host's decision and a good one: a
/// merge makes it impossible to remove a label. The consequence for this
/// client is that the form must send the whole profile, including fields it is
/// not showing, or editing a note would silently erase a company.
/// </remarks>
public sealed record SenderSettingsRequest
{
    public string? Label { get; init; }

    public string? CompanyId { get; init; }

    public string? Notes { get; init; }

    public string? ExternalRef { get; init; }

    public string? NotificationTarget { get; init; }

    /// <summary>One of <see cref="SenderPosture"/>'s names, or null to clear it.</summary>
    public string? Posture { get; init; }
}

/// <summary>
/// The stances an operator can record.
/// </summary>
/// <remarks>
/// <b>A closed set, and the Host refuses anything else by name.</b> A stored
/// stance nothing recognises is worse than no stance, because it looks like a
/// decision someone made. Modelled as constants rather than an enum so that an
/// unrecognised value coming back from a newer Host is reported rather than
/// silently defaulted.
/// </remarks>
public static class SenderPosture
{
    public const string Trusted = "trusted";
    public const string Normal = "normal";
    public const string Watch = "watch";

    public static IReadOnlyList<string> All { get; } = [Trusted, Normal, Watch];

    /// <summary>Whether a value is one this console can offer.</summary>
    public static bool IsKnown(string? posture) =>
        posture is not null && All.Contains(posture, StringComparer.Ordinal);
}

/// <summary>The answer to <c>GET /v1/companies</c>.</summary>
public sealed record CompanyListingResponse
{
    public required string TenantId { get; init; }

    public required IReadOnlyList<CompanyResponse> Companies { get; init; }
}

/// <summary>One company: an operator-side group of senders.</summary>
public sealed record CompanyResponse
{
    public required string CompanyId { get; init; }

    public required string Name { get; init; }

    public string? Notes { get; init; }

    public required string UpdatedBy { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>The body of a company create or update.</summary>
public sealed record CompanyRequest
{
    public string? Name { get; init; }

    public string? Notes { get; init; }
}
