using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StyloMail.AccessProxy.Accounts;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Sessions;
using StyloMail.Embeddings;
using StyloMail.Host.Contracts;
using StyloMail.Host.Proxy;
using StyloMail.Host.Serialization;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Tests;

/// <summary>
/// The <c>/v1/proxy</c> management routes: tenant scope from claims, the Review/Administer split,
/// typed conflicts, cursor binding, drain-aware listener control and the not-produced/zero
/// distinction.
/// </summary>
public sealed class ProxyRoutesTests
{
    private const string ProxyEnabled = "StyloMail:Proxy:Enabled";
    private static readonly string[] ExpectedParticipantTokens = ["pa_account"];
    private static readonly string[] ExpectedWarningMarkers = ["DisplayNameMismatch"];
    private static readonly string[] ExpectedFindingIds = ["finding-1"];
    private static readonly string[] ExpectedWarningMarkersForSubmission = ["AuthenticationFailed"];

    // ---------------------------------------------------------------------------------------------
    // Privilege split and tenant scope
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reads_need_review_and_mutations_need_administer()
    {
        using var host = ProxyHost();

        using var anonymous = host.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/proxy/accounts")).StatusCode);

        using var sender = host.ClientAs(TestPrincipals.AcmeSenderKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await sender.GetAsync("/v1/proxy/accounts")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await sender.PostAsJsonAsync("/v1/proxy/accounts", AccountBody("alice@example.com", "b-1"))).StatusCode);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        Assert.Equal(HttpStatusCode.OK, (await reviewer.GetAsync("/v1/proxy/accounts")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await reviewer.PostAsJsonAsync("/v1/proxy/accounts", AccountBody("alice@example.com", "b-1"))).StatusCode);

