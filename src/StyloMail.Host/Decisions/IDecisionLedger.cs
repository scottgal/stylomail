using StyloMail.Core;

namespace StyloMail.Host.Decisions;

/// <summary>
/// The explainable decision ledger: what was decided, on what evidence, under which versions.
/// </summary>
/// <remarks>
/// Every read and write is scoped by an explicit tenant argument that comes from the authenticated
/// principal. There is deliberately no "find by id" overload without a tenant: an unscoped lookup
/// is the shape that turns a ledger into a cross-tenant oracle, and it is the shape most likely to
/// get called by accident from a new endpoint.
/// </remarks>
public interface IDecisionLedger
{
    Task RecordAsync(MailAssessment assessment, CancellationToken cancellationToken);

    /// <summary>Returns the decision, or null when it does not exist <em>for this tenant</em>.
    /// The two cases are indistinguishable to the caller by design.</summary>
    Task<MailAssessment?> FindAsync(string tenantId, string assessmentId, CancellationToken cancellationToken);

    /// <summary>
    /// One page of this tenant's ledger, newest first.
    /// </summary>
    /// <remarks>
    /// Keyset-paged rather than offset-paged. An offset shifts when a new decision is recorded while
    /// a caller is paging, so a reviewer would see the same row twice or miss one entirely — and
    /// missing one silently is the failure that matters, because a ledger whose rows can vanish
    /// between pages is not an audit trail.
    /// </remarks>
    Task<DecisionListingPage> ListAsync(
        DecisionListingQuery query,
        CancellationToken cancellationToken);
}
