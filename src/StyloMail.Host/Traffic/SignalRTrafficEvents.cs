using Microsoft.AspNetCore.SignalR;

namespace StyloMail.Host.Traffic;

/// <summary>
/// Publishes changes to the console over SignalR.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its entire body is a try/catch, and that is the implementation rather than a defensive
/// habit.</b> This is the component that stands between the mail path and a transport that can be
/// down, and the hard rule of this lane is that no emission may fail an assessment or a delivery.
/// So <see cref="Publish"/> returns nothing, awaits nothing a caller can see, and cannot throw for
/// any input: a null event, a serialization fault, a disposed context and a dead network all leave
/// through the same door, which nothing is behind.
/// </para>
/// <para>
/// <b>The send is not awaited by the caller and is not cancelled by it either.</b> An assessment
/// being cancelled must not cancel the announcement of a decision it already made, and a delivery
/// that has already happened must not lose its notification because the request that caused it went
/// away. The token is deliberately absent.
/// </para>
/// <para>
/// <b>This is the only type in the host that holds a hub context</b>, which is asserted rather than
/// promised, see <c>TrafficHardRuleTests.No_component_of_this_host_holds_a_hub_context_except_the_one_that_has_to</c>.
/// Everything else publishes through
/// <see cref="ITrafficEvents"/>, so there is exactly one place where a transport failure could
/// become a mail failure, and it is this one, and it catches.
/// </para>
/// </remarks>
public sealed class SignalRTrafficEvents : ITrafficEvents
{
    private readonly IHubContext<TrafficHub> _hub;

    public SignalRTrafficEvents(IHubContext<TrafficHub> hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _hub = hub;
    }

    /// <summary>
    /// Announces a change to the console, or to nobody, and never to the caller.
    /// </summary>
    /// <remarks>
    /// The task is discarded, so this returns the instant the change is handed to the transport
    /// rather than waiting for it. That discard is <em>not</em> what makes this safe: an async
    /// method captures its own exception into the returned task, which means a discarded fault
    /// would be an unobserved one, and the guarantee would rest on nobody ever looking. What makes
    /// it safe is <see cref="PublishAsync"/>, which cannot fault, and that is what the discard is
    /// relying on.
    /// </remarks>
    public void Publish(TrafficEvent change) => _ = PublishAsync(change);

    /// <summary>
    /// Announces a change, awaitable so that "this cannot fail" is observable rather than asserted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Public because the guarantee is the contract, and a guarantee a test cannot observe is a
    /// comment.</b> The fire-and-forget form above hides whether the body faulted, which is exactly
    /// the property that must not be taken on trust: awaiting this task under a hub that throws is
    /// how the test in this lane proves the catch is load-bearing rather than decorative. Callers
    /// inside the host use <see cref="Publish"/>.
    /// </para>
    /// <para>
    /// <b>This completes successfully for every input.</b> A dead transport, a disposed context, a
    /// serialization fault and a null argument all leave through the same door, and nothing is
    /// behind it.
    /// </para>
    /// </remarks>
    public async Task PublishAsync(TrafficEvent change)
    {
        // No argument guard here, deliberately, and it is the one place in this codebase where its
        // absence is the point rather than an oversight. See the remarks: a guard outside the try
        // would be a throw into an assessment, and the tests pin both the null case and the fact
        // that adding one back is what this lane cannot do.
        try
        {
            // A tenant's change goes to that tenant's group and nowhere else. A change about the
            // host goes to every connection, which is not a widening: reachability is already
            // served without credentials at /health/ready, so broadcasting it discloses nothing
            // that route does not, and it carries no tenant content to disclose.
            //
            // Anything else is unaddressable and is dropped. A tenant-scoped change with no tenant
            // has two possible destinations and both are wrong, so this fails closed rather than
            // wide: the alternative would put one tenant's activity in front of every other
            // tenant's console, and a hint that reaches nobody costs nothing because the console's
            // own reads are what it renders from.
            var audience = change.IsHostScoped
                ? _hub.Clients.All
                : change.TenantId is { Length: > 0 } tenant
                    ? _hub.Clients.Group(TrafficHub.AudienceFor(tenant))
                    : null;

            if (audience is not null)
            {
                await audience.SendAsync(TrafficHub.NoticeMethod, change.ToNotice()).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Deliberately and completely swallowed. This catch is the hard rule: a hub outage, a
            // revoked connection, a serialization fault and a caller's mistake all stop here, so
            // that nothing above this line can fail because the console was not watching. There is
            // nothing to log it to either: the deployments this runs in log inside request and
            // delivery paths, and a line per unreachable console would be noise in the one place
            // an operator reads. The hub's own connection state is what a console reports.
        }
    }
}
