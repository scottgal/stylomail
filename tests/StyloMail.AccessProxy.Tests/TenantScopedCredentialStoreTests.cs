using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Tests.Support;

namespace StyloMail.AccessProxy.Tests;

public sealed class TenantScopedCredentialStoreTests
{
    [Fact]
    public async Task Credential_in_one_tenant_does_not_resolve_for_another_tenant()
    {
        var harness = new ProxyHarness();
        using var secret = SecretValue.FromUtf8(ProxyHarness.AppPasswordSecret);

        await harness.CredentialStore.StoreAsync(
            new BackendCredentialWrite
            {
                TenantId = ProxyHarness.TenantId,
                AccountId = "shared-backend-account",
                ProviderId = "gmail",
                Discriminator = AppPasswordCredentialProvider.DiscriminatorValue,
                BackendUsername = ProxyHarness.Login,
                Secret = secret,
                TimeProvider = harness.Clock,
            },
            CancellationToken.None);

        using var positiveControl = await harness.Resolver.ResolveAsync(
            ProxyHarness.TenantId,
            "shared-backend-account",
            BackendProtocol.Imap,
            CancellationToken.None);

        await Assert.ThrowsAsync<CredentialUnavailableException>(async () =>
            await harness.Resolver.ResolveAsync(
                "tenant-beta",
                "shared-backend-account",
                BackendProtocol.Imap,
                CancellationToken.None));
    }
}
