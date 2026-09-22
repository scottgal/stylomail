using System.Text;
using System.Text.Json;
using StyloMail.AccessProxy.Backends;
using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Tests.Support;

namespace StyloMail.AccessProxy.Tests;

/// <summary>
/// "Never let a credential reach a log, exception, decision record or metric" — driven rather than
/// asserted.
/// </summary>
/// <remarks>
/// The rule is a hard constraint, and it is also the kind of rule that is easy to believe you have
/// followed. So rather than reading the code and agreeing with it, this runs the failure paths that
/// actually produce messages and inspects every one of them.
/// </remarks>
public sealed class SecretLeakTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Every secret in play, so a sweep cannot accidentally check only one of them.</summary>
    private static readonly string[] AllSecrets =
    [
        ProxyHarness.ClientPassword,
        ProxyHarness.AppPasswordSecret,
        ProxyHarness.RefreshTokenSecret,
        ProxyHarness.AccessTokenSecret,
    ];

    [Fact]
    public async Task NoFailurePath_ProducesAnExceptionContainingACredential()
    {
        var exceptions = new List<Exception>();

        // Every one of these is a real failure mode with its own message: a missing credential, a
        // revoked one, an unreadable one, an unknown kind, a provider refusal, and a token service
        // that is down.
        exceptions.Add(await CaptureAsync(async () =>
        {
            var h = new ProxyHarness();
            h.AddAccount();
            await h.Resolver.ResolveAsync("acct-1", BackendProtocol.Imap, CancellationToken.None);
        }));

        exceptions.Add(await CaptureAsync(async () =>
        {
            var h = new ProxyHarness();
            h.AddAccount();
            await h.AddBackendCredentialAsync(revoked: true);
            await h.Resolver.ResolveAsync("acct-1", BackendProtocol.Imap, CancellationToken.None);
        }));

        exceptions.Add(await CaptureAsync(async () =>
        {
            var h = new ProxyHarness();
            h.AddAccount();
            await h.AddBackendCredentialAsync(discriminator: "unknown-kind");
            await h.Resolver.ResolveAsync("acct-1", BackendProtocol.Imap, CancellationToken.None);
        }));

        exceptions.Add(await CaptureAsync(() =>
        {
            var h = new ProxyHarness();
            var binding = new SecretBinding("t", "a", "app-password");
            using var secret = SecretValue.FromUtf8(ProxyHarness.AppPasswordSecret);
            var stored = h.Protector.Protect(secret, binding);
            var wrong = new SecretBinding("t", "a", "oauth-refresh-token");
            h.Protector.Unprotect(stored.Ciphertext, stored.KeyId, wrong);
            return Task.CompletedTask;
        }));

        exceptions.Add(await CaptureAsync(async () =>
        {
            var h = new ProxyHarness();
            await h.EnrolOAuthAccountAsync();
            h.OAuth.FailWith = new HttpRequestException("token endpoint unreachable");
            await h.Resolver.ResolveAsync("acct-1", BackendProtocol.Imap, CancellationToken.None);
        }));

        exceptions.Add(await CaptureAsync(async () =>
        {
            var h = new ProxyHarness();
            await h.EnrolAppPasswordAccountAsync();
            h.Transport.AcceptCredential = false;
            await h.NewImapConnector().ConnectAsync(
                new BackendConnectionRequest
                {
                    AccountId = "acct-1",
                    Protocol = BackendProtocol.Imap,
                    TimeProvider = h.Clock,
                },
                CancellationToken.None);
        }));

        Assert.NotEmpty(exceptions);

        foreach (var exception in exceptions)
        {
            AssertNoSecret(exception);
        }
    }

    [Fact]
    public async Task ATokenEndpointThatLeaksTheTokenIntoItsException_DoesNotPropagateIt()
    {
        // The token endpoint is a seam someone else implements, so its messages are not ours to
        // trust. This is the case where an implementation interpolates the refresh token into an
        // exception — the shape every logging framework would then print in full.
        var harness = new ProxyHarness();
        await harness.EnrolOAuthAccountAsync();
        harness.OAuth.FailWith = new HttpRequestException(
            $"POST https://oauth2.googleapis.com/token failed for refresh_token={ProxyHarness.RefreshTokenSecret}");

        var exception = await CaptureAsync(async () =>
            await harness.Resolver.ResolveAsync("acct-1", BackendProtocol.Imap, CancellationToken.None));

        AssertNoSecret(exception);
    }

    [Fact]
    public async Task ATokenEndpointThatLeaksTheTokenIntoANestedException_DoesNotPropagateIt()
    {
        // One layer deeper, because exception chains are logged in full.
        var harness = new ProxyHarness();
        await harness.EnrolOAuthAccountAsync();
        harness.OAuth.FailWith = new InvalidOperationException(
            "outer",
            new HttpRequestException($"https://oauth2.googleapis.com/token?refresh_token={ProxyHarness.RefreshTokenSecret}"));

        var exception = await CaptureAsync(async () =>
            await harness.Resolver.ResolveAsync("acct-1", BackendProtocol.Imap, CancellationToken.None));

        AssertNoSecret(exception);
    }

    [Fact]
    public async Task SeriallySerializingTheCredentialRecord_YieldsNoPlaintext()
    {
        // A "decision record" or a debugging dump of the stored record is one of the four places the
        // constraint names. Serialisation is how it would get there.
        var harness = new ProxyHarness();
        var record = await harness.AddBackendCredentialAsync();

        var json = JsonSerializer.Serialize(record);
        var text = JsonSerializer.Serialize(record, record.GetType(), Indented);

        foreach (var secret in AllSecrets)
        {
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }

        // The ciphertext and its key id are present — a record that serialised to nothing would pass
        // the assertions above for the wrong reason.
        Assert.Contains("ProtectedSecret", json, StringComparison.Ordinal);
        Assert.Contains(record.ProtectionKeyId, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeriallySerializingTheAccountRecord_YieldsNoPlaintext()
    {
        var harness = new ProxyHarness();
        var account = harness.AddAccount();

        var json = JsonSerializer.Serialize(account);

        Assert.DoesNotContain(ProxyHarness.ClientPassword, json, StringComparison.Ordinal);
        Assert.Contains("PasswordHash", json, StringComparison.Ordinal);

        // And the verifier is not the password — a hash that happened to be the plaintext would pass
        // a naive "no plaintext in the record" check while being exactly the bug.
        Assert.False(Contains(Encoding.UTF8.GetBytes(json), Encoding.UTF8.GetBytes(ProxyHarness.ClientPassword)));
    }

    [Fact]
    public async Task ClientPasswordAndBackendCredential_AreIndependentSecrets()
    {
        // Spec §9.4 item 4: separate secrets with separate rotation, neither derivable from the other.
        // The test is behavioural rather than structural: rotate one and the other keeps working.
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        // Rotate the client password.
        var account = (await harness.AccountStore.FindByIdAsync("acct-1", CancellationToken.None))!;
        harness.AccountStore.Add(account with
        {
            PasswordHash = harness.PasswordHasher.Hash(Encoding.UTF8.GetBytes("a-brand-new-client-password")),
        });

        // The old client password no longer works...
        var rotated = (await harness.AccountStore.FindByIdAsync("acct-1", CancellationToken.None))!;
        Assert.False(harness.PasswordHasher.Verify(
            Encoding.UTF8.GetBytes(ProxyHarness.ClientPassword), rotated.PasswordHash));

        // ...but the backend credential is untouched and still resolves.
        using var authenticator = await harness.Resolver.ResolveAsync(
            "acct-1", BackendProtocol.Imap, CancellationToken.None);
        Assert.Equal("PLAIN", authenticator.SaslMechanism);

        // And the stored verifier is not, and does not contain, the backend credential.
        var stored = (await harness.AccountStore.FindByIdAsync("acct-1", CancellationToken.None))!;
        var hashJson = JsonSerializer.Serialize(stored);
        Assert.DoesNotContain(ProxyHarness.AppPasswordSecret, hashJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotatingTheBackendCredential_DoesNotDisturbTheClientPassword()
    {
        var harness = new ProxyHarness();
        await harness.EnrolAppPasswordAccountAsync();

        var before = (await harness.AccountStore.FindByIdAsync("acct-1", CancellationToken.None))!;

        // Move the account onto OAuth — the migration spec §9.5 is designed around.
        await harness.AddBackendCredentialAsync(
            discriminator: OAuthRefreshTokenCredentialProvider.DiscriminatorValue,
            secret: ProxyHarness.RefreshTokenSecret);

        var after = (await harness.AccountStore.FindByIdAsync("acct-1", CancellationToken.None))!;

        Assert.Equal(before.PasswordHash.Hash, after.PasswordHash.Hash);
        Assert.True(harness.PasswordHasher.Verify(
            Encoding.UTF8.GetBytes(ProxyHarness.ClientPassword), after.PasswordHash));

        using var authenticator = await harness.Resolver.ResolveAsync(
            "acct-1", BackendProtocol.Imap, CancellationToken.None);
        Assert.Equal("XOAUTH2", authenticator.SaslMechanism);
    }

    private static void AssertNoSecret(Exception exception)
    {
        var text = Flatten(exception);

        foreach (var secret in AllSecrets)
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    private static string Flatten(Exception exception)
    {
        var builder = new StringBuilder();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            builder.AppendLine(current.GetType().Name);
            builder.AppendLine(current.Message);
        }

        return builder.ToString();
    }

    private static async Task<Exception> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("Expected the scenario to throw, so there is a message to inspect.");
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
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
