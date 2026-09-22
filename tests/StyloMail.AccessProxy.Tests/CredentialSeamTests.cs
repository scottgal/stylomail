using System.Text;
using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Tests.Support;

namespace StyloMail.AccessProxy.Tests;

/// <summary>
/// The credential seam: the decision spec §9.5 says must be got right first.
/// </summary>
public sealed class CredentialSeamTests
{
    [Fact]
    public async Task StoredCredential_IsNotPlaintext()
    {
        // "Never store a credential in plaintext" is only meaningfully tested against the bytes that
        // actually landed in the store, so this asserts on the record rather than on the protector.
        var harness = new ProxyHarness();
        var record = await harness.AddBackendCredentialAsync();

        Assert.False(
            Contains(record.ProtectedSecret, Encoding.UTF8.GetBytes(ProxyHarness.AppPasswordSecret)),
            "The app password is present verbatim in the stored record.");
    }

    [Fact]
    public async Task StoredCredential_IsNotRecoverableFromTheRecordAlone()
    {
        // The store hands back ciphertext and a key id. Without the key ring, that is all a database
        // dump yields, which is the whole difference between encrypting at rest and not.
        var harness = new ProxyHarness();
        var record = await harness.AddBackendCredentialAsync();

        var wrongRing = new AesGcmSecretProtector(InMemorySecretKeyRing.CreateRandom("other-key"));
        var binding = new SecretBinding(record.TenantId, record.AccountId, record.Discriminator);

        Assert.Throws<CredentialUnavailableException>(
            () => wrongRing.Unprotect(record.ProtectedSecret, record.ProtectionKeyId, binding));
    }

    [Fact]
    public async Task Ciphertext_ReboundToADifferentDiscriminator_FailsClosed()
    {
        // The attack this closes is specific and nasty: an attacker with write access to the store
        // re-labels an OAuth refresh token as an app-password. Without the discriminator in the
        // authenticated data, the app-password provider would happily send a refresh token as a
        // password, a silent, wrong credential presented to the provider.
        var harness = new ProxyHarness();
        var record = await harness.AddBackendCredentialAsync();

        var relabelled = new SecretBinding(
            record.TenantId, record.AccountId, OAuthRefreshTokenCredentialProvider.DiscriminatorValue);

        Assert.Throws<CredentialUnavailableException>(
            () => harness.Protector.Unprotect(record.ProtectedSecret, record.ProtectionKeyId, relabelled));
    }

    [Fact]
    public async Task Ciphertext_ReplayedIntoAnotherAccount_FailsClosed()
    {
        var harness = new ProxyHarness();
        var record = await harness.AddBackendCredentialAsync();

        var otherAccount = new SecretBinding(record.TenantId, "acct-2", record.Discriminator);

        Assert.Throws<CredentialUnavailableException>(
            () => harness.Protector.Unprotect(record.ProtectedSecret, record.ProtectionKeyId, otherAccount));
    }

    [Fact]
    public async Task Ciphertext_ReplayedIntoAnotherTenant_FailsClosed()
    {
        var harness = new ProxyHarness();
        var record = await harness.AddBackendCredentialAsync();

        var otherTenant = new SecretBinding("tenant-beta", record.AccountId, record.Discriminator);

        Assert.Throws<CredentialUnavailableException>(
            () => harness.Protector.Unprotect(record.ProtectedSecret, record.ProtectionKeyId, otherTenant));
    }

    [Fact]
    public async Task Resolver_SelectsTheAppPasswordProviderFromTheDiscriminator()
    {
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        using var authenticator = await harness.Resolver.ResolveAsync(
            "acct-1", BackendProtocol.Imap, CancellationToken.None);

        Assert.Equal(BackendAuthStyle.Sasl, authenticator.Style);
        Assert.Equal("PLAIN", authenticator.SaslMechanism);
    }

