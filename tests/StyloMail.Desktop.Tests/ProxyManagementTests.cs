using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.ViewModels;
using StyloMail.Desktop.Views;

namespace StyloMail.Desktop.Tests;

public sealed class ProxyManagementTests
{
    private static StyloMailApiClient Client(StubHttpMessageHandler handler) => new(handler.CreateClient(), new TestApiKeyProvider());

    [Fact]
    public async Task Operations_requests_only_the_frozen_last_24_hours_window()
    {
        var handler = StubHttpMessageHandler.ReturningJson(OperationsJson);
        await Client(handler).GetProxyOperationsAsync("last_24_hours");
        Assert.Equal("/v1/proxy/operations?window=last_24_hours", handler.SingleRequest.PathAndQuery);
    }

    [Fact]
    public void Not_produced_operations_are_unknown_not_zero_and_name_the_window()
    {
        var text = ProxyManagementDialog.DescribeOperations(Operations("not_produced", []));
        Assert.Contains("last_24_hours", text, StringComparison.Ordinal);
        Assert.Contains("not produced", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown, not zero", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rendered_rows_distinguish_disabled_from_listener_fault_and_exclude_sensitive_seeds()
    {
        var account = ProxyAccountRow.From(new ProxyAccountResponse { AccountId = "acc_safe", LoginKey = "pseudonym_4", Enabled = false, ProviderId = "mail", CredentialDiscriminator = "managed", EnrollmentState = "active", CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch });
        var listener = ProxyManagementDialog.DescribeOperations(Operations("available", [new ProxyListenerResponse { ListenerId = "listener_safe", Protocol = "Smtp", Enabled = true, State = "faulted", TlsPolicy = "Required", Draining = false, FaultReason = "CertificateUnavailable", SafeDetail = "certificate unavailable" }]));
        Assert.Contains("disabled", account.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fault CertificateUnavailable", listener, StringComparison.Ordinal);
        var rendered = account.Summary + account.Detail + listener;
        var forbidden = new[] { "raw.login@example.test", "backend-user", "127.0.0.1" };
        // Control: the negative check itself fails when an unsafe source is
        // rendered, rather than going green because its strings never occur.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoLeaks("raw.login@example.test", forbidden));
        AssertNoLeaks(rendered, forbidden);
    }

    [Fact]
    public async Task A_forbidden_account_mutation_renders_its_refusal_and_returns_to_an_enabled_state()
    {
        var model = new ProxyAccountMutationViewModel();

        var completed = await model.ExecuteAsync(
            () => Client(StubHttpMessageHandler.ReturningJson(
                "{\"error\":\"forbidden\",\"detail\":\"Review-only access cannot change accounts.\"}",
                System.Net.HttpStatusCode.Forbidden)).SetProxyAccountEnabledAsync("acc_safe", true),
            "unreachable success text");

        Assert.False(completed);
        Assert.False(model.IsPending);
        Assert.Contains("Review-only access", model.StatusLabel, StringComparison.Ordinal);
    }

    private static ProxyOperationsResponse Operations(string availability, IReadOnlyList<ProxyListenerResponse> listeners) => new()
    {
        TenantId = "tenant", Window = "last_24_hours", WindowStart = DateTimeOffset.UnixEpoch,
        WindowEnd = DateTimeOffset.UnixEpoch.AddHours(24), Availability = availability, Listeners = listeners,
        Counters = new ProxyCounters(), Provenance = "proxy_listener_events",
    };

    private static void AssertNoLeaks(string rendered, IEnumerable<string> forbidden)
    {
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, rendered, StringComparison.Ordinal);
        }
    }

    private const string OperationsJson = """{"tenantId":"tenant","window":"last_24_hours","windowStart":"1970-01-01T00:00:00+00:00","windowEnd":"1970-01-02T00:00:00+00:00","availability":"not_produced","listeners":[],"counters":{},"provenance":"proxy_listener_events"}""";
}
