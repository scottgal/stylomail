using System.Buffers.Text;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StyloMail.AccessProxy.Accounts;
using StyloMail.AccessProxy.Credentials;
using StyloMail.Host.Auth;
using StyloMail.Host.Contracts;
using StyloMail.Host.Proxy;
using StyloMail.Host.Serialization;

namespace StyloMail.Host.Endpoints;

/// <summary>
/// The <c>/v1/proxy</c> management surface: accounts, live sessions, listeners, operations and
/// structural conversations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenant comes from the authenticated principal and from nowhere else.</b> There is no tenant
/// parameter on any route, so a cross-tenant read is absent rather than refused: a caller has
/// nothing to name. Reads require <c>Review</c>; mutations require <c>Administer</c>.
/// </para>
/// <para>
/// <b>Nothing here carries content.</b> No message body, subject, header value, raw login, backend
/// username, peer address, certificate reference or secret appears in a normal response. The
/// quarantine review path remains the only content route, and it is not part of this surface.
/// </para>
/// <para>
/// <b>Unknown and zero are different answers.</b> Where telemetry has never been produced the
/// response says <c>not_produced</c> and its numeric fields are null. Where the stream is readable
/// and the window is empty, the answer is a real zero. The two are never collapsed into one.
/// </para>
/// </remarks>
internal static class ProxyEndpoints
{
    internal const string Last24Hours = "last_24_hours";
    internal const string Available = "available";
    internal const string NotProduced = "not_produced";
    private const string ListenerEventsProvenance = "proxy_listener_events";
    private const string ThreadingProvenance = "proxy_threading_and_assessment";
    private const int DefaultLimit = 50;
    private const int MaximumLimit = 100;

    internal static void Map(IEndpointRouteBuilder v1)
    {
        var proxy = v1.MapGroup("/proxy");

        // Enrolment and lifecycle. A caller-chosen account id is not accepted; the Host mints it.
        proxy.MapPost("/accounts", CreateAccountAsync)
            .RequireAuthorization(HostPolicies.Administer);

        proxy.MapGet("/accounts", ListAccountsAsync)
            .RequireAuthorization(HostPolicies.Review);

        proxy.MapPost("/accounts/{id}/disable", DisableAccountAsync)
            .RequireAuthorization(HostPolicies.Administer);

        proxy.MapPost("/accounts/{id}/enable", EnableAccountAsync)
            .RequireAuthorization(HostPolicies.Administer);

        proxy.MapPost("/accounts/{id}/credential/revoke", RevokeCredentialAsync)
            .RequireAuthorization(HostPolicies.Administer);

        proxy.MapGet("/sessions", ListSessionsAsync)
            .RequireAuthorization(HostPolicies.Review);

        proxy.MapGet("/listeners", ListListenersAsync)
            .RequireAuthorization(HostPolicies.Review);

        // Enable and disable are real controls. Bind address, port, TLS policy and certificate are
        // deployment configuration and have no route at all.
        proxy.MapPost("/listeners/{id}/enable", EnableListenerAsync)
            .RequireAuthorization(HostPolicies.Administer);

        proxy.MapPost("/listeners/{id}/disable", DisableListenerAsync)
            .RequireAuthorization(HostPolicies.Administer);

        proxy.MapGet("/operations", OperationsAsync)
            .RequireAuthorization(HostPolicies.Review);

        proxy.MapGet("/conversations", ListConversationsAsync)
            .RequireAuthorization(HostPolicies.Review);

        proxy.MapGet("/conversations/{threadToken}/signals", ConversationSignalsAsync)
            .RequireAuthorization(HostPolicies.Review);
    }

    // ---------------------------------------------------------------------------------------------
    // Accounts
    // ---------------------------------------------------------------------------------------------

