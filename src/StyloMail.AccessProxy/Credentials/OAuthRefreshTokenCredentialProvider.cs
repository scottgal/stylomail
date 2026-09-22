using System.Security.Cryptography;
using System.Text;

namespace StyloMail.AccessProxy.Credentials;

/// <summary>A refresh-token exchange request. The token is a secret; the subject is not.</summary>
public sealed record OAuthRefreshRequest
{
    /// <summary>The provider account being authenticated, e.g. the Gmail address. Not a secret.</summary>
    public required string Subject { get; init; }

    /// <summary>The refresh token. Redacted by <see cref="SecretValue.ToString"/> if logged by accident.</summary>
    public required SecretValue RefreshToken { get; init; }

    /// <summary>Scopes to request, when they must be narrowed below the grant. Null means "as granted".</summary>
    public string? Scopes { get; init; }
}

/// <summary>
/// Exchanges a refresh token for a short-lived access token.
/// </summary>
/// <remarks>
/// A seam of its own, and separately injectable, because it is the one place the proxy talks to a
/// provider's token service over the network. The credential seam itself must stay network-free so
/// its behaviour (which kind of credential, which mechanism, what fails closed) is testable without
/// a provider, this is where "no real network in tests" is honoured for the OAuth path.
/// </remarks>
public interface IOAuthTokenEndpoint
{
    /// <summary>Exchanges the refresh token for an access token.</summary>
    /// <exception cref="CredentialUnavailableException">The exchange failed and must not be retried here.</exception>
    ValueTask<SecretValue> ExchangeRefreshTokenAsync(
        OAuthRefreshRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// The OAuth 2.0 credential kind: a stored refresh token, replayed to the provider as <c>XOAUTH2</c>.
/// </summary>
/// <remarks>
/// This is the implementation spec §9.2 says the stored Gmail credential has to be, Google removed
/// username/password authentication for IMAP, SMTP and POP3, so a refresh token obtained once via a
/// consent flow is the only durable answer.
///
/// <para>
/// <b>It exists in this first slice precisely to prove the seam.</b> The app-password path is what
/// the operator ships today, but spec §9.5 requires the migration to be an implementation swap
/// rather than a redesign, and that claim is only worth anything if both implementations actually
/// exist and are actually exercised. So this provider is complete and registered, and the test
/// suite drives a whole session through it, asserting that the session driver behaves identically
/// for both kinds.
/// </para>
///
/// <para>
/// <b>Access tokens are not cached.</b> Refreshing per backend connection is one HTTPS call against
/// a rate limit that is generous, and it means a short-lived bearer token never sits in this
/// process's memory for longer than one exchange. Caching it would be an optimisation that creates
/// a second credential to protect and to expire correctly, in exchange for latency on a path that
/// is already network-bound. Spec §9.4 item 3 makes the blast radius of a leaked bearer credential
/// explicit, and the cheapest way to bound it is not to hold one.
/// </para>
/// </remarks>
public sealed class OAuthRefreshTokenCredentialProvider : IBackendCredentialProvider
{
    private readonly IOAuthTokenEndpoint _tokenEndpoint;

    public OAuthRefreshTokenCredentialProvider(IOAuthTokenEndpoint tokenEndpoint)
        => _tokenEndpoint = tokenEndpoint ?? throw new ArgumentNullException(nameof(tokenEndpoint));

    /// <summary>
    /// The discriminator value stored on records using this provider.
    /// </summary>
    /// <remarks>
    /// This string is the entire migration. Moving a tenant from app passwords to OAuth means
    /// writing this discriminator onto their record (after the consent flow has produced the
    /// refresh token) and nothing else, no caller changes, no driver changes, no session changes.
    /// </remarks>
    public const string DiscriminatorValue = "oauth-refresh-token";

    public string Discriminator => DiscriminatorValue;

    public async ValueTask<IBackendAuthenticator> CreateAuthenticatorAsync(
        BackendCredentialContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.Record.BackendUsername;
        if (string.IsNullOrEmpty(subject))
        {
            throw new CredentialUnavailableException(
                $"The OAuth credential for account '{context.Record.AccountId}' has no backend username.");
        }

        SecretValue accessToken;
        try
        {
            accessToken = await _tokenEndpoint.ExchangeRefreshTokenAsync(
                new OAuthRefreshRequest
                {
                    Subject = subject,
                    RefreshToken = context.Secret,
                    Scopes = context.Record.GrantedScopes,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Inclusive of HttpRequestException and any provider-specific failure. The point of the
            // catch is to guarantee that a failed exchange becomes a fail-closed credential failure,
            // not an unhandled exception that some caller might be tempted to retry.
            //
            // The inner exception is carried for diagnosis, "connection refused" versus "certificate
            // expired" is the difference between a network problem and a configuration problem, but
            // only when it is safe to carry. The token endpoint is a seam someone else implements,
            // and its exception messages are not ours to trust; a single interpolated token in one of
            // them would put a refresh token into an exception chain, and every logging framework
            // prints those in full. So the chain is checked against the exact secret before being
            // attached, and dropped rather than risked.
            var inner = context.Secret.AppearsIn(ex) ? null : ex;

            throw new CredentialUnavailableException(
                $"Could not obtain an access token for account '{context.Record.AccountId}'.", inner!);
        }

        using (accessToken)
        {
            return new SaslInitialResponseAuthenticator("XOAUTH2", BuildXOAuth2Payload(subject, accessToken));
        }
    }

    /// <summary>
    /// Builds the XOAUTH2 client response: <c>user=&lt;email&gt;\x01auth=Bearer &lt;token&gt;\x01\x01</c>.
    /// </summary>
    /// <remarks>
    /// The access token goes straight from the endpoint's buffer into this one and is never
    /// rendered as a string. A Gmail rejection of this payload arrives as a <c>+</c> continuation
    /// carrying a base64 JSON error, which the shared SASL exchange answers with an empty line,     /// per the protocol, so the server produces a proper negative reply and the session fails
    /// closed rather than retrying with a token Google has already refused.
    /// </remarks>
    private static byte[] BuildXOAuth2Payload(string subject, SecretValue accessToken)
    {
        var prefix = Encoding.UTF8.GetBytes("user=" + subject + "\u0001auth=Bearer ");
        var token = accessToken.Utf8;
        var payload = new byte[prefix.Length + token.Length + 2];
        prefix.CopyTo(payload.AsSpan());
        token.CopyTo(payload.AsSpan(prefix.Length));
        payload[^2] = 1;
        payload[^1] = 1;
        return payload;
    }
}
