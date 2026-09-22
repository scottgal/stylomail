using StyloMail.Queue;

namespace StyloMail.Host.Contracts;

/// <summary>
/// The answer to <c>GET /v1/senders</c>: the principals this tenant can send as, with their controls.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not a record that carries a credential.</b> A <c>HostPrincipalOptions</c> holds
/// the API key that authenticates the principal, and a response built from one by serialisation
/// would publish every credential on the host. This projection names each field it carries so that
/// adding one to the configuration cannot leak it by default, which is the failure mode that would
/// be hardest to notice and worst to have.
/// </para>
/// <para>
/// It is fed from <see cref="Auth.PrincipalInventoryEntry"/> rather than from the configuration,
/// because a sender may now be minted on the host rather than configured in it, and that type has
/// no key and no digest to leak in the first place.
/// </para>
/// </remarks>
public sealed record SenderListingResponse
{
    /// <summary>The tenant the listing was taken for, echoed so a caller can confirm the scope.</summary>
    public required string TenantId { get; init; }

    public required IReadOnlyList<SenderResponse> Senders { get; init; }

    public static SenderListingResponse From(
        string tenantId,
        IReadOnlyList<Auth.PrincipalInventoryEntry> principals,
        IReadOnlyList<Controls.SenderControlState> controls,
        IReadOnlyList<Controls.SenderProfile> profiles)
    {
        var byPrincipal = controls.ToDictionary(state => state.PrincipalId, StringComparer.Ordinal);
        var profilesByPrincipal = profiles.ToDictionary(profile => profile.PrincipalId, StringComparer.Ordinal);

        return new SenderListingResponse
        {
            TenantId = tenantId,
            Senders =
            [
                .. principals.Select(principal => new SenderResponse
                {
                    PrincipalId = principal.PrincipalId,

                    // Which of the host's two credential sources holds this sender. On the row
                    // rather than left for a second call, because a console showing a minted sender
                    // and a configured one identically would hide the fact that only one of them can
                    // be revoked from the host.
                    Source = principal.SourceName,

                    // A principal with no control record has never been paused, which is not the same
                    // as having no state: it is the ordinary state, and it is reported as such rather
                    // than omitted so a caller never has to distinguish "absent" from "not paused".
                    Control = byPrincipal.TryGetValue(principal.PrincipalId, out var state)
                        ? SenderControlResponse.From(state)
                        : SenderControlResponse.Unpaused,

                    // Carried on the listing rather than fetched per sender: the sidebar groups by
                    // company, and a settings call per row is a request storm on a tenant with a few
                    // hundred. Both come from the profile projection, so this is two fields and not
                    // two more queries.
                    Label = profilesByPrincipal.TryGetValue(principal.PrincipalId, out var profile)
                        ? profile.Label
                        : null,
                    CompanyId = profilesByPrincipal.TryGetValue(principal.PrincipalId, out var owner)
                        ? owner.CompanyId
                        : null,
                }),
            ],
        };
    }
}

/// <summary>One sending principal and its control state.</summary>
public sealed record SenderResponse
{
    public required string PrincipalId { get; init; }

    /// <summary>
    /// Which source resolved this sender: <c>store</c> for a key minted on this host, or
    /// <c>environment</c> for one configured in it.
    /// </summary>
    /// <remarks>
    /// A closed set, and the same two words <c>key list</c> reports. It matters to an operator for
    /// one reason: a configured principal is read-only and cannot be revoked from the host, so a
    /// console that did not carry this could not tell what its own controls would do.
    /// </remarks>
    public required string Source { get; init; }

    public required SenderControlResponse Control { get; init; }

    /// <summary>A human name for this sender, or null when nobody has set one.</summary>
    public string? Label { get; init; }

    /// <summary>The company this sender is filed under, or null for unfiled.</summary>
    public string? CompanyId { get; init; }
}

/// <summary>
/// Whether a principal's outbound delivery is paused, and the audit trail of who decided.
/// </summary>
/// <remarks>
/// The pause fields survive a resume on purpose. "Why was this account stopped for six hours?" is a
/// question an operator asks <em>after</em> it has been lifted, and erasing them on resume would make
/// the audit trail answer only the question nobody needs to ask.
/// </remarks>
public sealed record SenderControlResponse
{
    public required bool Paused { get; init; }

    public DateTimeOffset? PausedAt { get; init; }

    public string? Reason { get; init; }

    public DateTimeOffset? ResumedAt { get; init; }

    public string? ResumedBy { get; init; }

    public string? ResumeReason { get; init; }

    /// <summary>Whoever acted last, the pause or the resume.</summary>
    public string? UpdatedBy { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>The state of a principal no control action has ever touched.</summary>
    /// <remarks>Every audit field is null rather than defaulted: none of them happened.</remarks>
    public static SenderControlResponse Unpaused { get; } = new() { Paused = false };

    public static SenderControlResponse From(Controls.SenderControlState state) => new()
    {
        Paused = state.Paused,
        PausedAt = state.PausedAt,
        Reason = state.Reason,
        ResumedAt = state.ResumedAt,
        ResumedBy = state.ResumedBy,
        ResumeReason = state.ResumeReason,
        UpdatedBy = state.UpdatedBy,
        UpdatedAt = state.UpdatedAt,
    };
}

/// <summary>
/// The answer to <c>GET /v1/messages</c>: a page of messages and their per-recipient progress.
/// </summary>
/// <remarks>
/// <b>Every row is the same projection <c>GET /v1/submissions/{id}</c> serves</b>, so a console can
/// render a message list and a message detail from one shape rather than reconciling two. Paging is
/// the queue's own cursor: it is not a capability and carries no tenant, so it cannot be turned into
/// a way to read another tenant's mail.
/// </remarks>
public sealed record MessageListingResponse
{
    public required string TenantId { get; init; }

    /// <summary>Which disposition was asked for, echoed so a caller can tell pages apart.</summary>
    public required string State { get; init; }

    public required IReadOnlyList<SubmissionStatusResponse> Messages { get; init; }

    /// <summary>Echo back to fetch the next page; null when this is the last one.</summary>
    public string? NextCursor { get; init; }

    public required bool HasMore { get; init; }

    public static MessageListingResponse From(string tenantId, string state, QueueListingPage page) => new()
    {
        TenantId = tenantId,
        State = state,
        Messages = [.. page.Items.Select(SubmissionStatusResponse.From)],
        NextCursor = page.NextCursor,
        HasMore = page.HasMore,
    };
}
