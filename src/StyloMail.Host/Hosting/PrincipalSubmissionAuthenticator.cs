using StyloMail.Host.Auth;
using StyloMail.Transport.Ingress;

namespace StyloMail.Host.Hosting;

/// <summary>
/// Verifies SMTP submission credentials against the host's own principal directory.
/// </summary>
/// <remarks>
/// <para>
/// Thin by design, and thin for a reason worth stating. "Who is this and what may they do?" already
/// has one answer in this process, <see cref="PrincipalDirectory"/>, and a transport that kept its
/// own copy of that knowledge would be a second answer that could disagree. That is now sharper than
/// it was: the directory resolves from the host's minted-key store first and from its configuration
/// second, so a submission credential and an HTTP credential are the same credential resolved by the
/// same code, and a key revoked on the host stops working on both at once. None of the verification
/// is re-implemented here.
/// </para>
/// <para>
/// <b>The username must match the principal the key belongs to.</b> The key alone identifies the
/// principal, so a mismatched username is not an additional secret, but accepting it would let one
/// principal's key authenticate as another principal's name, and every audit record downstream would
/// then carry a name that never proved anything.
/// </para>
/// <para>
/// <b>An empty approved-sender list authorises nothing.</b> That is the transport's rule and it is
/// left exactly as it is: the null sender (bounces and DSNs) is always permitted, and everything
/// else must appear in <see cref="HostPrincipal.ApprovedSenderIdentities"/>. "No restriction
/// configured" and "may send as anyone" must not be the same value, or a missing grant becomes a
/// universal relay permission. A minted principal starts with an empty list for that reason, and
/// <c>key create --sender</c> is how an operator grants one.
/// </para>
/// </remarks>
public sealed class PrincipalSubmissionAuthenticator : ISubmissionAuthenticator
{
    private readonly PrincipalDirectory _directory;

    public PrincipalSubmissionAuthenticator(PrincipalDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = directory;
    }

    public ValueTask<AuthenticatedPrincipal?> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var principal = _directory.Resolve(password);

        if (principal is null
            || string.IsNullOrEmpty(username)
            || !string.Equals(principal.PrincipalId, username, StringComparison.Ordinal))
        {
            // One refusal for both cases, deliberately. Distinguishing "no such key" from "right key,
            // wrong username" would let a caller probe which of the two it got wrong, and the caller
            // is by definition unauthenticated at this point.
            return ValueTask.FromResult<AuthenticatedPrincipal?>(null);
        }

        return ValueTask.FromResult<AuthenticatedPrincipal?>(new AuthenticatedPrincipal
        {
            PrincipalId = principal.PrincipalId,
            TenantId = principal.TenantId,
            ApprovedSenderIdentities = [.. principal.ApprovedSenderIdentities],
        });
    }
}
