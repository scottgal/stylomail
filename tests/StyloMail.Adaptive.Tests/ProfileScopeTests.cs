using StyloMail.Adaptive.Profiles;
using StyloMail.Core;

namespace StyloMail.Adaptive.Tests;

public class ProfileScopeTests
{
    private const string TenantA = "tenant-a";

    [Fact]
    public void DomainContextIsNotAccountTrust()
    {
        var domain = ProfileScopes.Domain(TenantA, "example.com");

        Assert.Equal(ProfileScopeKind.DomainContext, domain.Scope);
        Assert.False(ProfileScopeTrust.IsAccountTrust(domain.Scope));
        Assert.True(ProfileScopeTrust.IsFallback(domain.Scope));
    }

    [Fact]
    public void SenderIdentityScopesAreAccountTrust()
    {
        Assert.True(ProfileScopeTrust.IsAccountTrust(ProfileScopeKind.OutboundSender));
        Assert.True(ProfileScopeTrust.IsAccountTrust(ProfileScopeKind.InboundSenderIdentity));
        Assert.False(ProfileScopeTrust.IsFallback(ProfileScopeKind.InboundSenderIdentity));
    }

    [Fact]
    public void TenantTrafficClassIsNotAccountTrust()
    {
        var key = ProfileScopes.TenantTrafficClass(TenantA, "scheduled-bulk");

        Assert.False(ProfileScopeTrust.IsAccountTrust(key.Scope));
        Assert.False(ProfileScopeTrust.IsFallback(key.Scope));
    }

    [Fact]
    public void SenderProfilesKeepInboundAndOutboundDistinct()
    {
        var outbound = ProfileScopes.OutboundSender(TenantA, "hash-of-account");
        var inbound = ProfileScopes.InboundSender(TenantA, "hash-of-account", NoAuthentication());

        Assert.Equal(MailDirection.Outbound, outbound.Direction);
        Assert.Equal(MailDirection.Inbound, inbound.Direction);
        Assert.NotEqual(outbound, inbound);
    }

    [Fact]
    public void InboundSenderKeySeparatesAuthenticationProvenance()
    {
        var authenticated = ProfileScopes.InboundSender(
            TenantA, "hash-of-sender", Authentication(("dkim", "pass"), ("spf", "pass")));
        var unauthenticated = ProfileScopes.InboundSender(
            TenantA, "hash-of-sender", NoAuthentication());

        // Same claimed identity, different authentication outcome. A forged From must not
        // inherit the trust accrued by the real sender's authenticated traffic.
        Assert.NotEqual(authenticated.Key, unauthenticated.Key);
        Assert.Contains("auth=dkim=pass,spf=pass", authenticated.Key);
        Assert.Contains("auth=none", unauthenticated.Key);
    }

    [Fact]
    public void ProvenanceIsStableRegardlessOfResultOrdering()
    {
        var first = ProfileScopes.InboundSender(
            TenantA, "hash-of-sender", Authentication(("dkim", "pass"), ("spf", "pass")));
        var second = ProfileScopes.InboundSender(
            TenantA, "hash-of-sender", Authentication(("spf", "pass"), ("dkim", "pass")));

        Assert.Equal(first.Key, second.Key);
    }

    [Fact]
    public void UntrustedAuthenticationResultsDoNotShapeProvenance()
    {
        var claimed = new AuthenticationContext
        {
            Results =
            [
                new AuthenticationResult
                {
                    Mechanism = "dkim",
                    Result = "pass",
                    FromTrustedVerifier = false,
                    VerifierId = "self-asserted",
                },
            ],
            ApprovedSenderIdentities = [],
            ProvenanceIncomplete = true,
        };

        var key = ProfileScopes.InboundSender(TenantA, "hash-of-sender", claimed);

        // A message cannot assert its own authentication.
        Assert.Contains("auth=none", key.Key);
        Assert.DoesNotContain("dkim=pass", key.Key);
    }

    [Fact]
    public void RelationshipKeysLinkDirectionsWithoutMergingThem()
    {
        var outbound = ProfileScopes.Relationship(TenantA, MailDirection.Outbound, "sender-h", "recipient-h");
        var inbound = ProfileScopes.Relationship(TenantA, MailDirection.Inbound, "sender-h", "recipient-h");

        // The pair is explicitly linkable, same key, but the statistics never merge,
        // because direction is part of identity.
        Assert.Equal("sender-h>recipient-h", outbound.Key);
        Assert.Equal(outbound.Key, inbound.Key);
        Assert.NotEqual(outbound, inbound);
        Assert.Equal(MailDirection.Outbound, outbound.Direction);
        Assert.Equal(MailDirection.Inbound, inbound.Direction);
    }

    [Fact]
    public void RecipientProfilesAreDirectionScoped()
    {
        var outbound = ProfileScopes.Recipient(TenantA, MailDirection.Outbound, "recipient-h");
        var inbound = ProfileScopes.Recipient(TenantA, MailDirection.Inbound, "recipient-h");

        Assert.NotEqual(outbound, inbound);
    }

    [Fact]
    public void TenantTrafficClassScopeCarriesNoDirection()
    {
        Assert.Null(ProfileScopes.TenantTrafficClass(TenantA, "conversational").Direction);
    }

    private static AuthenticationContext NoAuthentication() => new()
    {
        Results = [],
        ApprovedSenderIdentities = [],
        ProvenanceIncomplete = true,
    };

    private static AuthenticationContext Authentication(params (string Mechanism, string Result)[] results) => new()
    {
        Results = [.. results.Select(r => new AuthenticationResult
        {
            Mechanism = r.Mechanism,
            Result = r.Result,
            VerifierId = "boundary-1",
            FromTrustedVerifier = true,
        })],
        ApprovedSenderIdentities = [],
        ProvenanceIncomplete = false,
    };
}
