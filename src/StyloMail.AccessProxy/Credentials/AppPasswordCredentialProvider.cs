using System.Security.Cryptography;
using System.Text;

namespace StyloMail.AccessProxy.Credentials;

/// <summary>
/// The transitional app-password credential kind (spec §9.5, operator decision 2026-09-22).
/// </summary>
/// <remarks>
/// Google removed username/password authentication for IMAP, POP3 and SMTP on 1 May 2025, and an
/// app password is the one surviving exception, a long random string the user generates in their
/// Google account, which requires 2-Step Verification to exist at all. The operator chose it as a
/// documented transitional path to ship sooner while OAuth verification runs in parallel.
///
/// <para>
/// <b>It is built to the seam, not to the mechanism.</b> This class is knowingly temporary: Google
/// has announced app passwords are being wound down, and spec §9.5 risk 5 says Google may disable
/// the path with little notice. That is tolerable only because replacing this file with
/// <see cref="OAuthRefreshTokenCredentialProvider"/> changes nothing outside it, the discriminator
/// on stored records changes and this class stops being selected. No session driver, no proxy and
/// no caller is aware of which one is in play, and the test suite asserts that directly.
/// </para>
///
/// <para>
/// <b>Onboarding must surface 2-Step Verification.</b> A user without 2SV cannot generate an app
/// password, and the failure presents as an authentication bug rather than a missing prerequisite
/// (spec §9.5 risk 2). That is a host/onboarding concern rather than this project's, but the failure
/// is named precisely here, <see cref="CredentialUnavailableException"/> rather than a rejection, /// so a caller can tell "this account was never enrolled" from "Google refused the credential".
/// </para>
/// </remarks>
public sealed class AppPasswordCredentialProvider : IBackendCredentialProvider
{
    /// <summary>
    /// The discriminator value stored on records using this provider.
    /// </summary>
    /// <remarks>
    /// A stable, documented string rather than an enum member: it is written into the credential
    /// store, so it has to outlive any refactor of this assembly.
    /// </remarks>
    public const string DiscriminatorValue = "app-password";

    public string Discriminator => DiscriminatorValue;

    public ValueTask<IBackendAuthenticator> CreateAuthenticatorAsync(
        BackendCredentialContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var username = context.Record.BackendUsername;
        if (string.IsNullOrEmpty(username))
        {
            throw new CredentialUnavailableException(
                $"The app-password credential for account '{context.Record.AccountId}' has no backend username.");
        }

        // SASL PLAIN (RFC 4616): authzid \0 authcid \0 passwd, with an empty authzid because the
        // user authenticates as themselves. The password is never materialised as a string, it
        // goes from the protected bytes into this buffer and no further.
        //
        // POP3 is the exception. Its SASL support is uneven across servers, and USER/PASS is the
        // interoperable path for an app password there, so the framing differs by protocol. That
        // choice is made here, below the seam, precisely so the POP3 driver does not have to know
        // which credential kind it is carrying.
        var plain = context.Protocol == BackendProtocol.Pop3
            ? BuildPlainPayload(username, context.Secret)
            : null;

        if (plain is not null)
        {
            var (user, password) = SplitPlainPayload(plain);
            CryptographicOperations.ZeroMemory(plain);
            return ValueTask.FromResult<IBackendAuthenticator>(
                new LegacyCredentialPairAuthenticator(user, password));
        }

        return ValueTask.FromResult<IBackendAuthenticator>(
            new SaslInitialResponseAuthenticator(
                "PLAIN",
                BuildPlainPayload(username, context.Secret)));
    }

    private static byte[] BuildPlainPayload(string username, SecretValue secret)
    {
        var userBytes = Encoding.UTF8.GetBytes(username);
        var secretBytes = secret.Utf8;

        var payload = new byte[1 + userBytes.Length + 1 + secretBytes.Length];
        var offset = 0;
        payload[offset++] = 0;                       // empty authzid
        userBytes.CopyTo(payload.AsSpan(offset));
        offset += userBytes.Length;
        payload[offset++] = 0;
        secretBytes.CopyTo(payload.AsSpan(offset));

        return payload;
    }

