using Microsoft.AspNetCore.SignalR;
using StyloMail.Host.Auth;

namespace StyloMail.Host.Traffic;

/// <summary>
/// The live-traffic hub a console subscribes to.
/// </summary>
/// <remarks>
/// <para>
/// <b>A client receives; it never asks.</b> This hub declares no callable method, so there is no
/// surface to misuse and no way for a caller to choose what it hears. Everything it sends is driven
/// by a change that already happened somewhere durable.
/// </para>
/// <para>
/// <b>The audience is derived from the authenticated principal and from nothing else.</b> A
/// connection joins one group, named by the tenant its API key resolved to, and the client does not
/// supply that name, cannot ask to join another, and has no call that would let it. A hub that took
/// the tenant off the connection would let any authenticated console read every tenant's traffic,
/// which is the failure this design has to be structurally incapable of.
/// </para>
/// <para>
/// <b>Authentication is the same header on the same route as everything else.</b> The console sends
/// its API key on the negotiate request and on the WebSocket handshake. It is never placed in the
/// query string, which is where SignalR's access-token pattern would put it and where it would land
/// in access logs, proxies and crash reports. The host does not read a token from the URL at all,
/// so there is nothing to be tempted by.
/// </para>
/// <para>
/// <b><c>wss://</c> off loopback is the client's rule</b> and is documented with the rest of the
/// console's transport policy; the host serves both and cannot decide it.
/// </para>
/// </remarks>
public sealed class TrafficHub : Hub
{
    /// <summary>Where the hub is mapped, and the prefix its negotiate route hangs off.</summary>
    /// <remarks>
    /// Under <c>/v1</c> with the rest of the authenticated surface, and mapped only when a
    /// deployment has enabled live traffic: a route that exists and always refuses invites a
    /// configuration change to "fix" it, and an absent one says this deployment has no feed.
    /// </remarks>
    public const string Path = "/v1/traffic";

    /// <summary>
    /// The single client-side method a notice arrives on.
    /// </summary>
    /// <remarks>
    /// One method carrying a kind, rather than one method per kind. A console subscribes once and
    /// switches on the kind, so adding a kind later does not require every console to have
    /// registered a handler for it first, which is the difference between a feature a client can
    /// receive and a feature it silently drops.
    /// </remarks>
    public const string NoticeMethod = "traffic";

    /// <summary>What a connection is allowed to hear, in the membership terms the transport uses.</summary>
    /// <remarks>
    /// A method rather than a bare expression so the rule is one readable line with a name, and so
    /// a test can assert the mapping without reaching into the hub's callback.
    /// </remarks>
    public static string AudienceFor(string tenantId) => tenantId;

    /// <summary>
    /// Puts this connection in its own tenant's group, before anything can be sent to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The tenant comes from the connection's authenticated identity.</b> Nothing on the wire
    /// carries it, and there is no call a client could make to change it. A connection whose
    /// principal somehow has no tenant is put in no group at all: it is connected, receives
    /// nothing, and cannot be addressed by a tenant it might otherwise be mistaken for.
    /// </para>
    /// <para>
    /// Awaited rather than fired and forgotten. A connection that reached the console before its
    /// group membership was established would be a console that looks live and hears nothing, which
    /// is exactly the silently-frozen feed the second rule exists to prevent.
    /// </para>
    /// </remarks>
    public override async Task OnConnectedAsync()
    {
        if (Context.User?.TenantId() is { Length: > 0 } tenantId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AudienceFor(tenantId))
                .ConfigureAwait(false);
        }

        await base.OnConnectedAsync().ConfigureAwait(false);
    }
}
