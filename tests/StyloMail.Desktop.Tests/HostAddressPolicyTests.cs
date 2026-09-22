using StyloMail.Desktop.Services;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// Which Host addresses the console will talk to.
/// </summary>
/// <remarks>
/// Every request the console makes carries the operator's API key in a header.
/// Pointed at a remote Host over plain http, that key travels in clear text,
/// and it can read every decision and release every quarantine. So this is a
/// security boundary rather than an input-validation nicety, and it is tested
/// as one.
/// </remarks>
public sealed class HostAddressPolicyTests
{
    private static bool Accepts(string address)
        => HostAddressPolicy.IsAcceptable(new Uri(address), out _);

    [Theory]
    [InlineData("https://stylomail.example.test")]
    [InlineData("https://stylomail.example.test:8443")]
    [InlineData("wss://stylomail.example.test")]
    public void Https_is_always_acceptable(string address)
        => Assert.True(Accepts(address));

    /// <summary>Loopback is the one place a plain connection is defensible.</summary>
    [Theory]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://localhost:5000")]
    [InlineData("http://[::1]:5000")]
    public void Plain_http_is_acceptable_on_this_machine(string address)
        => Assert.True(Accepts(address));

    /// <summary>
    /// The rule that matters. A remote plain-http Host is refused, and the
    /// refusal says why rather than just saying no.
    /// </summary>
    [Theory]
    [InlineData("http://stylomail.example.test")]
    [InlineData("http://10.0.0.5:5000")]
    [InlineData("http://mail.internal:8080")]
    public void Plain_http_to_another_machine_is_refused(string address)
    {
        Assert.False(Accepts(address));

        HostAddressPolicy.IsAcceptable(new Uri(address), out var refusal);

        Assert.NotNull(refusal);
        Assert.Contains("unencrypted", refusal, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A name that merely looks local is not local. Resolving is not this
    /// function's job, and guessing would be the whole vulnerability.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1.example.test")]
    [InlineData("http://localhost.example.test")]
    [InlineData("http://notlocalhost")]
    public void A_name_that_only_looks_local_is_refused(string address)
        => Assert.False(Accepts(address));

    [Theory]
    [InlineData("ftp://stylomail.example.test")]
    [InlineData("file:///etc/passwd")]
    public void An_unexpected_scheme_is_refused_with_its_own_reason(string address)
    {
        Assert.False(Accepts(address));

        HostAddressPolicy.IsAcceptable(new Uri(address), out var refusal);

        Assert.NotNull(refusal);
        Assert.Contains("scheme", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_incomplete_address_is_refused()
    {
        Assert.False(HostAddressPolicy.IsAcceptable(null, out var nullRefusal));
        Assert.NotNull(nullRefusal);

        Assert.False(HostAddressPolicy.IsAcceptable(new Uri("/v1/decisions", UriKind.Relative), out _));
    }

    /// <summary>
    /// The console's own default has to pass its own rule, or a first run
    /// fails at the thing it was shipped with.
    /// </summary>
    [Fact]
    public void The_console_default_address_is_acceptable()
    {
        var defaultAddress = new Uri("http://127.0.0.1:5000");

        Assert.True(HostAddressPolicy.IsAcceptable(defaultAddress, out _));
        Assert.True(HostAddressPolicy.IsLoopback(defaultAddress));
    }
}