    /// <summary>
    /// Splits a PLAIN payload into its username and password halves.
    /// </summary>
    /// <remarks>
    /// Reusing the PLAIN layout rather than plumbing a second copy of the secret through a second
    /// code path: one construction, one place the plaintext exists.
    /// </remarks>
    private static (byte[] Username, byte[] Password) SplitPlainPayload(byte[] plainPayload)
    {
        var firstNul = Array.IndexOf(plainPayload, (byte)0);
        var secondNul = firstNul < 0 ? -1 : Array.IndexOf(plainPayload, (byte)0, firstNul + 1);
        if (secondNul < 0)
        {
            throw new CredentialUnavailableException("Malformed SASL PLAIN payload.");
        }

        return (
            plainPayload.AsSpan(firstNul + 1, secondNul - firstNul - 1).ToArray(),
            plainPayload.AsSpan(secondNul + 1).ToArray());
    }
}

/// <summary>
/// A SASL exchange that carries its whole response in the opening command.
/// </summary>
/// <remarks>
/// Used by PLAIN and XOAUTH2, the two mechanisms where the client speaks first. Any server
/// continuation after the initial response is an error signal in both, so the exchange answers with
/// an empty line, which both protocols require the client to send, and lets the server's rejection
/// come back as an ordinary negative reply for the driver to translate into a fail-closed refusal.
/// </remarks>
internal sealed class SaslInitialResponseAuthenticator : IBackendAuthenticator
{
    private byte[]? _initialResponse;
    private bool _started;

    internal SaslInitialResponseAuthenticator(string mechanism, byte[] initialResponse)
    {
        SaslMechanism = mechanism;
        _initialResponse = initialResponse;
    }

    public BackendAuthStyle Style => BackendAuthStyle.Sasl;

    public string SaslMechanism { get; }

    public ValueTask<SecretValue?> NextAsync(
        ReadOnlyMemory<byte>? serverChallenge,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_initialResponse is null, this);

        if (!_started)
        {
            _started = true;

            // A copy per call, so the value the driver sends and disposes is not the buffer this
            // authenticator still holds, otherwise disposing the token would blank the exchange
            // and a later call would silently send zeros.
            return ValueTask.FromResult<SecretValue?>(
                SecretValue.FromBytes((byte[])_initialResponse.Clone()));
        }

        // A continuation we did not expect. We do not guess at a second credential and we do not
        // retry: we send the empty line the protocol expects, the server rejects, the session ends.
        return ValueTask.FromResult<SecretValue?>(SecretValue.FromBytes([]));
    }

    public void Dispose()
    {
        if (_initialResponse is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_initialResponse);
        _initialResponse = null;
    }
}

/// <summary>
/// A two-token username/password exchange, sent as separate protocol verbs.
/// </summary>
/// <remarks>
/// POP3's classical authentication: <c>USER</c> then <c>PASS</c>, each with its own reply. Modelled
/// as an ordinary token sequence rather than as a special case so the POP3 driver needs no knowledge
/// of credential kinds, it asks for the next token twice and frames each as the verb its protocol
/// defines.
/// </remarks>
internal sealed class LegacyCredentialPairAuthenticator : IBackendAuthenticator
{
    private byte[]? _username;
    private byte[]? _password;
    private int _index;

    internal LegacyCredentialPairAuthenticator(byte[] username, byte[] password)
    {
        _username = username;
        _password = password;
    }

    public BackendAuthStyle Style => BackendAuthStyle.LegacyUsernamePassword;

    public string? SaslMechanism => null;

    public ValueTask<SecretValue?> NextAsync(
        ReadOnlyMemory<byte>? serverChallenge,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var token = _index switch
        {
            0 => SecretValue.FromBytes((byte[])_username!.Clone()),
            1 => SecretValue.FromBytes((byte[])_password!.Clone()),
            _ => null,
        };

        _index++;
        return ValueTask.FromResult(token);
    }

    public void Dispose()
    {
        if (_username is not null)
        {
            CryptographicOperations.ZeroMemory(_username);
            _username = null;
        }

        if (_password is not null)
        {
            CryptographicOperations.ZeroMemory(_password);
            _password = null;
        }
    }
}
