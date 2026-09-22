using System.Net;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// The management routes: a sender's profile, and the companies they group into.
/// </summary>
/// <remarks>
/// Four of these pin decisions the Host made that the console has to respect
/// rather than discover: a 200-with-nulls for an undescribed sender, a full
/// replace rather than a merge, a closed posture set, and an author the client
/// cannot choose.
/// </remarks>
public sealed class ManagementTests
{
    private static StyloMailApiClient Client(StubHttpMessageHandler handler)
        => new(handler.CreateClient(), new TestApiKeyProvider());

    // ===================== sender settings =====================

    [Fact]
    public async Task Reading_a_senders_settings_requests_its_own_route()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.SenderSettings);

        await Client(handler).GetSenderSettingsAsync("acme-outbound");

        Assert.Equal(HttpMethod.Get, handler.SingleRequest.Method);
        Assert.Equal("/v1/senders/acme-outbound/settings", handler.SingleRequest.Path);
    }

    /// <summary>
    /// A sender whose settings nobody has written is a 200 with nulls, and the
    /// console must be able to tell that from a described-but-blank one.
    /// </summary>
    [Fact]
    public async Task An_undescribed_sender_is_not_an_error_and_says_so()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.SenderSettingsUndescribed);

        var settings = await Client(handler).GetSenderSettingsAsync("quiet@example.test");

        Assert.Equal("quiet@example.test", settings.PrincipalId);
        Assert.Null(settings.Label);
        Assert.Null(settings.CompanyId);

        // The distinction the console groups by: never described, rather than
        // described as belonging to nothing.
        Assert.False(settings.IsDescribed);
    }

    [Fact]
    public async Task A_described_sender_binds_every_field()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.SenderSettings);

        var settings = await Client(handler).GetSenderSettingsAsync("acme-outbound");

        Assert.True(settings.IsDescribed);
        Assert.Equal("Acme outbound", settings.Label);
        Assert.Equal("co_7f3a", settings.CompanyId);
        Assert.Equal("crm-99213", settings.ExternalRef);
        Assert.Equal("ops@acme.test", settings.NotificationTarget);
        Assert.Equal(SenderPosture.Watch, settings.Posture);
        Assert.Equal("ops@acme.test", settings.UpdatedBy);
    }

    /// <summary>
    /// A write replaces the whole profile, so the request must carry the fields
    /// the form is not showing. Asserted by sending the whole shape and reading
    /// what went on the wire.
    /// </summary>
    [Fact]
    public async Task Writing_senders_settings_sends_the_whole_profile()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.SenderSettings);

        await Client(handler).SaveSenderSettingsAsync("acme-outbound", new SenderSettingsRequest
        {
            Label = "Acme outbound",
            CompanyId = "co_7f3a",
            Notes = "Primary marketing account",
            ExternalRef = "crm-99213",
            NotificationTarget = "ops@acme.test",
            Posture = SenderPosture.Watch,
        });

        Assert.Equal(HttpMethod.Put, handler.SingleRequest.Method);

        var body = handler.SingleRequest.Body;
        Assert.NotNull(body);

        // Company id included even though this hypothetical form was editing a
        // note: omitting it is what would erase the company.
        Assert.Contains("\"companyId\":\"co_7f3a\"", body, StringComparison.Ordinal);
        Assert.Contains("\"posture\":\"watch\"", body, StringComparison.Ordinal);
    }

    /// <summary>The console never names the author. The Host takes it from the principal.</summary>
    [Fact]
    public async Task A_settings_write_carries_no_author()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.SenderSettings);

        await Client(handler).SaveSenderSettingsAsync("acme-outbound", new SenderSettingsRequest
        {
            Label = "Acme outbound",
        });

        Assert.DoesNotContain("updatedBy", handler.SingleRequest.Body!, StringComparison.Ordinal);
    }

    /// <summary>A stance the Host does not recognise is refused by name.</summary>
    [Fact]
    public async Task An_unknown_posture_is_a_named_refusal()
    {
        var handler = StubHttpMessageHandler.ReturningJson(
            Wire.Error("unknown_posture", "'gold' is not a posture. Valid: trusted, normal, watch."),
            HttpStatusCode.BadRequest);

        var exception = await Assert.ThrowsAsync<StyloMailApiException>(
            () => Client(handler).SaveSenderSettingsAsync("acme-outbound", new SenderSettingsRequest
            {
                Posture = "gold",
            }));

        Assert.Equal("unknown_posture", exception.Code);
    }

    /// <summary>
    /// The posture set is closed on both sides. A console that could send a
    /// value the Host refuses would fail at runtime, in front of an operator,
    /// for something knowable at the call site.
    /// </summary>
    [Theory]
    [InlineData("trusted", true)]
    [InlineData("normal", true)]
    [InlineData("watch", true)]
    [InlineData("gold", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_posture_set_is_closed(string? posture, bool known)
        => Assert.Equal(known, SenderPosture.IsKnown(posture));

    // ===================== companies =====================

    [Fact]
    public async Task Listing_companies_requests_their_route()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.CompanyListing);

        var listing = await Client(handler).GetCompaniesAsync();

        Assert.Equal("/v1/companies", handler.SingleRequest.Path);
        Assert.Equal("smoke", listing.TenantId);

        var company = Assert.Single(listing.Companies);
        Assert.Equal("co_7f3a", company.CompanyId);
        Assert.Equal("Acme", company.Name);
        Assert.Equal("ops@acme.test", company.UpdatedBy);
    }

    /// <summary>The Host mints the id, so the client does not offer one.</summary>
    [Fact]
    public async Task Creating_a_company_sends_no_identifier()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.Company);

        await Client(handler).CreateCompanyAsync(new CompanyRequest { Name = "Acme" });

        Assert.Equal(HttpMethod.Post, handler.SingleRequest.Method);
        Assert.Equal("/v1/companies", handler.SingleRequest.Path);
        Assert.DoesNotContain("companyId", handler.SingleRequest.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Updating_a_company_addresses_it_by_id()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.Company);

        await Client(handler).SaveCompanyAsync("co_7f3a", new CompanyRequest { Name = "Acme Ltd" });

        Assert.Equal(HttpMethod.Put, handler.SingleRequest.Method);
        Assert.Equal("/v1/companies/co_7f3a", handler.SingleRequest.Path);
        Assert.Contains("Acme Ltd", handler.SingleRequest.Body!, StringComparison.Ordinal);
    }

    /// <summary>An id that is an address-shaped string still has to survive the path.</summary>
    [Fact]
    public async Task A_principal_identifier_is_escaped_into_the_path()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.SenderSettings);

        await Client(handler).GetSenderSettingsAsync("sender@example.test");

        Assert.Equal("/v1/senders/sender%40example.test/settings", handler.SingleRequest.Path);
    }

    /// <summary>
    /// The sidebar needs no settings call per sender, which is what the row
    /// fields are for.
    /// </summary>
    [Fact]
    public async Task The_sender_listing_carries_the_label_and_company()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.SenderListing);

        var listing = await Client(handler).GetSendersAsync();

        var described = listing.Senders.Single(sender => sender.PrincipalId == "compromised@example.test");
        Assert.Equal("Acme outbound", described.Label);
        Assert.Equal("co_7f3a", described.CompanyId);

        // Null rather than empty: nobody described this one, which the sidebar
        // groups differently from a sender filed under no company.
        var undescribed = listing.Senders.Single(sender => sender.PrincipalId == "quiet@example.test");
        Assert.Null(undescribed.Label);
        Assert.Null(undescribed.CompanyId);
    }
}
