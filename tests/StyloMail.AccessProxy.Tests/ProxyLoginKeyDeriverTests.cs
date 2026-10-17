using StyloMail.AccessProxy.Accounts;

namespace StyloMail.AccessProxy.Tests;

public sealed class ProxyLoginKeyDeriverTests
{
    [Fact]
    public void Tenant_and_host_key_are_both_required_for_the_login_pseudonym()
    {
        var firstKey = new byte[32];
        firstKey[0] = 1;
        var secondKey = new byte[32];
        secondKey[0] = 2;
        using var first = new HmacProxyLoginKeyDeriver(firstKey);
        using var second = new HmacProxyLoginKeyDeriver(secondKey);
        var alpha = first.Derive("tenant-alpha", "ALICE@EXAMPLE.COM");
        var beta = first.Derive("tenant-beta", "ALICE@EXAMPLE.COM");
        var rotated = second.Derive("tenant-alpha", "ALICE@EXAMPLE.COM");

        Assert.NotEqual(alpha, beta);
        Assert.NotEqual(alpha, rotated);
    }
}
