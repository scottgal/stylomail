using StyloMail.Desktop.Models;
using StyloMail.Desktop.Services;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// How the console decides what to tell an operator about its Host.
/// </summary>
/// <remarks>
/// The first test here exists because a screenshot of the first-run window said
/// "The Host refused the request" when the true answer was "no API key is set".
/// The cause is easy to miss: <c>GET /health/ready</c> is unauthenticated by
/// design, so the key is never consulted, and a console with no key at all
/// happily probes whatever address it has and reports whatever answers.
/// </remarks>
public sealed class AppServicesTests
{
    private static readonly Uri Host = new("https://host.test");

    private static AppServices Services(StubHttpMessageHandler handler, IKeychain keychain)
        => AppServices.Create(Host, keychain, handler);

    private static InMemoryKeychain KeychainHolding(string key)
    {
        var keychain = new InMemoryKeychain();
        new KeychainApiKeyProvider(keychain).Store(key);
        return keychain;
    }

    /// <summary>
    /// With no key there is nothing this console can do, whatever the Host says.
    /// Reporting readiness instead would send an operator to investigate a Host
    /// that is fine, when the one step they actually have to take is entering a
    /// key.
    /// </summary>
    [Fact]
    public async Task With_no_key_the_console_reports_that_and_probes_nothing()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.Ready);

        var status = await Services(handler, new InMemoryKeychain()).CheckHostAsync();

        Assert.Equal(HostStatusKind.NeedsApiKey, status.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task With_a_key_the_console_reports_what_the_host_answers()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.Ready);

        var status = await Services(handler, KeychainHolding("a-key")).CheckHostAsync();

        Assert.Equal(HostStatusKind.Ready, status.Kind);
        Assert.Equal("/health/ready", handler.SingleRequest.Path);
    }

    /// <summary>
    /// The readiness probe stays unauthenticated even once a key exists. A
    /// credential attached to a route that does not need one is exposure bought
    /// with nothing.
    /// </summary>
    [Fact]
    public async Task The_readiness_probe_sends_no_credential()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.Ready);

        await Services(handler, KeychainHolding("a-key")).CheckHostAsync();

        Assert.Null(handler.SingleRequest.Header("X-StyloMail-Key"));
    }

    /// <summary>
    /// A Host that is up but cannot durably accept mail reports its failed
    /// checks rather than an error.
    /// </summary>
    [Fact]
    public async Task A_not_ready_host_is_reported_with_its_failed_checks()
    {
        var handler = StubHttpMessageHandler.ReturningJson(
            Wire.NotReady,
            System.Net.HttpStatusCode.ServiceUnavailable);

        var status = await Services(handler, KeychainHolding("a-key")).CheckHostAsync();

        Assert.Equal(HostStatusKind.NotReady, status.Kind);
        Assert.NotEmpty(status.FailedChecks);
    }

    /// <summary>
    /// An unreachable Host is reported, not thrown. The sidebar has to render
    /// something in every one of these cases.
    /// </summary>
    [Fact]
    public async Task An_unreachable_host_is_reported_rather_than_thrown()
    {
        var handler = new StubHttpMessageHandler(
            _ => throw new HttpRequestException("Connection refused."));

        var status = await Services(handler, KeychainHolding("a-key")).CheckHostAsync();

        Assert.Equal(HostStatusKind.Unreachable, status.Kind);
    }
}
