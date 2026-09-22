using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StyloMail.Host.Contracts;
using StyloMail.Host.Serialization;

namespace StyloMail.Host.Tests;

/// <summary>
/// The operator metadata surface: a sender's profile and the companies senders are grouped into.
/// </summary>
/// <remarks>
/// Read on <c>Review</c>, write on <c>Administer</c> — the split the pause route already draws. The
/// tests here are mostly about that split and about tenant scoping, because this is metadata about
/// senders rather than about messages: getting tenancy wrong here leaks who a tenant's senders are
/// rather than what they sent.
/// </remarks>
public sealed class ManagementSurfaceTests
{
    // ---------------------------------------------------------------------------------------------
    // Privilege split
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reading_a_profile_needs_review_and_writing_it_needs_administer()
    {
        using var host = new TestHost();

        using var anonymous = host.Anonymous();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/v1/senders/{TestPrincipals.AcmeSenderPrincipal}/settings")).StatusCode);

        using var sender = host.ClientAs(TestPrincipals.AcmeSenderKey);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await sender.GetAsync($"/v1/senders/{TestPrincipals.AcmeSenderPrincipal}/settings")).StatusCode);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        // Reading is the reviewer's: describing a sender is what a reviewer needs to act on one.
        Assert.Equal(
            HttpStatusCode.OK,
            (await reviewer.GetAsync($"/v1/senders/{TestPrincipals.AcmeSenderPrincipal}/settings")).StatusCode);

        // Writing is not. A reviewer who may read who a sender is has no business renaming them.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await reviewer.PutAsJsonAsync(
                $"/v1/senders/{TestPrincipals.AcmeSenderPrincipal}/settings",
                new { label = "not permitted" })).StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Sender settings
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_sender_nobody_has_described_reads_as_unset_rather_than_missing()
    {
        // Not a 404. The caller asked a sensible question about a sender that exists, and the honest
        // answer is "nothing recorded yet" — a 404 would read as "no such sender" and send the console
        // looking for a principal that is right there in the listing.
        using var host = new TestHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var response = await reviewer.GetAsync($"/v1/senders/{TestPrincipals.AcmeSenderPrincipal}/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var settings = JsonSerializer.Deserialize<SenderSettingsResponse>(
            await response.Content.ReadAsStringAsync(), HostJson.Options);

        Assert.NotNull(settings);
        Assert.Equal(TestPrincipals.AcmeSenderPrincipal, settings!.PrincipalId);
        Assert.Null(settings.Label);
        Assert.Null(settings.CompanyId);
        Assert.Null(settings.UpdatedAt);
    }

    [Fact]
    public async Task A_profile_round_trips_and_records_who_wrote_it()
    {
        using var host = new TestHost();
        using var operatorClient = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var written = await operatorClient.PutAsJsonAsync(
            $"/v1/senders/{TestPrincipals.AcmeSenderPrincipal}/settings",
            new
            {
                label = "Acme outbound",
                companyId = "co_acme",
                notes = "primary sender",
                externalRef = "acme-001",
                notificationTarget = "ops@acme.example",
                posture = "watch",
            });

        Assert.Equal(HttpStatusCode.OK, written.StatusCode);

        var settings = JsonSerializer.Deserialize<SenderSettingsResponse>(
            await written.Content.ReadAsStringAsync(), HostJson.Options);

        Assert.NotNull(settings);
        Assert.Equal("Acme outbound", settings!.Label);
        Assert.Equal("co_acme", settings.CompanyId);
        Assert.Equal("watch", settings.Posture);

        // The audit stamp comes from the principal, never from the body. A caller that can set its own
        // "who did this" has not been audited.
        Assert.Equal(TestPrincipals.AcmeOperatorPrincipal, settings.UpdatedBy);
        Assert.NotNull(settings.UpdatedAt);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var reread = JsonSerializer.Deserialize<SenderSettingsResponse>(
            await (await reviewer.GetAsync($"/v1/senders/{TestPrincipals.AcmeSenderPrincipal}/settings"))
                .Content.ReadAsStringAsync(),
            HostJson.Options);

        Assert.Equal("Acme outbound", reread!.Label);
    }

    [Fact]
    public async Task Writing_a_profile_clears_what_the_body_omits()
    {
        // A full replace, not a merge: the console sends the whole form, and a merge would make it
        // impossible for an operator to remove a label they no longer want.
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await client.PutAsJsonAsync(
            $"/v1/senders/{TestPrincipals.AcmeSenderPrincipal}/settings",
            new { label = "Acme outbound", notes = "first pass" });

        var second = await client.PutAsJsonAsync(
            $"/v1/senders/{TestPrincipals.AcmeSenderPrincipal}/settings",
            new { label = "Acme outbound" });

        var settings = JsonSerializer.Deserialize<SenderSettingsResponse>(
            await second.Content.ReadAsStringAsync(), HostJson.Options);

        Assert.Null(settings!.Notes);
    }

    [Fact]
    public async Task A_posture_this_host_does_not_recognise_is_refused_by_name()
    {
        // Posture is a closed vocabulary. A stored stance nothing recognises is worse than no stance,
        // because it looks like a decision someone made.
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var response = await client.PutAsJsonAsync(
            $"/v1/senders/{TestPrincipals.AcmeSenderPrincipal}/settings",
            new { posture = "trustd" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unknown_posture", body.RootElement.GetProperty("error").GetString());
        Assert.Contains("watch", body.RootElement.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_profile_is_scoped_to_the_tenant_that_wrote_it()
    {
        // Two tenants may legitimately use the same principal id, so an unscoped write would let one
        // tenant describe another's sender — and the console would then show a name that tenant never
        // chose.
        using var host = new TestHost();

        using (var acme = host.ClientAs(TestPrincipals.AcmeOperatorKey))
        {
            await acme.PutAsJsonAsync(
                $"/v1/senders/{TestPrincipals.AcmeSenderPrincipal}/settings",
                new { label = "Acme outbound" });
        }

        using var globex = host.ClientAs(TestPrincipals.GlobexReviewerKey);
        var settings = JsonSerializer.Deserialize<SenderSettingsResponse>(
            await (await globex.GetAsync($"/v1/senders/{TestPrincipals.AcmeSenderPrincipal}/settings"))
                .Content.ReadAsStringAsync(),
            HostJson.Options);

        Assert.Null(settings!.Label);
    }

    // ---------------------------------------------------------------------------------------------
    // Companies
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_company_mints_an_id_and_lists_it()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var created = await client.PostAsJsonAsync("/v1/companies", new { name = "Acme Group" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var company = JsonSerializer.Deserialize<CompanyResponse>(
            await created.Content.ReadAsStringAsync(), HostJson.Options);

        Assert.NotNull(company);
        Assert.StartsWith("co_", company!.CompanyId, StringComparison.Ordinal);
        Assert.Equal("Acme Group", company.Name);
        Assert.Equal(TestPrincipals.AcmeOperatorPrincipal, company.UpdatedBy);

        var listing = await ListingAsync(client);
        Assert.Single(listing.Companies);
    }

    [Fact]
    public async Task A_company_can_be_renamed_by_id()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var company = JsonSerializer.Deserialize<CompanyResponse>(
            await (await client.PostAsJsonAsync("/v1/companies", new { name = "First name" }))
                .Content.ReadAsStringAsync(),
            HostJson.Options);

        var renamed = await client.PutAsJsonAsync(
            $"/v1/companies/{company!.CompanyId}", new { name = "Second name" });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var listing = await ListingAsync(client);
        Assert.Equal("Second name", Assert.Single(listing.Companies).Name);
    }

    [Fact]
    public async Task Companies_are_scoped_to_the_calling_tenant()
    {
        using var host = new TestHost();

        using (var acme = host.ClientAs(TestPrincipals.AcmeOperatorKey))
        {
            await acme.PostAsJsonAsync("/v1/companies", new { name = "Acme Group" });
        }

        using var globex = host.ClientAs(TestPrincipals.GlobexReviewerKey);

        Assert.Empty((await ListingAsync(globex)).Companies);
    }

    [Theory]
    [InlineData("/v1/companies", "POST")]
    [InlineData("/v1/companies/co_anything", "PUT")]
    public async Task Writing_a_company_needs_administer(string route, string method)
    {
        using var host = new TestHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var request = new HttpRequestMessage(new HttpMethod(method), route)
        {
            Content = JsonContent.Create(new { name = "not permitted" }),
        };

        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.SendAsync(request)).StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // The sidebar's listing carries the grouping keys
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_sender_listing_carries_the_label_and_company_so_the_sidebar_groups_without_a_call_per_sender()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await client.PutAsJsonAsync(
            $"/v1/senders/{TestPrincipals.AcmeSenderPrincipal}/settings",
            new { label = "Acme outbound", companyId = "co_acme" });

        var listing = JsonSerializer.Deserialize<SenderListingResponse>(
            await (await client.GetAsync("/v1/senders")).Content.ReadAsStringAsync(), HostJson.Options);

        var row = Assert.IsType<SenderListingResponse>(listing).Senders
            .Single(s => s.PrincipalId == TestPrincipals.AcmeSenderPrincipal);

        Assert.Equal("Acme outbound", row.Label);
        Assert.Equal("co_acme", row.CompanyId);

        // And a sender with no profile is null, not an empty string — "nobody described this" and
        // "described as blank" are different facts.
        var undescribed = listing.Senders.First(s => s.PrincipalId == TestPrincipals.AcmeReviewerPrincipal);
        Assert.Null(undescribed.Label);
        Assert.Null(undescribed.CompanyId);
    }

    [Fact]
    public async Task The_sender_listing_still_names_no_credential()
    {
        // The listing grew two fields. This is the assertion that says growing it did not start
        // publishing the configuration it is built from.
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await client.PutAsJsonAsync(
            $"/v1/senders/{TestPrincipals.AcmeSenderPrincipal}/settings",
            new { label = "Acme outbound" });

        var body = await (await client.GetAsync("/v1/senders")).Content.ReadAsStringAsync();

        foreach (var key in new[] { TestPrincipals.AcmeSenderKey, TestPrincipals.AcmeOperatorKey,
                     TestPrincipals.AcmeReviewerKey, TestPrincipals.GlobexSenderKey })
        {
            Assert.DoesNotContain(key, body, StringComparison.Ordinal);
        }
    }

    private static async Task<CompanyListingResponse> ListingAsync(HttpClient client)
    {
        var response = await client.GetAsync("/v1/companies");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return Assert.IsType<CompanyListingResponse>(JsonSerializer.Deserialize<CompanyListingResponse>(
            await response.Content.ReadAsStringAsync(), HostJson.Options));
    }
}