        using var operatorClient = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        Assert.Equal(
            HttpStatusCode.Created,
            (await operatorClient.PostAsJsonAsync("/v1/proxy/accounts", AccountBody("alice@example.com", "b-1"))).StatusCode);
    }

    [Fact]
    public async Task An_account_created_in_one_tenant_is_absent_from_another()
    {
        using var host = ProxyHost();
        using var acme = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var created = await EnrolAsync(acme, "alice@example.com", "backend-1");

        using var globex = host.ClientAs(TestPrincipals.GlobexReviewerKey);
        var listing = await ReadAsync<ProxyAccountsResponse>(
            await globex.GetAsync("/v1/proxy/accounts"));

        Assert.Empty(listing.Accounts);

        // Addressed by the same opaque id, in another tenant, the target does not exist.
        using var globexOperator = host.ClientAs(TestPrincipals.GlobexOperatorKey);
        var disabled = await globexOperator.PostAsync($"/v1/proxy/accounts/{created.AccountId}/disable", null);
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Enrolment shape and typed conflicts
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Enrolment_is_metadata_only_and_never_echoes_identity_or_secrets()
    {
        using var host = ProxyHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var body = AccountBody("alice@example.com", "backend-1");
        var response = await client.PostAsJsonAsync("/v1/proxy/accounts", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("alice@example.com", raw);
        Assert.DoesNotContain("backend-user", raw);
        Assert.DoesNotContain("client-secret-value", raw);
        Assert.DoesNotContain("backend-secret-value", raw);

        var created = JsonSerializer.Deserialize<ProxyAccountCreatedResponse>(raw, HostJson.Options);
        Assert.NotNull(created);
        Assert.StartsWith("pa_", created!.AccountId);
        Assert.True(created.Enabled);
        Assert.Equal("active", created.EnrollmentState);
        Assert.Equal(TestPrincipals.AcmeTenant, created.TenantId);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var listingRaw = await (await reviewer.GetAsync("/v1/proxy/accounts")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("alice@example.com", listingRaw);
        Assert.DoesNotContain("backend-user", listingRaw);
        Assert.DoesNotContain("client-secret-value", listingRaw);
        Assert.DoesNotContain("backend-secret-value", listingRaw);

        var listing = JsonSerializer.Deserialize<ProxyAccountsResponse>(listingRaw, HostJson.Options)!;
        var account = Assert.Single(listing.Accounts);
        Assert.DoesNotContain("@", account.LoginKey);
        Assert.Equal("active", account.EnrollmentState);
    }

    [Fact]
    public async Task A_duplicate_login_is_a_conflict()
    {
        using var host = ProxyHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await EnrolAsync(client, "alice@example.com", "backend-1");

        var duplicate = await client.PostAsJsonAsync(
            "/v1/proxy/accounts",
            AccountBody("alice@example.com", "backend-2"));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("duplicate_login", await ErrorCodeAsync(duplicate));
    }

    [Theory]
    [InlineData(ProxyEnrollmentConflict.DuplicateAccountId, HttpStatusCode.Conflict, "duplicate_account")]
    [InlineData(ProxyEnrollmentConflict.TenantMismatch, HttpStatusCode.UnprocessableEntity, "tenant_mismatch")]
    [InlineData(ProxyEnrollmentConflict.BackendCredentialUnavailable, HttpStatusCode.NotFound, "credential_unavailable")]
    public async Task Every_typed_conflict_maps_to_its_documented_status(
        ProxyEnrollmentConflict conflict,
        HttpStatusCode expected,
        string expectedCode)
    {
        using var host = ProxyHost().Override(services =>
        {
            services.RemoveAll<IProxyEnrollmentService>();
            services.AddSingleton<IProxyEnrollmentService>(new ThrowingEnrollmentService(conflict));
        });

        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        var response = await client.PostAsJsonAsync(
            "/v1/proxy/accounts",
            AccountBody("alice@example.com", "backend-1"));

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(expectedCode, await ErrorCodeAsync(response));

        // The Host's existing error body, not a second shape invented for this surface.
        var raw = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(raw);
        Assert.True(document.RootElement.TryGetProperty("detail", out _));
    }

    [Fact]
    public async Task A_missing_request_field_is_a_bad_request()
    {
        using var host = ProxyHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var response = await client.PostAsJsonAsync(
            "/v1/proxy/accounts",
            new { login = "alice@example.com" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("client_password_required", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Account_lifecycle_answers_not_found_for_a_missing_target()
    {
        using var host = ProxyHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsync("/v1/proxy/accounts/pa_missing/disable", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsync("/v1/proxy/accounts/pa_missing/enable", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsync("/v1/proxy/accounts/pa_missing/credential/revoke", null)).StatusCode);
    }

    [Fact]
    public async Task Disable_enable_and_revoke_are_reflected_in_the_readback()
    {
        using var host = ProxyHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var created = await EnrolAsync(client, "alice@example.com", "backend-1");

        var disabled = await ReadAsync<ProxyAccountResponse>(
            await client.PostAsync($"/v1/proxy/accounts/{created.AccountId}/disable", null));
        Assert.False(disabled.Enabled);
        Assert.Equal("active", disabled.EnrollmentState);

        var enabled = await ReadAsync<ProxyAccountResponse>(
            await client.PostAsync($"/v1/proxy/accounts/{created.AccountId}/enable", null));
        Assert.True(enabled.Enabled);

        var revoked = await ReadAsync<ProxyAccountResponse>(
            await client.PostAsync($"/v1/proxy/accounts/{created.AccountId}/credential/revoke", null));
        Assert.Equal("revoked", revoked.EnrollmentState);
        Assert.NotNull(revoked.RevokedAt);

        // Disabling an account is an operator state, never an inference about backend health.
        Assert.True(revoked.Enabled);
    }

    // ---------------------------------------------------------------------------------------------
    // Pagination
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Account_paging_is_stable_and_the_cursor_is_bound_to_tenant_and_page_size()
    {
        using var host = ProxyHost();
        using var client = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        await EnrolAsync(client, "a@example.com", "backend-a");
        await EnrolAsync(client, "b@example.com", "backend-b");
        await EnrolAsync(client, "c@example.com", "backend-c");

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var first = await ReadAsync<ProxyAccountsResponse>(
            await reviewer.GetAsync("/v1/proxy/accounts?limit=2"));

        Assert.Equal(2, first.Accounts.Count);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextCursor);

        var second = await ReadAsync<ProxyAccountsResponse>(
            await reviewer.GetAsync($"/v1/proxy/accounts?limit=2&after={first.NextCursor}"));

        Assert.Single(second.Accounts);
        Assert.False(second.HasMore);
        Assert.Null(second.NextCursor);
        Assert.True(string.CompareOrdinal(second.Accounts[0].AccountId, first.Accounts[^1].AccountId) > 0);

        // A cursor is opaque, but it is bound: another tenant cannot replay it, and neither can the
        // same tenant with a different page size.
        using var globex = host.ClientAs(TestPrincipals.GlobexReviewerKey);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await globex.GetAsync($"/v1/proxy/accounts?limit=2&after={first.NextCursor}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await reviewer.GetAsync($"/v1/proxy/accounts?limit=1&after={first.NextCursor}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await reviewer.GetAsync("/v1/proxy/accounts?limit=0")).StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Availability: not produced is not zero
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Not_produced_is_distinct_from_a_readable_zero()
    {
        using var host = ProxyHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        // No listener event has ever been recorded for this tenant.
        var before = await ReadAsync<ProxySessionsResponse>(
            await reviewer.GetAsync("/v1/proxy/sessions?scope=live"));
        Assert.Equal("not_produced", before.Availability);
        Assert.Empty(before.Sessions);

        var operationsBefore = await ReadAsync<ProxyOperationsResponse>(
            await reviewer.GetAsync("/v1/proxy/operations?window=last_24_hours"));
        Assert.Equal("not_produced", operationsBefore.Availability);
        Assert.Null(operationsBefore.Counters.LiveSessions);
        Assert.Null(operationsBefore.Counters.Opened);

        // One listener lifecycle event makes the stream readable. Empty now means zero, not unknown.
        var sink = host.Services.GetRequiredService<IProxyListenerEventSink>();
        await sink.PublishAsync(
            new ProxyListenerEvent
            {
                Kind = ProxyListenerEventKind.Started,
                TenantId = TestPrincipals.AcmeTenant,
                Protocol = ProxyListenerProtocol.Imap,
                Timestamp = DateTimeOffset.UtcNow,
                BoundPort = 143,
            },
            CancellationToken.None);

        var after = await ReadAsync<ProxySessionsResponse>(
            await reviewer.GetAsync("/v1/proxy/sessions?scope=live"));
        Assert.Equal("available", after.Availability);
        Assert.Empty(after.Sessions);
        Assert.False(after.HasMore);

        var operationsAfter = await ReadAsync<ProxyOperationsResponse>(
            await reviewer.GetAsync("/v1/proxy/operations?window=last_24_hours"));
        Assert.Equal("available", operationsAfter.Availability);
        Assert.Equal(0L, operationsAfter.Counters.LiveSessions);
        Assert.Equal(0L, operationsAfter.Counters.Opened);
        Assert.Equal(0L, operationsAfter.Counters.Closed);
        Assert.Equal(0L, operationsAfter.Counters.Faulted);
        Assert.Equal("proxy_listener_events", operationsAfter.Provenance);
    }

    [Fact]
    public async Task Only_the_named_window_and_the_live_scope_are_accepted()
    {
        using var host = ProxyHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await reviewer.GetAsync("/v1/proxy/operations?window=last_7_days")).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await reviewer.GetAsync("/v1/proxy/operations")).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await reviewer.GetAsync("/v1/proxy/conversations?window=current")).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await reviewer.GetAsync("/v1/proxy/sessions?scope=recent")).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await reviewer.GetAsync("/v1/proxy/sessions")).StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Conversations and thread-token shape
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Thread_tokens_must_be_unpadded_base64url()
    {
        using var host = ProxyHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        // Rejected shapes: a dot, padding and a standard-base64 '+'.
        foreach (var rejected in new[] { "abc.def", "abc=", "abc+def" })
        {
            var response = await reviewer.GetAsync(
                $"/v1/proxy/conversations/{Uri.EscapeDataString(rejected)}/signals?window=last_24_hours");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_thread_token", await ErrorCodeAsync(response));
        }

        // An accepted shape that no conversation uses is a 404, which proves it passed the shape
        // check: a rejected shape would be a 400.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await reviewer.GetAsync("/v1/proxy/conversations/AAAA/signals?window=last_24_hours")).StatusCode);

        var sink = host.Services.GetRequiredService<IProxyThreadingEventSink>();
        await sink.PublishAsync(
            new ProxyThreadingEvent
            {
                TenantId = TestPrincipals.AcmeTenant,
                AccountId = "pa_account",
                Protocol = ProxyListenerProtocol.Imap,
                MessageToken = "message-one",
                ThreadToken = "abc-DEF_123",
                ReferenceDepth = 2,
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5),
            },
            CancellationToken.None);

        var accepted = await reviewer.GetAsync(
            "/v1/proxy/conversations/abc-DEF_123/signals?window=last_24_hours");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task A_missing_measurement_is_an_explicit_not_produced_point()
    {
        using var host = ProxyHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var sink = host.Services.GetRequiredService<IProxyThreadingEventSink>();
        await sink.PublishAsync(
            new ProxyThreadingEvent
            {
                TenantId = TestPrincipals.AcmeTenant,
                AccountId = "pa_account",
                Protocol = ProxyListenerProtocol.Imap,
                MessageToken = "message-one",
                ThreadToken = "thread-one",
                ReferenceDepth = 1,
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5),
            },
            CancellationToken.None);

        var signals = await ReadAsync<ProxyConversationSignalsResponse>(
            await reviewer.GetAsync("/v1/proxy/conversations/thread-one/signals?window=last_24_hours"));

        var point = Assert.Single(signals.Points);
        Assert.Equal("not_produced", point.Availability);
        Assert.Equal("NoAnalyserConfigured", point.AvailabilityReason);
        Assert.Null(point.ToneScore);
        Assert.Equal("message-one", point.TurnToken);
        Assert.Empty(point.WarningMarkers);
        Assert.Equal(0, point.MarkerCount);
        Assert.Equal("proxy_threading_and_assessment", signals.Provenance);
    }

    [Fact]
    public async Task A_clean_measured_point_and_an_unmeasured_point_are_distinguishable()
    {
        using var host = ProxyHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var threading = host.Services.GetRequiredService<IProxyThreadingEventSink>();
        var warnings = host.Services.GetRequiredService<IProxyWarningEventSink>();

        var now = DateTimeOffset.UtcNow;
        foreach (var (message, minutes) in new[]
                 {
                     ("m-clean", 4), ("m-warn", 3), ("m-unmeasured", 2), ("m-no-outcome", 1),
                 })
        {
            await threading.PublishAsync(
                new ProxyThreadingEvent
                {
                    TenantId = TestPrincipals.AcmeTenant,
                    AccountId = "pa_account",
                    Protocol = ProxyListenerProtocol.Imap,
                    MessageToken = message,
                    ThreadToken = "thread-warnings",
                    ReferenceDepth = 1,
                    Timestamp = now.AddMinutes(-minutes),
                },
                CancellationToken.None);
        }

        // A measured clean message: the analysis ran and found nothing.
        await warnings.RecordOutcomeAsync(
            Outcome("m-clean", ProxyWarningAvailability.Available,
                ProxyWarningAvailabilityReason.None, markerCount: 0, now.AddMinutes(-4)),
            CancellationToken.None);

        // A measured message with one derived marker.
        await warnings.RecordOutcomeAsync(
            Outcome("m-warn", ProxyWarningAvailability.Available,
                ProxyWarningAvailabilityReason.None, markerCount: 1, now.AddMinutes(-3)),
            CancellationToken.None);
        await warnings.RecordMarkerAsync(
            new ProxyDeterministicWarningEvent
            {
                TenantId = TestPrincipals.AcmeTenant,
                AccountId = "pa_account",
                TrafficKind = MessageTrafficKind.Retrieval,
                MessageToken = "m-warn",                ThreadToken = "thread-warnings",
                MarkerKind = ProxyWarningMarkerKind.DisplayNameMismatch,
                FindingId = "finding-1",
                ObservedAt = now.AddMinutes(-3),
                Availability = ProxyWarningAvailability.Available,
                AvailabilityReason = ProxyWarningAvailabilityReason.None,
            },
            CancellationToken.None);

        // The analysis was attempted but the message could not be read.
        await warnings.RecordOutcomeAsync(
            Outcome("m-unmeasured", ProxyWarningAvailability.NotProduced,
                ProxyWarningAvailabilityReason.UnreadableMessage, markerCount: 0, now.AddMinutes(-2)),
            CancellationToken.None);

        // m-no-outcome deliberately has no outcome row at all: no analysis was produced.

        var signals = await ReadAsync<ProxyConversationSignalsResponse>(
            await reviewer.GetAsync(
                "/v1/proxy/conversations/thread-warnings/signals?window=last_24_hours"));

        Assert.Equal(4, signals.Points.Count);

        var clean = Assert.Single(signals.Points, point => point.TurnToken == "m-clean");
        Assert.Equal("available", clean.Availability);
        Assert.Equal("None", clean.AvailabilityReason);
        Assert.Equal(0, clean.MarkerCount);
        Assert.Empty(clean.WarningMarkers);

        var warned = Assert.Single(signals.Points, point => point.TurnToken == "m-warn");
        Assert.Equal("available", warned.Availability);
        Assert.Equal(1, warned.MarkerCount);
        Assert.Equal(ExpectedWarningMarkers, warned.WarningMarkers);
        Assert.Equal(ExpectedFindingIds, warned.DimensionIds);

        var unmeasured = Assert.Single(signals.Points, point => point.TurnToken == "m-unmeasured");
        Assert.Equal("not_produced", unmeasured.Availability);
        Assert.Equal("UnreadableMessage", unmeasured.AvailabilityReason);
        Assert.Equal(0, unmeasured.MarkerCount);

        var absent = Assert.Single(signals.Points, point => point.TurnToken == "m-no-outcome");
        Assert.Equal("not_produced", absent.Availability);
        Assert.Equal("NoAnalyserConfigured", absent.AvailabilityReason);

        // Tone is a separate, unproduced measurement on every point.
        Assert.All(signals.Points, point => Assert.Null(point.ToneScore));
    }

    [Fact]
    public async Task A_submission_outcome_without_a_threading_event_still_appears_as_a_measured_point()
    {
        using var host = ProxyHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var warnings = host.Services.GetRequiredService<IProxyWarningEventSink>();
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);

        await warnings.RecordOutcomeAsync(
            new ProxyWarningAnalysisOutcome
            {
                TenantId = TestPrincipals.AcmeTenant,
                AccountId = "pa_account",
                TrafficKind = MessageTrafficKind.Submission,
                MessageToken = "m-submission",
                ThreadToken = "thread-submissionA",
                ObservedAt = at,
                Availability = ProxyWarningAvailability.Available,
                AvailabilityReason = ProxyWarningAvailabilityReason.None,
                MarkerCount = 1,
            },
            CancellationToken.None);

        await warnings.RecordMarkerAsync(
            new ProxyDeterministicWarningEvent
            {
                TenantId = TestPrincipals.AcmeTenant,
                AccountId = "pa_account",
                TrafficKind = MessageTrafficKind.Submission,
                MessageToken = "m-submission",
                ThreadToken = "thread-submissionA",
                MarkerKind = ProxyWarningMarkerKind.AuthenticationFailed,
                FindingId = "deterministic.trusted_authentication_failure:0",
                ObservedAt = at,
                Availability = ProxyWarningAvailability.Available,
                AvailabilityReason = ProxyWarningAvailabilityReason.None,
            },
            CancellationToken.None);

        var conversations = await ReadAsync<ProxyConversationsResponse>(
            await reviewer.GetAsync("/v1/proxy/conversations?window=last_24_hours"));
        var conversation = Assert.Single(conversations.Conversations);
        Assert.Equal("thread-submissionA", conversation.ThreadToken);
        // Depth was not recorded by the warning outcome, so it is unknown rather than 1.
        Assert.Null(conversation.ReferenceDepth);
        Assert.Equal("available", conversation.Availability);

        var signals = await ReadAsync<ProxyConversationSignalsResponse>(
            await reviewer.GetAsync("/v1/proxy/conversations/thread-submissionA/signals?window=last_24_hours"));
        var point = Assert.Single(signals.Points);
        Assert.Equal("m-submission", point.TurnToken);
        Assert.Equal("available", point.Availability);
        Assert.Equal(1, point.MarkerCount);
        Assert.Equal(ExpectedWarningMarkersForSubmission, point.WarningMarkers);
    }

    private static ProxyWarningAnalysisOutcome Outcome(
        string messageToken,
        ProxyWarningAvailability availability,
        ProxyWarningAvailabilityReason reason,
        int markerCount,
        DateTimeOffset observedAt)
        => new()
        {
            TenantId = TestPrincipals.AcmeTenant,
            AccountId = "pa_account",
            TrafficKind = MessageTrafficKind.Retrieval,
            MessageToken = messageToken,
            ThreadToken = "thread-warnings",
            ObservedAt = observedAt,
            Availability = availability,
            AvailabilityReason = reason,
            MarkerCount = markerCount,
        };

    [Fact]
    public async Task Conversations_report_not_produced_without_telemetry_and_zero_with_it()
    {
        using var host = ProxyHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var before = await ReadAsync<ProxyConversationsResponse>(
            await reviewer.GetAsync("/v1/proxy/conversations?window=last_24_hours"));
        Assert.Equal("not_produced", before.Availability);
        Assert.Empty(before.Conversations);

        var sink = host.Services.GetRequiredService<IProxyThreadingEventSink>();
        await sink.PublishAsync(
            new ProxyThreadingEvent
            {
                TenantId = TestPrincipals.AcmeTenant,
                AccountId = "pa_account",
                Protocol = ProxyListenerProtocol.Imap,
                MessageToken = "message-one",
                ThreadToken = "thread-one",
                ReferenceDepth = 3,
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5),
            },
            CancellationToken.None);

        var after = await ReadAsync<ProxyConversationsResponse>(
            await reviewer.GetAsync("/v1/proxy/conversations?window=last_24_hours"));

        Assert.Equal("available", after.Availability);
        var conversation = Assert.Single(after.Conversations);
        Assert.Equal("thread-one", conversation.ThreadToken);
        Assert.Equal(3, conversation.ReferenceDepth);
        Assert.Equal(ExpectedParticipantTokens, conversation.ParticipantTokens);
    }

    // ---------------------------------------------------------------------------------------------
    // Listeners
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Listener_disable_is_drain_aware_and_the_readback_shows_the_drain()
    {
        var factory = new FakeListenerHandleFactory();
        using var host = HostWithListener(factory);
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        using var operatorClient = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var listing = await ReadAsync<ProxyListenersResponse>(
            await reviewer.GetAsync("/v1/proxy/listeners"));
        var listener = Assert.Single(listing.Listeners);
        Assert.Equal("imap-main", listener.ListenerId);
        Assert.Equal("Imap", listener.Protocol);
        Assert.Equal("LoopbackHarnessPlaintext", listener.TlsPolicy);
        Assert.Equal("stopped", listener.State);
        Assert.False(listener.Enabled);

        var enabled = await ReadAsync<ProxyListenerResponse>(
            await operatorClient.PostAsync("/v1/proxy/listeners/imap-main/enable", null));
        Assert.Equal("started", enabled.State);
        Assert.True(enabled.Enabled);
        Assert.Equal(54123, enabled.BindPort);

        var disableResponse = await operatorClient.PostAsync("/v1/proxy/listeners/imap-main/disable", null);
        Assert.Equal(HttpStatusCode.Accepted, disableResponse.StatusCode);

        var draining = await ReadAsync<ProxyListenerResponse>(disableResponse);
        Assert.Equal("stopping", draining.State);
        Assert.True(draining.Draining);
        Assert.False(draining.Enabled);

        // 202 means accepted and stopping, not that the drain worker has already run. Wait for
        // StopAsync to be entered before asserting that a repeated disable did not start a second.
        await factory.Last!.Started.WaitAsync(TimeSpan.FromSeconds(2));

        // A second disable while draining is idempotent; it must not start a competing drain.
        var secondDisable = await operatorClient.PostAsync("/v1/proxy/listeners/imap-main/disable", null);
        Assert.Equal(HttpStatusCode.Accepted, secondDisable.StatusCode);
        Assert.Equal(1, factory.Last!.StopCount);

        // The drain is still running: the readback must say so rather than pretending the listener
        // is already stopped, and the existing session is deliberately not killed.
        var during = await ReadAsync<ProxyListenersResponse>(
            await reviewer.GetAsync("/v1/proxy/listeners"));
        Assert.Equal("stopping", during.Listeners[0].State);
        Assert.True(during.Listeners[0].Draining);

        factory.Last!.AllowDrain();

        var stopped = await WaitForStoppedAsync(reviewer);
        Assert.Equal("stopped", stopped.State);
        Assert.False(stopped.Draining);
        Assert.False(stopped.Enabled);
        // The completed drain disposed its handle exactly once and left no resources held.
        Assert.Equal(1, factory.Last!.StopCount);
        Assert.Equal(1, factory.Last!.DisposeCount);
    }

    [Fact]
    public async Task Listener_controls_require_administer_and_a_known_listener()
    {
        using var host = HostWithListener(new FakeListenerHandleFactory());
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        using var operatorClient = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await reviewer.PostAsync("/v1/proxy/listeners/imap-main/disable", null)).StatusCode);

        var missing = await operatorClient.PostAsync("/v1/proxy/listeners/not-configured/disable", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("listener_not_found", await ErrorCodeAsync(missing));

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await operatorClient.PostAsync("/v1/proxy/listeners/not-configured/enable", null)).StatusCode);
    }

    [Fact]
    public async Task The_real_listener_factory_binds_and_reports_the_actual_port()
    {
        // No fake handle here: this is the composition path, on a loopback harness listener with an
        // ephemeral port. Enable must bind a real socket through access's published session runner,
        // and the readback must show the port the operating system assigned.
        using var host = HostWithListener(factory: null);
        using var operatorClient = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var enable = await operatorClient.PostAsync("/v1/proxy/listeners/imap-main/enable", null);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);

        var started = await ReadAsync<ProxyListenerResponse>(enable);
        Assert.Equal("started", started.State);
        Assert.True(started.Enabled);
        Assert.True(started.BindPort > 0);
        Assert.False(started.Draining);

        var disable = await operatorClient.PostAsync("/v1/proxy/listeners/imap-main/disable", null);
        Assert.Equal(HttpStatusCode.Accepted, disable.StatusCode);

        var stopped = await WaitForStoppedAsync(reviewer);
        Assert.Equal("stopped", stopped.State);
        Assert.False(stopped.Enabled);
        Assert.False(stopped.Draining);
    }

    [Fact]
    public async Task A_live_session_is_listed_and_uninstrumented_bytes_are_null_not_zero()
    {
        using var host = ProxyHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var sink = host.Services.GetRequiredService<IProxyListenerEventSink>();

        await sink.PublishAsync(
            new ProxyListenerEvent
            {
                Kind = ProxyListenerEventKind.Started,
                TenantId = TestPrincipals.AcmeTenant,
                Protocol = ProxyListenerProtocol.Imap,
                Timestamp = DateTimeOffset.UtcNow,
                BoundPort = 143,
            },
            CancellationToken.None);

        await sink.PublishAsync(
            new ProxySessionEvent
            {
                Kind = ProxySessionEventKind.Opened,
                SessionId = "s-1",
                TenantId = TestPrincipals.AcmeTenant,
                AccountId = "pa_account",
                Protocol = ProxyListenerProtocol.Imap,
                Direction = ProxySessionDirection.ClientToProxy,
                Timestamp = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        var live = await ReadAsync<ProxySessionsResponse>(
            await reviewer.GetAsync("/v1/proxy/sessions?scope=live"));

        var session = Assert.Single(live.Sessions);
        Assert.Equal("Imap", session.Protocol);
        Assert.Equal("pa_account", session.AccountId);

        // No byte counter is wired. A measured zero and an unmeasured session must not look alike.
        Assert.Null(session.ClientBytes);
        Assert.Null(session.BackendBytes);

        await sink.PublishAsync(
            new ProxySessionEvent
            {
                Kind = ProxySessionEventKind.Closed,
                SessionId = "s-1",
                TenantId = TestPrincipals.AcmeTenant,
                AccountId = "pa_account",
                Protocol = ProxyListenerProtocol.Imap,
                Direction = ProxySessionDirection.ClientToProxy,
                Timestamp = DateTimeOffset.UtcNow,
                Outcome = SessionOutcome.Relayed,
            },
            CancellationToken.None);

        var afterClose = await ReadAsync<ProxySessionsResponse>(
            await reviewer.GetAsync("/v1/proxy/sessions?scope=live"));
        Assert.Empty(afterClose.Sessions);
    }

    [Fact]
    public async Task An_authenticated_live_session_shows_its_account_and_an_unauthenticated_one_does_not()
    {
        using var host = ProxyHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var sink = host.Services.GetRequiredService<IProxyListenerEventSink>();
        var authenticator = host.Services.GetRequiredService<IProxySessionAuthenticator>();

        await sink.PublishAsync(
            new ProxyListenerEvent
            {
                Kind = ProxyListenerEventKind.Started,
                TenantId = TestPrincipals.AcmeTenant,
                Protocol = ProxyListenerProtocol.Imap,
                Timestamp = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        foreach (var sessionId in new[] { "s-anon", "s-auth" })
        {
            await sink.PublishAsync(
                new ProxySessionEvent
                {
                    Kind = ProxySessionEventKind.Opened,
                    SessionId = sessionId,
                    TenantId = TestPrincipals.AcmeTenant,
                    Protocol = ProxyListenerProtocol.Imap,
                    Direction = ProxySessionDirection.ClientToProxy,
                    Timestamp = DateTimeOffset.UtcNow,
                },
                CancellationToken.None);
        }

        var authenticatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
        await authenticator.MarkAuthenticatedAsync(
            TestPrincipals.AcmeTenant, "s-auth", "pa_authenticated", authenticatedAt, CancellationToken.None);

        var live = await ReadAsync<ProxySessionsResponse>(
            await reviewer.GetAsync("/v1/proxy/sessions?scope=live"));

        Assert.Equal(2, live.Sessions.Count);

        var authenticated = Assert.Single(live.Sessions, session => session.SessionId == "s-auth");
        Assert.Equal("pa_authenticated", authenticated.AccountId);
        Assert.NotNull(authenticated.AuthenticatedAt);

        var anonymous = Assert.Single(live.Sessions, session => session.SessionId == "s-anon");
        Assert.Null(anonymous.AccountId);
        Assert.Null(anonymous.AuthenticatedAt);
    }

    [Fact]
    public async Task Element_analysis_readiness_reports_configuration_without_leaking_it()
    {
        using var host = ProxyHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var defaultRaw = await (await reviewer.GetAsync("/v1/proxy/listeners")).Content.ReadAsStringAsync();
        var defaultResponse = JsonSerializer.Deserialize<ProxyListenersResponse>(defaultRaw, HostJson.Options)!;
        Assert.False(defaultResponse.ElementAnalysisConfigured);
        Assert.Null(host.Services.GetRequiredService<ProxyElementConfiguration>().Classifier);

        using var configured = ProxyHost();
        var tokenPath = Path.Combine(configured.Root, "embedding-token");
        File.WriteAllText(tokenPath, "fixture-token-not-a-real-secret");
        configured
            .Configure("StyloMail:Embeddings:Enabled", "true")
            .Configure("StyloMail:Embeddings:Endpoint", "http://127.0.0.1:1")
            .Configure("StyloMail:Embeddings:TokenFilePath", tokenPath);

        using var configuredReviewer = configured.ClientAs(TestPrincipals.AcmeReviewerKey);
        var configuredRaw = await (await configuredReviewer.GetAsync("/v1/proxy/listeners"))
            .Content.ReadAsStringAsync();
        var configuredResponse = JsonSerializer.Deserialize<ProxyListenersResponse>(
            configuredRaw, HostJson.Options)!;

        Assert.True(configuredResponse.ElementAnalysisConfigured);
        Assert.NotNull(configured.Services.GetRequiredService<ProxyElementConfiguration>().Classifier);

        // Readiness names the state, never the address or the token path.
        Assert.DoesNotContain("127.0.0.1:1", configuredRaw);
        Assert.DoesNotContain(tokenPath, configuredRaw);
        Assert.DoesNotContain("fixture-token-not-a-real-secret", configuredRaw);
    }

    [Fact]
    public void Null_element_configuration_composes_nothing_and_writes_nothing()
    {
        using var host = ProxyHost();

        var configuration = host.Services.GetRequiredService<ProxyElementConfiguration>();
        Assert.False(configuration.Enabled);
        Assert.Null(configuration.Classifier);

        var database = host.Services.GetRequiredService<HostDatabase>();
        using var connection = database.Open();
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM proxy_segment_outcome;"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM proxy_element_point;"));
    }

    [Fact]
    public async Task An_unreachable_configured_endpoint_writes_one_not_produced_outcome_and_no_points()
    {
        using var host = ProxyHost();
        var tokenPath = Path.Combine(host.Root, "embedding-token");
        File.WriteAllText(tokenPath, "fixture-token-not-a-real-secret");
        host
            .Configure("StyloMail:Embeddings:Enabled", "true")
            .Configure("StyloMail:Embeddings:Endpoint", "http://127.0.0.1:1")
            .Configure("StyloMail:Embeddings:TokenFilePath", tokenPath);

        var configuration = host.Services.GetRequiredService<ProxyElementConfiguration>();
        Assert.True(configuration.Enabled);
        Assert.NotNull(configuration.Classifier);

        var sink = host.Services.GetRequiredService<IProxyElementSink>();
        var orchestrator = new StyloMail.AccessProxy.Submission.ProxyElementOrchestrator(
            configuration.Classifier!, sink);
        var bytes = "para one\r\n\r\npara two"u8.ToArray();

        await orchestrator.EmitAsync(
            new ProxyWarningAnalysisInput
            {
                Message = bytes,
                TenantId = TestPrincipals.AcmeTenant,
                AccountId = "pa_account",
                ObservedAt = DateTimeOffset.UtcNow,
            },
            bytes,
            "m-unreachable",
            "thread-unreachable",
            MessageTrafficKind.Submission,
            CancellationToken.None);

        var database = host.Services.GetRequiredService<HostDatabase>();
        using var connection = database.Open();

        using (var outcome = connection.CreateCommand())
        {
            outcome.CommandText =
                "SELECT availability,availability_reason,segment_count FROM proxy_segment_outcome "
                + "WHERE tenant_id=$tenant AND message_token='m-unreachable';";
            outcome.Parameters.AddWithValue("$tenant", TestPrincipals.AcmeTenant);
            using var reader = outcome.ExecuteReader();
            Assert.True(reader.Read());
            // A refused connection is service_unavailable, not "unsupported content".
            Assert.Equal("NotProduced", reader.GetString(0));
            Assert.Equal("ServiceUnavailable", reader.GetString(1));
            Assert.True(reader.GetInt32(2) >= 1);
            Assert.False(reader.Read());
        }

        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM proxy_element_point;"));
    }

    private static long Scalar(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task Element_series_projects_scorers_versions_and_per_point_availability()
    {
        using var host = ProxyHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var sink = host.Services.GetRequiredService<IProxyElementSink>();
        var at = DateTimeOffset.UtcNow.AddMinutes(-2);
        const string thread = "thread-elementsABC";

        await sink.RecordOutcomeAsync(
            new ProxySegmentationOutcome
            {
                TenantId = TestPrincipals.AcmeTenant,
                AccountId = "pa_account",
                TrafficKind = MessageTrafficKind.Retrieval,
                MessageToken = "m-element",
                ThreadToken = thread,
                ObservedAt = at,
                Availability = ProxyWarningAvailability.Available,
                AvailabilityReason = ProxySegmentationAvailabilityReason.None,
                SegmentCount = 1,
                Truncated = false,
                AppliedMaxSegments = 32,
                AppliedMaxSegmentBytes = 6000,
                AnalysedOrdinals = [0],
                AnySegmentDropped = false,
            },
            CancellationToken.None);

        await sink.RecordPointAsync(
            Point("credential_request", score: 0.31, exemplar: "seed-1:credential_request:0007",
                availability: ProxyWarningAvailability.Available, reason: "none", at),
            CancellationToken.None);

        // One element can be unavailable while another on the same segment is scored.
        await sink.RecordPointAsync(
            Point("threat", score: null, exemplar: null,
                availability: ProxyWarningAvailability.NotProduced, reason: "no_exemplars", at),
            CancellationToken.None);

        var series = await ReadAsync<ProxyElementSeriesResponse>(
            await reviewer.GetAsync($"/v1/proxy/conversations/{thread}/elements?window=last_24_hours"));

        Assert.Equal("proxy_element_classification", series.Provenance);
        var point = Assert.Single(series.Points);
        Assert.Equal(0, point.Ordinal);
        Assert.Equal("m-element", point.TurnToken);
        Assert.Equal(2, point.Elements.Count);

        var available = Assert.Single(point.Elements, element => element.Element == "credential_request");
        Assert.Equal("prototype_knn", available.Scorer);
        Assert.Equal(ElementScorers.PrototypeKnnVersion, available.ScorerVersion);
        Assert.Equal(ElementVocabulary.Version, available.VocabularyVersion);
        Assert.Equal("google/embeddinggemma-2", available.ModelTag);
        Assert.Equal("classification/1", available.PrefixVersion);
        Assert.Equal(256, available.Dimension);
        Assert.Equal(0.31, available.Score);
        Assert.Equal("seed-1:credential_request:0007", available.NearestExemplarId);
        Assert.Equal("available", available.Availability);
        Assert.Equal("none", available.AvailabilityReason);

        var missing = Assert.Single(point.Elements, element => element.Element == "threat");
        Assert.Null(missing.Score);
        Assert.Null(missing.NearestExemplarId);
        Assert.Equal("not_produced", missing.Availability);
        Assert.Equal("no_exemplars", missing.AvailabilityReason);

        // Tone is a sibling series and its service is not published: not produced, never zeros.
        Assert.Equal("goemotions/1", point.Tone.VocabularyVersion);
        Assert.Null(point.Tone.ModelTag);
        Assert.Equal("not_produced", point.Tone.Availability);
        Assert.Equal("tone_model_unavailable", point.Tone.AvailabilityReason);
        Assert.Empty(point.Tone.Top);
    }

    [Fact]
    public async Task Element_series_serves_both_traffic_kinds_for_one_thread_without_mixing()
    {
        using var host = ProxyHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var sink = host.Services.GetRequiredService<IProxyElementSink>();
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        const string thread = "thread-bothDirectionsX";

        foreach (var kind in new[] { MessageTrafficKind.Retrieval, MessageTrafficKind.Submission })
        {
            await sink.RecordOutcomeAsync(
                new ProxySegmentationOutcome
                {
                    TenantId = TestPrincipals.AcmeTenant,
                    AccountId = "pa_account",
                    TrafficKind = kind,
                    MessageToken = "m-shared",
                    ThreadToken = thread,
                    ObservedAt = at,
                    Availability = ProxyWarningAvailability.Available,
                    AvailabilityReason = ProxySegmentationAvailabilityReason.None,
                    SegmentCount = 1,
                    Truncated = false,
                    AppliedMaxSegments = 32,
                    AppliedMaxSegmentBytes = 6000,
                    AnalysedOrdinals = [0],
                    AnySegmentDropped = false,
                },
                CancellationToken.None);
        }

        await sink.RecordPointAsync(
            new ProxyElementPoint
            {
                TenantId = TestPrincipals.AcmeTenant,
                AccountId = "pa_account",
                TrafficKind = MessageTrafficKind.Retrieval,
                MessageToken = "m-shared",
                ThreadToken = thread,
                Ordinal = 0,
                Element = "credential_request",
                Scorer = ElementScorers.PrototypeKnn,
                ScorerVersion = ElementScorers.PrototypeKnnVersion,
                Score = 0.31,
                NearestExemplarId = "seed-1:credential_request:0007",
                Availability = ProxyWarningAvailability.Available,
                AvailabilityReason = "none",
                ModelTag = "google/embeddinggemma-2",
                PrefixVersion = "classification/1",
                Dimension = 256,
                VocabularyVersion = ElementVocabulary.Version,
                ObservedAt = at,
            },
            CancellationToken.None);

        await sink.RecordPointAsync(
            new ProxyElementPoint
            {
                TenantId = TestPrincipals.AcmeTenant,
                AccountId = "pa_account",
                TrafficKind = MessageTrafficKind.Submission,
                MessageToken = "m-shared",
                ThreadToken = thread,
                Ordinal = 0,
                Element = "threat",
                Scorer = ElementScorers.LinearProbe,
                ScorerVersion = ElementScorers.LinearProbeVersion,
                Score = 0.8,
                NearestExemplarId = null,
                Availability = ProxyWarningAvailability.Available,
                AvailabilityReason = "none",
                ModelTag = "google/embeddinggemma-2",
                PrefixVersion = "classification/1",
                Dimension = 256,
                VocabularyVersion = ElementVocabulary.Version,
                ObservedAt = at,
            },
            CancellationToken.None);

        var series = await ReadAsync<ProxyElementSeriesResponse>(
            await reviewer.GetAsync($"/v1/proxy/conversations/{thread}/elements?window=last_24_hours"));

        // Same message token and ordinal, two directions: two points, never merged.
        Assert.Equal(2, series.Points.Count);

        var retrieval = Assert.Single(series.Points, point => point.TrafficKind == "Retrieval");
        var retrievalElement = Assert.Single(retrieval.Elements);
        Assert.Equal("credential_request", retrievalElement.Element);
        Assert.Equal(ElementScorers.PrototypeKnn, retrievalElement.Scorer);
        Assert.Equal(0.31, retrievalElement.Score);
        Assert.Equal("available", retrievalElement.Availability);

        var submission = Assert.Single(series.Points, point => point.TrafficKind == "Submission");
        var submissionElement = Assert.Single(submission.Elements);
        Assert.Equal("threat", submissionElement.Element);
        Assert.Equal(ElementScorers.LinearProbe, submissionElement.Scorer);
        Assert.Equal(0.8, submissionElement.Score);
        Assert.Equal("available", submissionElement.Availability);
        Assert.Null(submissionElement.NearestExemplarId);
    }

    [Fact]
    public async Task Element_batch_is_atomic_and_replaces_superseded_points()
    {
        using var host = ProxyHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var sink = host.Services.GetRequiredService<IProxyElementSink>();
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        const string thread = "thread-batchPointsA";

        var outcome = BatchOutcome("m-batch", thread, at);
        var credential = BatchPoint("m-batch", thread, "credential_request", 0.31,
            "seed-1:credential_request:0007", at, ElementScorers.PrototypeKnn);
        var threat = BatchPoint("m-batch", thread, "threat", 0.8,
            null, at, ElementScorers.LinearProbe);

        await sink.RecordBatchAsync(outcome, [credential, threat], CancellationToken.None);
        Assert.Equal(2L, Scalar(host, "SELECT COUNT(*) FROM proxy_element_point WHERE message_token='m-batch';"));

        // A replacement batch for the same message supersedes the previous point set atomically.
        await sink.RecordBatchAsync(outcome, [credential], CancellationToken.None);
        Assert.Equal(1L, Scalar(host, "SELECT COUNT(*) FROM proxy_element_point WHERE message_token='m-batch';"));
        Assert.Equal(1L, Scalar(host, "SELECT COUNT(*) FROM proxy_segment_outcome WHERE message_token='m-batch';"));

        // A contract break anywhere in the batch writes nothing and leaves the prior set intact.
        var broken = new ProxyElementPoint
        {
            TenantId = TestPrincipals.AcmeTenant,
            AccountId = "pa_account",
            TrafficKind = MessageTrafficKind.Retrieval,
            MessageToken = "m-batch",
            ThreadToken = thread,
            Ordinal = 1,
            Element = "threat",
            Scorer = ElementScorers.PrototypeKnn,
            ScorerVersion = ElementScorers.PrototypeKnnVersion,
            Score = null,
            NearestExemplarId = "seed-1:threat:0001",
            Availability = ProxyWarningAvailability.Available,
            AvailabilityReason = "none",
            ModelTag = "google/embeddinggemma-2",
            PrefixVersion = "classification/1",
            Dimension = 256,
            VocabularyVersion = ElementVocabulary.Version,
            ObservedAt = at,
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => sink.RecordBatchAsync(outcome, [credential, broken], CancellationToken.None).AsTask());

        Assert.Equal(1L, Scalar(host, "SELECT COUNT(*) FROM proxy_element_point WHERE message_token='m-batch';"));
        Assert.Equal(1L, Scalar(host, "SELECT COUNT(*) FROM proxy_segment_outcome WHERE message_token='m-batch';"));

        var series = await ReadAsync<ProxyElementSeriesResponse>(
            await reviewer.GetAsync($"/v1/proxy/conversations/{thread}/elements?window=last_24_hours"));
        var point = Assert.Single(series.Points);
        var element = Assert.Single(point.Elements);
        Assert.Equal("credential_request", element.Element);
    }

    [Fact]
    public async Task Historical_elements1_points_stay_readable_without_mixing_versions()
    {
        using var host = ProxyHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var sink = host.Services.GetRequiredService<IProxyElementSink>();
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        const string thread = "thread-historicalA";

        await sink.RecordOutcomeAsync(BatchOutcome("m-historical", thread, at), CancellationToken.None);

        // The write path now refuses a non-current vocabulary, so a legacy row is seeded directly.
        // This is exactly the shape an elements/1 ledger has on disk today.
        InsertLegacyElementPoint(host, "m-historical", thread, at, "attachment_lure", "elements/1");

        // A current-version point at the SAME ordinal and message. The response must keep them as
        // two points rather than merging elements from different vocabularies.
        await sink.RecordPointAsync(
            new ProxyElementPoint
            {
                TenantId = TestPrincipals.AcmeTenant,
                AccountId = "pa_account",
                TrafficKind = MessageTrafficKind.Retrieval,
                MessageToken = "m-historical",
                ThreadToken = thread,
                Ordinal = 0,
                Element = ElementVocabulary.CredentialRequest,
                Scorer = ElementScorers.LinearProbe,
                ScorerVersion = ElementScorers.LinearProbeVersion,
                Score = 0.7,
                NearestExemplarId = null,
                Availability = ProxyWarningAvailability.Available,
                AvailabilityReason = "none",
                ModelTag = "google/embeddinggemma-2",
                PrefixVersion = "classification/1",
                Dimension = 256,
                VocabularyVersion = ElementVocabulary.Version,
                ObservedAt = at,
            },
            CancellationToken.None);

        var series = await ReadAsync<ProxyElementSeriesResponse>(
            await reviewer.GetAsync($"/v1/proxy/conversations/{thread}/elements?window=last_24_hours"));

        Assert.Equal(2, series.Points.Count);

        var historical = Assert.Single(series.Points, point => point.VocabularyVersion == "elements/1");
        Assert.Equal(0, historical.Ordinal);
        var oldElement = Assert.Single(historical.Elements);
        Assert.Equal("attachment_lure", oldElement.Element);
        Assert.Equal("elements/1", oldElement.VocabularyVersion);

        var current = Assert.Single(
            series.Points, point => point.VocabularyVersion == ElementVocabulary.Version);
        Assert.Equal(0, current.Ordinal);
        var currentElement = Assert.Single(current.Elements);
        Assert.Equal(ElementVocabulary.CredentialRequest, currentElement.Element);
        Assert.Equal(ElementVocabulary.Version, currentElement.VocabularyVersion);

        // New writes must declare the current version.
        var legacyWrite = new ProxyElementPoint
        {
            TenantId = TestPrincipals.AcmeTenant,
            AccountId = "pa_account",
            TrafficKind = MessageTrafficKind.Retrieval,
            MessageToken = "m-historical",
            ThreadToken = thread,
            Ordinal = 1,
            Element = ElementVocabulary.CredentialRequest,
            Scorer = ElementScorers.PrototypeKnn,
            ScorerVersion = ElementScorers.PrototypeKnnVersion,
            Score = 0.2,
            NearestExemplarId = "seed-1:credential_request:0007",
            Availability = ProxyWarningAvailability.Available,
            AvailabilityReason = "none",
            ModelTag = "google/embeddinggemma-2",
            PrefixVersion = "classification/1",
            Dimension = 256,
            VocabularyVersion = "elements/1",
            ObservedAt = at,
        };
        var versionError = await Assert.ThrowsAsync<ArgumentException>(
            () => sink.RecordPointAsync(legacyWrite, CancellationToken.None).AsTask());
        Assert.Contains("elements/1", versionError.Message);

        // A removed element under the current version is refused and named, so a log can tell a
        // stale producer from a corrupted row.
        var removedWrite = new ProxyElementPoint
        {
            TenantId = TestPrincipals.AcmeTenant,
            AccountId = "pa_account",
            TrafficKind = MessageTrafficKind.Retrieval,
            MessageToken = "m-historical",
            ThreadToken = thread,
            Ordinal = 1,
            Element = "attachment_lure",
            Scorer = ElementScorers.PrototypeKnn,
            ScorerVersion = ElementScorers.PrototypeKnnVersion,
            Score = 0.2,
            NearestExemplarId = "seed-1:attachment_lure:0003",
            Availability = ProxyWarningAvailability.Available,
            AvailabilityReason = "none",
            ModelTag = "google/embeddinggemma-2",
            PrefixVersion = "classification/1",
            Dimension = 256,
            VocabularyVersion = ElementVocabulary.Version,
            ObservedAt = at,
        };
        var elementError = await Assert.ThrowsAsync<ArgumentException>(
            () => sink.RecordPointAsync(removedWrite, CancellationToken.None).AsTask());
        Assert.Contains("attachment_lure", elementError.Message);
    }

    private static void InsertLegacyElementPoint(
        TestHost host,
        string message,
        string thread,
        DateTimeOffset at,
        string element,
        string vocabularyVersion)
    {
        var database = host.Services.GetRequiredService<HostDatabase>();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO proxy_element_point "
            + "(tenant_id,message_token,direction,ordinal,element,scorer,thread_token,account_id,"
            + "observed_at,score,nearest_exemplar_id,availability,availability_reason,model_tag,"
            + "prefix_version,dimension,vocabulary_version,scorer_version) "
            + "VALUES ($tenant,$message,'Retrieval',0,$element,$scorer,$thread,$account,$at,0.4,"
            + "$exemplar,'available','none',$model,$prefix,256,$vocabulary,$scorerVersion);";
        command.Parameters.AddWithValue("$tenant", TestPrincipals.AcmeTenant);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$element", element);
        command.Parameters.AddWithValue("$scorer", ElementScorers.PrototypeKnn);
        command.Parameters.AddWithValue("$thread", thread);
        command.Parameters.AddWithValue("$account", "pa_account");
        command.Parameters.AddWithValue("$at", at.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$exemplar", "seed-1:attachment_lure:0003");
        command.Parameters.AddWithValue("$model", "google/embeddinggemma-2");
        command.Parameters.AddWithValue("$prefix", "classification/1");
        command.Parameters.AddWithValue("$vocabulary", vocabularyVersion);
        command.Parameters.AddWithValue("$scorerVersion", ElementScorers.PrototypeKnnVersion);
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task Element_reason_vocabulary_accepts_insufficient_exemplars_and_refuses_unknown()
    {
        using var host = ProxyHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var sink = host.Services.GetRequiredService<IProxyElementSink>();
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        const string thread = "thread-reasonsABCD";

        await sink.RecordOutcomeAsync(BatchOutcome("m-reason", thread, at), CancellationToken.None);

        await sink.RecordPointAsync(
            NotProducedElement("m-reason", thread, 0, "insufficient_exemplars", at),
            CancellationToken.None);

        var series = await ReadAsync<ProxyElementSeriesResponse>(
            await reviewer.GetAsync($"/v1/proxy/conversations/{thread}/elements?window=last_24_hours"));
        var point = Assert.Single(series.Points);
        var element = Assert.Single(point.Elements);
        Assert.Equal("not_produced", element.Availability);
        Assert.Equal("insufficient_exemplars", element.AvailabilityReason);
        Assert.Null(element.Score);

        // An unknown reason is refused rather than stored as an opaque string.
        var unknown = await Assert.ThrowsAsync<ArgumentException>(() => sink.RecordPointAsync(
            NotProducedElement("m-reason", thread, 1, "made_up_reason", at),
            CancellationToken.None).AsTask());
        Assert.Contains("made_up_reason", unknown.Message);

        // An available point cannot carry a not-produced reason.
        var badAvailable = NotProducedElement("m-reason", thread, 2, "no_exemplars", at);
        badAvailable = badAvailable with
        {
            Availability = ProxyWarningAvailability.Available,
            Score = 0.5,
            NearestExemplarId = "seed-1:threat:0001",
        };
        await Assert.ThrowsAsync<ArgumentException>(
            () => sink.RecordPointAsync(badAvailable, CancellationToken.None).AsTask());
    }

    private static ProxyElementPoint NotProducedElement(
        string message,
        string thread,
        int ordinal,
        string reason,
        DateTimeOffset at)
        => new()
        {
            TenantId = TestPrincipals.AcmeTenant,
            AccountId = "pa_account",
            TrafficKind = MessageTrafficKind.Retrieval,
            MessageToken = message,
            ThreadToken = thread,
            Ordinal = ordinal,
            Element = ElementVocabulary.Threat,
            Scorer = ElementScorers.PrototypeKnn,
            ScorerVersion = ElementScorers.PrototypeKnnVersion,
            Score = null,
            NearestExemplarId = null,
            Availability = ProxyWarningAvailability.NotProduced,
            AvailabilityReason = reason,
            ModelTag = "google/embeddinggemma-2",
            PrefixVersion = "classification/1",
            Dimension = 256,
            VocabularyVersion = ElementVocabulary.Version,
            ObservedAt = at,
        };

    private static ProxySegmentationOutcome BatchOutcome(
        string message,
        string thread,
        DateTimeOffset at)
        => new()
        {
            TenantId = TestPrincipals.AcmeTenant,
            AccountId = "pa_account",
            TrafficKind = MessageTrafficKind.Retrieval,
            MessageToken = message,
            ThreadToken = thread,
            ObservedAt = at,
            Availability = ProxyWarningAvailability.Available,
            AvailabilityReason = ProxySegmentationAvailabilityReason.None,
            SegmentCount = 2,
            Truncated = false,
            AppliedMaxSegments = 32,
            AppliedMaxSegmentBytes = 6000,
            AnalysedOrdinals = [0, 1],
            AnySegmentDropped = false,
        };

    private static ProxyElementPoint BatchPoint(
        string message,
        string thread,
        string element,
        double? score,
        string? exemplar,
        DateTimeOffset at,
        string scorer)
        => new()
        {
            TenantId = TestPrincipals.AcmeTenant,
            AccountId = "pa_account",
            TrafficKind = MessageTrafficKind.Retrieval,
            MessageToken = message,
            ThreadToken = thread,
            Ordinal = 0,
            Element = element,
            Scorer = scorer,
            ScorerVersion = ElementScorers.VersionOf(scorer)!,
            Score = score,
            NearestExemplarId = exemplar,
            Availability = ProxyWarningAvailability.Available,
            AvailabilityReason = "none",
            ModelTag = "google/embeddinggemma-2",
            PrefixVersion = "classification/1",
            Dimension = 256,
            VocabularyVersion = ElementVocabulary.Version,
            ObservedAt = at,
        };

    private static long Scalar(TestHost host, string sql)
    {
        var database = host.Services.GetRequiredService<HostDatabase>();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task An_element_point_with_a_contract_break_is_refused_not_stored()
    {
        using var host = ProxyHost();
        var sink = host.Services.GetRequiredService<IProxyElementSink>();
        var at = DateTimeOffset.UtcNow;

        // Available with no score is a contract break; the whole point is refused.
        await Assert.ThrowsAsync<ArgumentException>(() => sink.RecordPointAsync(
            Point("credential_request", score: null, exemplar: "seed-1:credential_request:0007",
                availability: ProxyWarningAvailability.Available, reason: "none", at),
            CancellationToken.None).AsTask());

        // A probe point carrying a kNN exemplar is also a break.
        await Assert.ThrowsAsync<ArgumentException>(() => sink.RecordPointAsync(
            new ProxyElementPoint
            {
                TenantId = TestPrincipals.AcmeTenant,
                AccountId = "pa_account",
                TrafficKind = MessageTrafficKind.Retrieval,
                MessageToken = "m-element",
                ThreadToken = "thread-elementsABC",
                Ordinal = 0,
                Element = "threat",
                Scorer = ElementScorers.LinearProbe,
                ScorerVersion = ElementScorers.LinearProbeVersion,
                Score = 0.5,
                NearestExemplarId = "seed-1:threat:0001",
                Availability = ProxyWarningAvailability.Available,
                AvailabilityReason = "none",
                ModelTag = "google/embeddinggemma-2",
                PrefixVersion = "classification/1",
                Dimension = 256,
                VocabularyVersion = ElementVocabulary.Version,
                ObservedAt = at,
            },
            CancellationToken.None).AsTask());
    }

    private static ProxyElementPoint Point(
        string element,
        double? score,
        string? exemplar,
        ProxyWarningAvailability availability,
        string reason,
        DateTimeOffset at)
        => new()
        {
            TenantId = TestPrincipals.AcmeTenant,
            AccountId = "pa_account",
            TrafficKind = MessageTrafficKind.Retrieval,
            MessageToken = "m-element",
            ThreadToken = "thread-elementsABC",
            Ordinal = 0,
            Element = element,
            Scorer = ElementScorers.PrototypeKnn,
            ScorerVersion = ElementScorers.PrototypeKnnVersion,
            Score = score,
            NearestExemplarId = exemplar,
            Availability = availability,
            AvailabilityReason = reason,
            ModelTag = "google/embeddinggemma-2",
            PrefixVersion = "classification/1",
            Dimension = 256,
            VocabularyVersion = ElementVocabulary.Version,
            ObservedAt = at,
        };

    [Fact]
    public async Task An_smtp_listener_binds_once_the_submission_gate_is_composed()
    {
        using var host = ProxyHost();
        var certificate = CreateCertificate(host.Root);
        host.Configure("StyloMail:Proxy:Listeners:0:ListenerId", "smtp-main")
            .Configure("StyloMail:Proxy:Listeners:0:TenantId", TestPrincipals.AcmeTenant)
            .Configure("StyloMail:Proxy:Listeners:0:Protocol", "Smtp")
            .Configure("StyloMail:Proxy:Listeners:0:BindAddress", "127.0.0.1")
            .Configure("StyloMail:Proxy:Listeners:0:Port", "0")
            .Configure("StyloMail:Proxy:Listeners:0:TlsPolicy", "Required")
            .Configure("StyloMail:Proxy:Listeners:0:CertificateReference", certificate)
            .Configure("StyloMail:Proxy:Listeners:0:Enabled", "false");

        using var operatorClient = host.ClientAs(TestPrincipals.AcmeOperatorKey);

        var enable = await operatorClient.PostAsync("/v1/proxy/listeners/smtp-main/enable", null);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);

        var started = await ReadAsync<ProxyListenerResponse>(enable);
        Assert.Equal("Smtp", started.Protocol);
        Assert.Equal("Required", started.TlsPolicy);
        Assert.Equal("started", started.State);
        Assert.True(started.BindPort > 0);

        await operatorClient.PostAsync("/v1/proxy/listeners/smtp-main/disable", null);
    }

    [Fact]
    public async Task A_tls_required_retrieval_listener_never_speaks_plaintext()
    {
        using var host = ProxyHost();
        var certificate = CreateCertificate(host.Root);
        host
            .Configure("StyloMail:Proxy:Listeners:0:ListenerId", "imap-tls")
            .Configure("StyloMail:Proxy:Listeners:0:TenantId", TestPrincipals.AcmeTenant)
            .Configure("StyloMail:Proxy:Listeners:0:Protocol", "Imap")
            .Configure("StyloMail:Proxy:Listeners:0:BindAddress", "127.0.0.1")
            .Configure("StyloMail:Proxy:Listeners:0:Port", "0")
            .Configure("StyloMail:Proxy:Listeners:0:TlsPolicy", "Required")
            .Configure("StyloMail:Proxy:Listeners:0:CertificateReference", certificate)
            .Configure("StyloMail:Proxy:Listeners:0:Enabled", "false");

        using var operatorClient = host.ClientAs(TestPrincipals.AcmeOperatorKey);
        var started = await ReadAsync<ProxyListenerResponse>(
            await operatorClient.PostAsync("/v1/proxy/listeners/imap-tls/enable", null));
        Assert.Equal("started", started.State);

        // 1. No plaintext greeting before a ClientHello. The listener is waiting for TLS.
        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, started.BindPort);
            var stream = raw.GetStream();
            var buffer = new byte[128];
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
            var read = await ReadOrZeroAsync(stream, buffer, cts.Token);
            Assert.Equal(0, read);
        }

        // 2. A plaintext LOGIN is not answered in plaintext; the handshake fails instead.
        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, started.BindPort);
            var stream = raw.GetStream();
            await stream.WriteAsync("a1 LOGIN alice secret\r\n"u8.ToArray());
            var buffer = new byte[256];
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
            var read = await ReadOrZeroAsync(stream, buffer, cts.Token);
            var text = read > 0 ? Encoding.ASCII.GetString(buffer, 0, read) : string.Empty;
            Assert.DoesNotContain("OK", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("LOGIN", text, StringComparison.OrdinalIgnoreCase);
        }

        // 3. The greeting is available only after an eager TLS handshake.
        using (var tlsClient = new TcpClient())
        {
            await tlsClient.ConnectAsync(IPAddress.Loopback, started.BindPort);
            await using var ssl = new SslStream(
                tlsClient.GetStream(),
                leaveInnerStreamOpen: false,
                (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync("localhost");

            using var reader = new StreamReader(ssl, Encoding.ASCII, leaveOpen: true);
            var greeting = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.StartsWith("* OK", greeting);
        }

        await operatorClient.PostAsync("/v1/proxy/listeners/imap-tls/disable", null);
    }

    private static async Task<int> ReadOrZeroAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        try
        {
            return await stream.ReadAsync(buffer, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (System.IO.IOException)
        {
            return 0;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private static TestHost ProxyHost()
        => new TestHost()
            .Configure(ProxyEnabled, "true")
            .Override(services =>
            {
                services.RemoveAll<IProxySecretSource>();
                services.AddSingleton<IProxySecretSource>(new FixtureProxySecretSource());
            });

    private static TestHost HostWithListener(FakeListenerHandleFactory? factory)
    {
        var host = ProxyHost()
            .Configure("StyloMail:Proxy:Listeners:0:ListenerId", "imap-main")
            .Configure("StyloMail:Proxy:Listeners:0:TenantId", TestPrincipals.AcmeTenant)
            .Configure("StyloMail:Proxy:Listeners:0:Protocol", "Imap")
            .Configure("StyloMail:Proxy:Listeners:0:BindAddress", "127.0.0.1")
            .Configure("StyloMail:Proxy:Listeners:0:Port", "0")
            .Configure("StyloMail:Proxy:Listeners:0:TlsPolicy", "LoopbackHarnessPlaintext")
            .Configure("StyloMail:Proxy:Listeners:0:Enabled", "false");

        if (factory is not null)
        {
            host.Override(services =>
            {
                services.RemoveAll<IProxyListenerHandleFactory>();
                services.AddSingleton<IProxyListenerHandleFactory>(factory);
            });
        }

        return host;
    }

    private static object AccountBody(string login, string backendAccountId)
        => new
        {
            login,
            clientPassword = "client-secret-value",
            backendAccountId,
            providerId = "gmail",
            credentialDiscriminator = "app-password",
            backendUsername = "backend-user",
            backendSecret = "backend-secret-value",
        };

    /// <summary>Creates a throwaway self-signed certificate for a loopback TLS listener.</summary>
    private static string CreateCertificate(string directory)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));

        var path = Path.Combine(directory, "listener.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, (string?)null));
        return path;
    }

    private static async Task<ProxyAccountCreatedResponse> EnrolAsync(
        HttpClient client,
        string login,
        string backendAccountId)
    {
        var response = await client.PostAsJsonAsync(
            "/v1/proxy/accounts",
            AccountBody(login, backendAccountId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await ReadAsync<ProxyAccountCreatedResponse>(response);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"Expected a success, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var raw = await response.Content.ReadAsStringAsync();
        var value = JsonSerializer.Deserialize<T>(raw, HostJson.Options);
        Assert.NotNull(value);
        return value!;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    private static async Task<ProxyListenerResponse> WaitForStoppedAsync(HttpClient reviewer)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var listing = await ReadAsync<ProxyListenersResponse>(
                await reviewer.GetAsync("/v1/proxy/listeners"));
            var listener = listing.Listeners[0];
            if (listener.State == "stopped")
            {
                return listener;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The listener did not report stopped after its drain was released.");
    }

    private sealed class FixtureProxySecretSource : IProxySecretSource
    {
        public byte[] GetLoginKey()
            => System.Text.Encoding.UTF8.GetBytes("fixture-login-key-32-bytes-long!!");

        public byte[] GetProtectionKey()
            => System.Text.Encoding.UTF8.GetBytes("fixture-protection-key-32-bytes!");
    }

    private sealed class ThrowingEnrollmentService : IProxyEnrollmentService
    {
        private readonly ProxyEnrollmentConflict _conflict;

        public ThrowingEnrollmentService(ProxyEnrollmentConflict conflict) => _conflict = conflict;

        public ValueTask EnrolAsync(ProxyEnrollmentRequest request, CancellationToken cancellationToken)
            => throw new ProxyEnrollmentConflictException(_conflict);
    }

    private sealed class FakeListenerHandleFactory : IProxyListenerHandleFactory
    {
        public FakeListenerHandle? Last { get; private set; }

        public IProxyListenerHandle? Create(HostProxyListenerOptions listener)
            => Last = new FakeListenerHandle();
    }

    private sealed class FakeListenerHandle : IProxyListenerHandle
    {
        private readonly TaskCompletionSource _drain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when StopAsync has been entered, before it waits for the drain.</summary>
        public Task Started => _started.Task;

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int? BoundPort => 54123;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            _started.TrySetResult();
            await _drain.Task.WaitAsync(cancellationToken);
        }

        public void AllowDrain() => _drain.TrySetResult();

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _drain.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