    [Fact]
    public async Task Resolver_SelectsTheOAuthProviderFromTheDiscriminator()
    {
        // Same call, same arguments, different stored discriminator, and the only thing that
        // changes is what comes back. That is the swap spec §9.5 requires.
        var harness = new ProxyHarness();
        await harness.EnrolOAuthAccountAsync();

        using var authenticator = await harness.Resolver.ResolveAsync(
            "acct-1", BackendProtocol.Imap, CancellationToken.None);

        Assert.Equal(BackendAuthStyle.Sasl, authenticator.Style);
        Assert.Equal("XOAUTH2", authenticator.SaslMechanism);
        Assert.Equal(1, harness.OAuth.CallCount);
    }

    [Fact]
    public async Task Resolver_UnknownDiscriminator_FailsClosedRatherThanGuessing()
    {
        var harness = new ProxyHarness();
        harness.AddAccount();
        await harness.AddBackendCredentialAsync(discriminator: "passkey-of-the-future");

        var ex = await Assert.ThrowsAsync<CredentialUnavailableException>(
            async () => await harness.Resolver.ResolveAsync("acct-1", BackendProtocol.Imap, CancellationToken.None));

        Assert.Contains("passkey-of-the-future", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolver_MissingCredential_FailsClosed()
    {
        var harness = new ProxyHarness();
        harness.AddAccount();

        await Assert.ThrowsAsync<CredentialUnavailableException>(
            async () => await harness.Resolver.ResolveAsync("acct-1", BackendProtocol.Imap, CancellationToken.None));
    }

    [Fact]
    public async Task Resolver_RevokedCredential_FailsClosed()
    {
        // Spec §9.5 risk 4: the secondary revocation path. A user who revokes in Google must be able
        // to revoke here, and the result must be a refusal rather than an attempt.
        var harness = new ProxyHarness();
        harness.AddAccount();
        await harness.AddBackendCredentialAsync(revoked: true);

        var ex = await Assert.ThrowsAsync<CredentialUnavailableException>(
            async () => await harness.Resolver.ResolveAsync("acct-1", BackendProtocol.Imap, CancellationToken.None));

        Assert.Contains("revoked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AppPassword_ProducesSaslPlainCarryingTheUsernameAndTheSecret()
    {
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        using var authenticator = await harness.Resolver.ResolveAsync(
            "acct-1", BackendProtocol.Imap, CancellationToken.None);

        using var token = await authenticator.NextAsync(null, CancellationToken.None);
        Assert.NotNull(token);

        var payload = token!.Utf8.ToArray();

        // SASL PLAIN: authzid \0 authcid \0 passwd
        Assert.Equal(0, payload[0]);
        var text = Encoding.UTF8.GetString(payload);
        var parts = text.Split('\0');
        Assert.Equal(3, parts.Length);
        Assert.Equal(ProxyHarness.Login, parts[1]);
        Assert.Equal(ProxyHarness.AppPasswordSecret, parts[2]);
    }

    [Fact]
    public async Task OAuth_ProducesSaslXoauth2CarryingTheAccessToken()
    {
        var harness = new ProxyHarness();
        await harness.EnrolOAuthAccountAsync();

        using var authenticator = await harness.Resolver.ResolveAsync(
            "acct-1", BackendProtocol.Imap, CancellationToken.None);

        using var token = await authenticator.NextAsync(null, CancellationToken.None);
        Assert.NotNull(token);

        var text = Encoding.UTF8.GetString(token!.Utf8);

        Assert.StartsWith($"user={ProxyHarness.Login}\u0001auth=Bearer ", text, StringComparison.Ordinal);
        Assert.EndsWith("\u0001\u0001", text, StringComparison.Ordinal);
        Assert.Contains(ProxyHarness.AccessTokenSecret, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppPassword_OverPop3_UsesTheLegacyCredentialPair()
    {
        // POP3's SASL support is uneven, so the app-password provider picks USER/PASS there. That
        // choice is made below the seam; the POP3 driver only sees a style.
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        using var authenticator = await harness.Resolver.ResolveAsync(
            "acct-1", BackendProtocol.Pop3, CancellationToken.None);

        Assert.Equal(BackendAuthStyle.LegacyUsernamePassword, authenticator.Style);
        Assert.Null(authenticator.SaslMechanism);
    }

    [Fact]
    public async Task OAuth_TokenEndpointFailure_BecomesACredentialFailureNotAnUnhandledException()
    {
        // The endpoint throws something generic; what must come out is a credential failure, because
        // that is the only exception type the session treats as fail-closed.
        var harness = new ProxyHarness();
        await harness.EnrolOAuthAccountAsync();
        harness.OAuth.FailWith = new HttpRequestException("token endpoint unreachable");

        await Assert.ThrowsAsync<CredentialUnavailableException>(
            async () => await harness.Resolver.ResolveAsync("acct-1", BackendProtocol.Imap, CancellationToken.None));
    }

    [Fact]
    public void UnknownKeyId_FailsClosedRatherThanThrowingAnUnhandledError()
    {
        var ring = InMemorySecretKeyRing.CreateRandom("current");
        var protector = new AesGcmSecretProtector(ring);
        var binding = new SecretBinding("t", "a", "app-password");
        using var secret = SecretValue.FromUtf8(ProxyHarness.AppPasswordSecret);

        var stored = protector.Protect(secret, binding);

        // Key rotated away with credentials still encrypted under it.
        Assert.Throws<CredentialUnavailableException>(
            () => protector.Unprotect(stored.Ciphertext, "retired-key", binding));
    }

    [Fact]
    public void KeyRing_KeySurvivesRepeatedUse()
    {
        // Regression: the protector used to clear the key it was handed. The key ring owns key
        // material, so clearing it blanked the ring's only copy, the first credential enrolled
        // worked and every operation after it used zeros, which is a whole-store corruption that
        // presents as "the credential failed authentication".
        var ring = InMemorySecretKeyRing.CreateRandom("k1");
        var protector = new AesGcmSecretProtector(ring);
        var binding = new SecretBinding("t", "a", "app-password");

        for (var round = 0; round < 3; round++)
        {
            using var secret = SecretValue.FromUtf8(ProxyHarness.AppPasswordSecret);
            var stored = protector.Protect(secret, binding);
            using var recovered = protector.Unprotect(stored.Ciphertext, stored.KeyId, binding);

            Assert.Equal(
                ProxyHarness.AppPasswordSecret,
                Encoding.UTF8.GetString(recovered.Utf8));
        }
    }

    [Fact]
    public async Task TwoProvidersClaimingOneDiscriminator_IsRejectedAtWiringTime()
    {
        await Task.CompletedTask;

        // Resolving by registration order would make which credential kind gets used depend on
        // composition order, which is not a property anyone can reason about.
        var ex = Assert.Throws<ArgumentException>(() => new BackendCredentialProviders(
        [
            new AppPasswordCredentialProvider(),
            new AppPasswordCredentialProvider(),
        ]));

        Assert.Contains("app-password", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordAndSecretToString_RedactRatherThanPrint()
    {
        // A record's generated ToString prints every property. That is exactly how a credential
        // store ends up in a log by accident, so the redactions are asserted rather than assumed.
        var record = new BackendCredentialRecord
        {
            AccountId = "acct-1",
            TenantId = "tenant-alpha",
            ProviderId = "gmail",
            Discriminator = "app-password",
            ProtectedSecret = Encoding.UTF8.GetBytes("CIPHERTEXTBYTES"),
            ProtectionKeyId = "k1",
            BackendUsername = "alice@example.com",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

        Assert.DoesNotContain("CIPHERTEXTBYTES", record.ToString(), StringComparison.Ordinal);
        Assert.Contains("[redacted]", record.ToString(), StringComparison.Ordinal);

        using var secret = SecretValue.FromUtf8(ProxyHarness.AppPasswordSecret);
        Assert.Equal("[redacted]", secret.ToString());
        Assert.DoesNotContain(ProxyHarness.AppPasswordSecret, $"{secret}", StringComparison.Ordinal);

        var stored = new ProtectedSecret(Encoding.UTF8.GetBytes("CIPHERTEXTBYTES"), "k1");
        Assert.DoesNotContain("CIPHERTEXTBYTES", stored.ToString(), StringComparison.Ordinal);
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