    internal static async Task<IResult> CreateAccountAsync(
        ProxyAccountCreateRequest? request,
        ClaimsPrincipal user,
        IProxyEnrollmentService enrollment,
        IProxyAccountDirectory accounts,
        IOptions<HostProxyOptions> proxyOptions,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (Tenant(user) is not { } tenantId)
        {
            return EndpointResults.Forbidden("tenant_required", "The principal has no tenant.");
        }

        if (!proxyOptions.Value.Enabled)
        {
            return ProxyNotConfigured();
        }

        if (request is null)
        {
            return EndpointResults.Invalid("body_required", "An account body is required.");
        }

        var login = Clean(request.Login);
        var backendAccountId = Clean(request.BackendAccountId);
        var providerId = Clean(request.ProviderId);
        var discriminator = Clean(request.CredentialDiscriminator);
        var backendUsername = Clean(request.BackendUsername);

        if (login is null)
        {
            return EndpointResults.Invalid("login_required", "A login is required.");
        }

        if (string.IsNullOrEmpty(request.ClientPassword))
        {
            return EndpointResults.Invalid("client_password_required", "A client password is required.");
        }

        if (backendAccountId is null)
        {
            return EndpointResults.Invalid("backend_account_required", "A backend account id is required.");
        }

        if (providerId is null)
        {
            return EndpointResults.Invalid("provider_required", "A provider id is required.");
        }

        if (discriminator is null)
        {
            return EndpointResults.Invalid("credential_kind_required", "A credential discriminator is required.");
        }

        if (backendUsername is null)
        {
            return EndpointResults.Invalid("backend_username_required", "A backend username is required.");
        }

        if (string.IsNullOrEmpty(request.BackendSecret))
        {
            return EndpointResults.Invalid("backend_secret_required", "A backend credential is required.");
        }

        // Minted here, never accepted from the caller. The prefix makes the id recognisable in a log
        // without embedding tenant or login material in it.
        var accountId = "pa_" + Guid.NewGuid().ToString("N");
        var now = clock.GetUtcNow();

        try
        {
            using var clientPassword = SecretValue.FromUtf8(request.ClientPassword);
            using var backendSecret = SecretValue.FromUtf8(request.BackendSecret);

            await enrollment.EnrolAsync(
                new ProxyEnrollmentRequest
                {
                    TenantId = tenantId,
                    AccountId = accountId,
                    Login = login,
                    ClientPassword = clientPassword,
                    BackendAccountId = backendAccountId,
                    ProviderId = providerId,
                    Discriminator = discriminator,
                    BackendUsername = backendUsername,
                    GrantedScopes = Clean(request.GrantedScopes),
                    BackendSecret = backendSecret,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ProxyEnrollmentConflictException conflict)
        {
            return Conflict(conflict.Conflict);
        }
        catch (CredentialUnavailableException)
        {
            // The credential could not be protected or is not usable. Nothing was written.
            return EndpointResults.NotFound(
                "credential_unavailable",
                "The backend credential is unavailable and the account was not created.");
        }
        catch (ProxyNotConfiguredException)
        {
            return ProxyNotConfigured();
        }
        catch (ProxyConfigurationException)
        {
            return ProxyNotConfigured();
        }

        var stored = await accounts.GetAsync(tenantId, accountId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return EndpointResults.StorageUnavailable("The account could not be read back after enrolment.");
        }

        return Results.Created(
            $"/v1/proxy/accounts/{accountId}",
            new ProxyAccountCreatedResponse
            {
                TenantId = tenantId,
                AccountId = stored.AccountId,
                Enabled = stored.Enabled,
                EnrollmentState = stored.EnrollmentState,
                CreatedAt = stored.CreatedAt,
                UpdatedAt = stored.UpdatedAt,
            });
    }

    internal static async Task<IResult> ListAccountsAsync(
        string? limit,
        string? after,
        ClaimsPrincipal user,
        IProxyAccountDirectory accounts,
        CancellationToken cancellationToken)
    {
        if (Tenant(user) is not { } tenantId)
        {
            return EndpointResults.Forbidden("tenant_required", "The principal has no tenant.");
        }

        if (!TryLimit(limit, out var pageSize, out var limitError))
        {
            return limitError;
        }

        if (!TryAfter(after, tenantId, "accounts", pageSize, out var afterAccountId))
        {
            return InvalidCursor();
        }

        var page = await accounts.ListAsync(tenantId, pageSize, afterAccountId, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new ProxyAccountsResponse
        {
            TenantId = tenantId,
            Accounts = [.. page.Accounts.Select(Account)],
            HasMore = page.HasMore,
            NextCursor = page.HasMore && page.Accounts.Count > 0
                ? ProxyCursors.Encode(tenantId, "accounts", pageSize, page.Accounts[^1].AccountId)
                : null,
        });
    }

    internal static async Task<IResult> DisableAccountAsync(
        string id,
        ClaimsPrincipal user,
        IProxyEnrollmentPersistence persistence,
        IProxyAccountDirectory accounts,
        TimeProvider clock,
        CancellationToken cancellationToken)
        => await SetAccountEnabledAsync(id, enabled: false, user, persistence, accounts, clock, cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<IResult> EnableAccountAsync(
        string id,
        ClaimsPrincipal user,
        IProxyEnrollmentPersistence persistence,
        IProxyAccountDirectory accounts,
        TimeProvider clock,
        CancellationToken cancellationToken)
        => await SetAccountEnabledAsync(id, enabled: true, user, persistence, accounts, clock, cancellationToken)
            .ConfigureAwait(false);

    private static async Task<IResult> SetAccountEnabledAsync(
        string id,
        bool enabled,
        ClaimsPrincipal user,
        IProxyEnrollmentPersistence persistence,
        IProxyAccountDirectory accounts,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (Tenant(user) is not { } tenantId)
        {
            return EndpointResults.Forbidden("tenant_required", "The principal has no tenant.");
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return EndpointResults.Invalid("account_required", "An account identifier is required.");
        }

        try
        {
            await persistence.SetAccountEnabledAsync(
                new ProxyAccountStateChange
                {
                    TenantId = tenantId,
                    AccountId = id,
                    Enabled = enabled,
                    UpdatedAt = clock.GetUtcNow(),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ProxyEnrollmentConflictException conflict)
            when (conflict.Conflict == ProxyEnrollmentConflict.AccountNotFound)
        {
            return AccountNotFound();
        }

        var stored = await accounts.GetAsync(tenantId, id, cancellationToken).ConfigureAwait(false);
        return stored is null ? AccountNotFound() : Results.Ok(Account(stored));
    }

    internal static async Task<IResult> RevokeCredentialAsync(
        string id,
        ClaimsPrincipal user,
        IStyloMailAccountStore accountStore,
        IProxyEnrollmentPersistence persistence,
        IProxyAccountDirectory accounts,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (Tenant(user) is not { } tenantId)
        {
            return EndpointResults.Forbidden("tenant_required", "The principal has no tenant.");
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return EndpointResults.Invalid("account_required", "An account identifier is required.");
        }

        var account = await accountStore.FindByIdAsync(tenantId, id, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return AccountNotFound();
        }

        try
        {
            await persistence.RevokeBackendCredentialAsync(
                new ProxyBackendCredentialRevocation
                {
                    TenantId = tenantId,
                    BackendAccountId = account.BackendAccountId,
                    RevokedAt = clock.GetUtcNow(),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ProxyEnrollmentConflictException conflict)
            when (conflict.Conflict is ProxyEnrollmentConflict.CredentialNotFound
                or ProxyEnrollmentConflict.AccountNotFound)
        {
            return EndpointResults.NotFound(
                "credential_not_found",
                "That account has no backend credential to revoke.");
        }

        var stored = await accounts.GetAsync(tenantId, id, cancellationToken).ConfigureAwait(false);
        return stored is null ? AccountNotFound() : Results.Ok(Account(stored));
    }

    // ---------------------------------------------------------------------------------------------
    // Live sessions
    // ---------------------------------------------------------------------------------------------

    internal static async Task<IResult> ListSessionsAsync(
        string? scope,
        string? limit,
        string? after,
        ClaimsPrincipal user,
        IProxyTelemetryReader telemetry,
        CancellationToken cancellationToken)
    {
        if (Tenant(user) is not { } tenantId)
        {
            return EndpointResults.Forbidden("tenant_required", "The principal has no tenant.");
        }

        if (!string.Equals(scope, "live", StringComparison.Ordinal))
        {
            return EndpointResults.Invalid(
                "invalid_scope",
                "The only supported session scope is 'live'.");
        }

        if (!TryLimit(limit, out var pageSize, out var limitError))
        {
            return limitError;
        }

        if (!TryAfter(after, tenantId, "sessions", pageSize, out var afterSessionId))
        {
            return InvalidCursor();
        }

        // The stream, not the count, decides availability. A tenant with no listener event at all
        // has never had telemetry produced, so its empty list is not a measured zero.
        var produced = await telemetry.HasListenerTelemetryAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (!produced)
        {
            return Results.Ok(new ProxySessionsResponse
            {
                TenantId = tenantId,
                Scope = "live",
                Availability = NotProduced,
                Sessions = [],
                HasMore = false,
                NextCursor = null,
            });
        }

        var sessions = await telemetry.ListLiveSessionsAsync(tenantId, pageSize, afterSessionId, cancellationToken)
            .ConfigureAwait(false);

        var page = sessions.Count > pageSize ? sessions.Take(pageSize).ToArray() : sessions;
        var hasMore = sessions.Count > pageSize;

        return Results.Ok(new ProxySessionsResponse
        {
            TenantId = tenantId,
            Scope = "live",
            Availability = Available,
            Sessions = [.. page.Select(session => new ProxySessionResponse
            {
                SessionId = session.SessionId,
                AccountId = session.AccountId,
                Protocol = session.Protocol.ToString(),
                Direction = session.Direction.ToString(),
                StartedAt = session.StartedAt,
                AuthenticatedAt = null,
                EndedAt = null,
                Outcome = null,
                // No byte counter is wired into the session lifecycle event yet. The event's numeric
                // fields default to zero, so projecting them would turn "not measured" into a
                // measured zero. Null is the honest value until the counter lands.
                ClientBytes = null,
                BackendBytes = null,
                BoundReason = null,
            })],
            HasMore = hasMore,
            NextCursor = hasMore && page.Count > 0
                ? ProxyCursors.Encode(tenantId, "sessions", pageSize, page[^1].SessionId)
                : null,
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Listeners
    // ---------------------------------------------------------------------------------------------

    internal static IResult ListListenersAsync(
        ClaimsPrincipal user,
        IProxyListenerControl listeners,
        ProxyTrustAnchors trustAnchors)
    {
        if (Tenant(user) is not { } tenantId)
        {
            return EndpointResults.Forbidden("tenant_required", "The principal has no tenant.");
        }

        return Results.Ok(new ProxyListenersResponse
        {
            TenantId = tenantId,
            Listeners = [.. listeners.Snapshot(tenantId).Select(Listener)],
            Providers = [.. trustAnchors.Describe().Select(provider => new ProxyProviderTrustResponse
            {
                ProviderId = provider.ProviderId,
                CustomTrustAnchor = provider.CustomTrustAnchor,
            })],
        });
    }

    internal static async Task<IResult> EnableListenerAsync(
        string id,
        ClaimsPrincipal user,
        IProxyListenerControl listeners,
        CancellationToken cancellationToken)
    {
        if (Tenant(user) is not { } tenantId)
        {
            return EndpointResults.Forbidden("tenant_required", "The principal has no tenant.");
        }

        var result = await listeners.EnableAsync(tenantId, id, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return ListenerNotFound();
        }

        if (string.Equals(result.State, "faulted", StringComparison.Ordinal))
        {
            return Results.Json(
                new ErrorResponse(
                    "listener_faulted",
                    "The listener could not be started. Its fault reason is available on the listener list."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (string.Equals(result.State, "not_produced", StringComparison.Ordinal))
        {
            return Results.Json(
                new ErrorResponse(
                    "listener_runtime_unavailable",
                    "This deployment does not compose the proxy listener runtime, so the listener cannot be started."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(Listener(result));
    }

    internal static async Task<IResult> DisableListenerAsync(
        string id,
        ClaimsPrincipal user,
        IProxyListenerControl listeners,
        CancellationToken cancellationToken)
    {
        if (Tenant(user) is not { } tenantId)
        {
            return EndpointResults.Forbidden("tenant_required", "The principal has no tenant.");
        }

        var result = await listeners.DisableAsync(tenantId, id, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return ListenerNotFound();
        }

        // 202 rather than 200: disable has stopped new sessions, but the drain is still running.
        // The returned snapshot says so with state=stopping and draining=true, and a later
        // GET /listeners is the readback that says stopped.
        return Results.Accepted(value: Listener(result));
    }

    // ---------------------------------------------------------------------------------------------
    // Operations
    // ---------------------------------------------------------------------------------------------

    internal static async Task<IResult> OperationsAsync(
        string? window,
        ClaimsPrincipal user,
        IProxyTelemetryReader telemetry,
        IProxyListenerControl listeners,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (Tenant(user) is not { } tenantId)
        {
            return EndpointResults.Forbidden("tenant_required", "The principal has no tenant.");
        }

        if (!IsLast24Hours(window))
        {
            return InvalidWindow();
        }

        var windowEnd = clock.GetUtcNow();
        var windowStart = windowEnd.AddHours(-24);

        var produced = await telemetry.HasListenerTelemetryAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        ProxyEventCounters counters;
        if (produced)
        {
            counters = await telemetry.CountAsync(tenantId, windowStart, windowEnd, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // Null, not zero. A renderer that receives 0 draws a flat line through a period the
            // deployment never measured.
            counters = new ProxyEventCounters(null, null, null, null);
        }

        return Results.Ok(new ProxyOperationsResponse
        {
            TenantId = tenantId,
            Window = Last24Hours,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Availability = produced ? Available : NotProduced,
            Listeners = [.. listeners.Snapshot(tenantId).Select(Listener)],
            Counters = new ProxyCountersResponse
            {
                LiveSessions = counters.LiveSessions,
                Opened = counters.Opened,
                Closed = counters.Closed,
                Faulted = counters.Faulted,
            },
            Provenance = ListenerEventsProvenance,
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Conversations
    // ---------------------------------------------------------------------------------------------

    internal static async Task<IResult> ListConversationsAsync(
        string? window,
        string? limit,
        string? after,
        ClaimsPrincipal user,
        IProxyTelemetryReader telemetry,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (Tenant(user) is not { } tenantId)
        {
            return EndpointResults.Forbidden("tenant_required", "The principal has no tenant.");
        }

        if (!IsLast24Hours(window))
        {
            return InvalidWindow();
        }

        if (!TryLimit(limit, out var pageSize, out var limitError))
        {
            return limitError;
        }

        if (!TryAfter(after, tenantId, "conversations", pageSize, out var afterThread))
        {
            return InvalidCursor();
        }

        var windowEnd = clock.GetUtcNow();
        var windowStart = windowEnd.AddHours(-24);

        var produced = await telemetry.HasThreadingTelemetryAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (!produced)
        {
            return Results.Ok(new ProxyConversationsResponse
            {
                TenantId = tenantId,
                Window = Last24Hours,
                WindowStart = windowStart,
                WindowEnd = windowEnd,
                Availability = NotProduced,
                Conversations = [],
                HasMore = false,
                NextCursor = null,
            });
        }

        var rows = await telemetry.ListConversationsAsync(
            tenantId, windowStart, windowEnd, pageSize, afterThread, cancellationToken).ConfigureAwait(false);

        var page = rows.Count > pageSize ? rows.Take(pageSize).ToArray() : rows;
        var hasMore = rows.Count > pageSize;

        return Results.Ok(new ProxyConversationsResponse
        {
            TenantId = tenantId,
            Window = Last24Hours,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Availability = Available,
            Conversations = [.. page.Select(row => new ProxyConversationResponse
            {
                ThreadToken = row.ThreadToken,
                AccountId = row.AccountId,
                ParticipantTokens = row.ParticipantTokens,
                ReferenceDepth = row.ReferenceDepth,
                LastActivityAt = row.LastActivityAt,
                Availability = row.Availability,
            })],
            HasMore = hasMore,
            NextCursor = hasMore && page.Count > 0
                ? ProxyCursors.Encode(tenantId, "conversations", pageSize, page[^1].ThreadToken)
                : null,
        });
    }

    internal static async Task<IResult> ConversationSignalsAsync(
        string threadToken,
        string? window,
        ClaimsPrincipal user,
        IProxyTelemetryReader telemetry,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (Tenant(user) is not { } tenantId)
        {
            return EndpointResults.Forbidden("tenant_required", "The principal has no tenant.");
        }

        // A thread token is an opaque base64url token with no padding. Anything else is refused
        // before it reaches a query, so a token that could be a path or a SQL fragment never is one.
        if (!ProxyTokens.IsUrlSafe(threadToken))
        {
            return EndpointResults.Invalid(
                "invalid_thread_token",
                "A thread token must be unpadded base64url (A-Z, a-z, 0-9, '-' and '_').");
        }

        if (!IsLast24Hours(window))
        {
            return InvalidWindow();
        }

        var exists = await telemetry.HasConversationAsync(tenantId, threadToken, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            return EndpointResults.NotFound(
                "conversation_not_found",
                "No conversation with that thread token is recorded for this tenant.");
        }

        var windowEnd = clock.GetUtcNow();
        var windowStart = windowEnd.AddHours(-24);

        var points = await telemetry.ListThreadPointsAsync(
            tenantId, threadToken, windowStart, windowEnd, cancellationToken).ConfigureAwait(false);

        return Results.Ok(new ProxyConversationSignalsResponse
        {
            TenantId = tenantId,
            ThreadToken = threadToken,
            Window = Last24Hours,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Provenance = ThreadingProvenance,
            // An empty list here is a readable empty window. Every point that exists states its own
            // availability, so a missing semantic measurement is an explicit not_produced point and
            // is never omitted or interpolated between neighbours.
            Points = [.. points.Select(point => new ProxySignalPointResponse
            {
                ObservedAt = point.ObservedAt,
                TurnToken = point.TurnToken,
                ToneScore = point.ToneScore,
                WarningMarkers = point.WarningMarkers,
                DimensionIds = point.DimensionIds,
                Availability = point.Availability,
            })],
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Shapes and validation
    // ---------------------------------------------------------------------------------------------

    private static ProxyAccountResponse Account(ProxyAccountRecord account) => new()
    {
        AccountId = account.AccountId,
        LoginKey = account.LoginKey,
        Enabled = account.Enabled,
        ProviderId = account.ProviderId,
        CredentialDiscriminator = account.CredentialDiscriminator,
        EnrollmentState = account.EnrollmentState,
        CreatedAt = account.CreatedAt,
        UpdatedAt = account.UpdatedAt,
        RevokedAt = account.RevokedAt,
    };

    private static ProxyListenerResponse Listener(ProxyListenerSnapshot listener) => new()
    {
        ListenerId = listener.ListenerId,
        Protocol = listener.Protocol.ToString(),
        BindPort = listener.BindPort,
        TlsPolicy = listener.TlsPolicy.ToString(),
        Enabled = listener.Enabled,
        State = listener.State,
        FaultReason = listener.FaultReason,
        SafeDetail = listener.SafeDetail,
        ObservedAt = listener.ObservedAt,
        Draining = listener.Draining,
    };

    private static string? Tenant(ClaimsPrincipal user) => user.TenantId();

    /// <summary>Trims an identifier. Blanks become absent rather than a string of spaces.</summary>
    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsLast24Hours(string? window)
        => string.Equals(window, Last24Hours, StringComparison.Ordinal);

    private static bool TryLimit(string? raw, out int limit, out IResult error)
    {
        limit = DefaultLimit;
        error = Results.Empty;

        if (raw is null)
        {
            return true;
        }

        if (!int.TryParse(raw, out var parsed) || parsed < 1 || parsed > MaximumLimit)
        {
            error = EndpointResults.Invalid(
                "invalid_limit",
                $"A page size must be between 1 and {MaximumLimit}.");
            return false;
        }

        limit = parsed;
        return true;
    }

    private static bool TryAfter(
        string? after,
        string tenantId,
        string scope,
        int limit,
        out string? key)
        => ProxyCursors.TryDecode(after, tenantId, scope, limit, out key);

    private static IResult Conflict(ProxyEnrollmentConflict conflict) => conflict switch
    {
        ProxyEnrollmentConflict.DuplicateLoginKey => EndpointResults.Conflict(
            "duplicate_login",
            "An account with that login already exists in this tenant."),
        ProxyEnrollmentConflict.DuplicateAccountId => EndpointResults.Conflict(
            "duplicate_account",
            "That account identifier is already in use."),
        ProxyEnrollmentConflict.TenantMismatch => Results.Json(
            new ErrorResponse("tenant_mismatch", "The account and credential belong to different tenants."),
            statusCode: StatusCodes.Status422UnprocessableEntity),
        ProxyEnrollmentConflict.AccountNotFound => AccountNotFound(),
        ProxyEnrollmentConflict.CredentialNotFound => EndpointResults.NotFound(
            "credential_not_found",
            "That account has no backend credential."),
        ProxyEnrollmentConflict.BackendCredentialUnavailable => EndpointResults.NotFound(
            "credential_unavailable",
            "The backend credential is unavailable or revoked."),
        _ => EndpointResults.Conflict("enrolment_conflict", "The enrolment was rejected."),
    };

    private static IResult AccountNotFound()
        => EndpointResults.NotFound("account_not_found", "No such account exists in this tenant.");

    private static IResult ListenerNotFound()
        => EndpointResults.NotFound("listener_not_found", "No such listener is configured for this tenant.");

    private static IResult InvalidCursor()
        => EndpointResults.Invalid(
            "invalid_cursor",
            "The cursor is not valid for this tenant, scope and page size.");

    private static IResult InvalidWindow()
        => EndpointResults.Invalid(
            "invalid_window",
            $"The only supported window is '{Last24Hours}'.");

    private static IResult ProxyNotConfigured()
        => Results.Json(
            new ErrorResponse(
                "proxy_not_configured",
                "The access proxy is not composed in this deployment, so it has no account store to write."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
}

/// <summary>Opaque, tenant- and filter-bound page cursors.</summary>
/// <remarks>
/// The cursor is base64url of a small payload, not an offset: an offset would silently skip or
/// repeat rows when a row is written between two pages. It is bound to the tenant, the scope and
/// the page size, so a cursor from one tenant (or one filter) cannot be replayed into another
/// query. A malformed or mismatched cursor is a 400, never a silently empty page.
/// </remarks>
internal static class ProxyCursors
{
    private sealed record Payload(string Tenant, string Scope, int Limit, string Key);

    public static string Encode(string tenantId, string scope, int limit, string key)
    {
        var payload = new Payload(tenantId, scope, limit, key);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, HostJson.Options);
        return Base64Url.EncodeToString(bytes);
    }

    public static bool TryDecode(
        string? cursor,
        string tenantId,
        string scope,
        int limit,
        out string? key)
    {
        key = null;
        if (cursor is null)
        {
            return true;
        }

        try
        {
            var bytes = Base64Url.DecodeFromChars(cursor);
            var payload = JsonSerializer.Deserialize<Payload>(bytes, HostJson.Options);
            if (payload is null
                || !string.Equals(payload.Tenant, tenantId, StringComparison.Ordinal)
                || !string.Equals(payload.Scope, scope, StringComparison.Ordinal)
                || payload.Limit != limit
                || string.IsNullOrEmpty(payload.Key))
            {
                return false;
            }

            key = payload.Key;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>The token shapes a caller may address a conversation by.</summary>
internal static class ProxyTokens
{
    private const int MaximumLength = 512;

    /// <summary>Unpadded base64url: the only shape a thread token may take.</summary>
    /// <remarks>
    /// <c>+</c>, <c>/</c> and <c>=</c> are rejected rather than normalized. Normalizing would mean
    /// two spellings address the same thread, and padding in a path segment is the shape that turns
    /// a token into a query fragment or a filesystem name depending on who decodes it next.
    /// </remarks>
    public static bool IsUrlSafe(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaximumLength || token.Length % 4 == 1)
        {
            return false;
        }

        foreach (var character in token)
        {
            var allowed = character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-'
                or '_';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}
