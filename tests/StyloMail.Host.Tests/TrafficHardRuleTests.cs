using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Core;
using StyloMail.Host.Hosting;
using StyloMail.Host.Traffic;
using StyloMail.Queue;

namespace StyloMail.Host.Tests;

/// <summary>
/// The rule the whole seam exists to hold: a hub outage is invisible to mail.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are the evidence, and they are the reason the port exists.</b> Emission happens inside
/// an assessment and inside a delivery, so a hub that could throw into either would turn a console
/// problem into lost mail. Every test here runs the real thing with the hub failing underneath it
/// and asserts the mail path is unchanged, not merely that it did not crash.
/// </para>
/// <para>
/// <b>"Unchanged" is asserted against the same call with no hub at all</b>, and compared field by
/// field. A test that only asserted a 200 would pass over a decision that had quietly become a
/// different one.
/// </para>
/// </remarks>
public sealed class TrafficHardRuleTests
{
    [Fact]
    public async Task An_assessment_with_the_hub_down_is_the_assessment_it_would_have_been()
    {
        using var withoutTheFeature = new TestHost().WithClock();
        using var withTheHubDown = TrafficSeamTests.WithTraffic().WithFailingHub().WithClock();

        var expected = await AssessAsync(withoutTheFeature);
        var actual = await AssessAsync(withTheHubDown);

        Assert.Equal(expected.Canonical, actual.Canonical);
    }

    [Fact]
    public async Task The_decision_the_hub_was_down_for_is_still_durably_recorded()
    {
        // The emission sits inside the ledger write's own method, so "the decision was recorded"
        // is the other half of what a throwing hub could have taken away. Asserted by reading it
        // back rather than by trusting the response.
        using var host = TrafficSeamTests.WithTraffic().WithFailingHub();

        var assessed = await AssessAsync(host);

        var recorded = await host.Services
            .GetRequiredService<StyloMail.Host.Decisions.IDecisionLedger>()
            .FindAsync(TestPrincipals.AcmeTenant, assessed.AssessmentId, CancellationToken.None);

        Assert.NotNull(recorded);
    }

    [Fact]
    public async Task A_delivery_with_the_hub_down_settles_exactly_as_it_would_have()
    {
        using var withTheHubDown = TrafficSeamTests.WithTraffic().WithFailingHub();

        var failing = await DeliverOnceAsync(
            withTheHubDown,
            withTheHubDown.Services.GetRequiredService<ITrafficEvents>());

        using var withoutTheFeature = new TestHost();

        var quiet = await DeliverOnceAsync(withoutTheFeature, NullTrafficEvents.Instance);

        Assert.Equal(quiet.Outcome, failing.Outcome);
        Assert.Equal(DeliveryCycleOutcome.Dispatched, failing.Outcome);
        Assert.Equal(quiet.RecipientStates, failing.RecipientStates);
        Assert.Equal(["Delivered"], failing.RecipientStates);

        // The transport was reached in both runs. Without this, a wrapper that swallowed the
        // delivery along with the exception would satisfy every assertion above.
        Assert.Equal(1, failing.PortCalls);
        Assert.Equal(1, quiet.PortCalls);
    }

    [Fact]
    public void The_hosted_worker_dials_through_the_port_that_announces()
    {
        // The wrapper is applied where the port is built, not where it is registered, because
        // whether a port exists at all is the same question as whether the worker runs. That makes
        // it invisible to a reader of the container, so it is asserted here instead.
        using var host = new TestHost();

        host.Configure("StyloMail:Transport:Upstream:Host", "192.0.2.1");
        host.Configure("StyloMail:Transport:Upstream:Port", "25");
        host.Configure("StyloMail:Transport:Upstream:Tls", "None");

        var port = host.Services.GetRequiredService<QueueDeliveryHostedService>().DeliveryPort;

        var announcing = Assert.IsType<TrafficEmittingDeliveryPort>(port);
        Assert.IsType<StyloMail.Transport.Delivery.SmtpDeliveryPort>(announcing.Inner);
    }

    [Fact]
    public void No_component_of_this_host_holds_a_hub_context_except_the_one_that_has_to()
    {
        // "No pipeline code may depend on the hub" is a statement about the shape of the program,
        // and this is where it is checked rather than promised. A component holding an IHubContext
        // has a dependency on the transport, its failure modes and its lifetime; one holding
        // ITrafficEvents has a call it makes and forgets.
        //
        // The adapter is the single exception by construction: something has to speak SignalR, and
        // having exactly one such place is what makes "every real implementation wraps its own
        // body" a claim about one file instead of an audit of five call sites.
        var allowed = new[] { typeof(SignalRTrafficEvents) };

        var holders = typeof(Program).Assembly.GetTypes()
            .Where(type => !allowed.Contains(type))
            .Where(HoldsHubContext)
            .Select(type => type.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([], holders.Select(name => name ?? "<anonymous>").ToArray());
    }

    /// <summary>
    /// Whether this type ever names a hub context, in a field, a property, a parameter or a return.
    /// </summary>
    /// <remarks>
    /// Types generated by the compiler are scanned too, deliberately: a closure that captured a hub
    /// context holds one in a field like any other type, and excusing those is how a call site comes
    /// to pass this check without passing the rule.
    /// </remarks>
    private static bool HoldsHubContext(Type type)
    {
        const System.Reflection.BindingFlags All =
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.DeclaredOnly;

        static bool IsHubContext(Type? candidate)
        {
            if (candidate is null)
            {
                return false;
            }

            if (candidate.IsArray)
            {
                return IsHubContext(candidate.GetElementType());
            }

            if (!candidate.IsGenericType)
            {
                return false;
            }

            // Descends into the type arguments rather than replacing a constructed generic with its
            // definition, which is what a loop would do: a definition is itself a generic type whose
            // definition is itself, so that walk never ends. Arguments are a finite tree and the
            // scan terminates.
            return candidate.GetGenericTypeDefinition() == typeof(Microsoft.AspNetCore.SignalR.IHubContext<>)
                || candidate.GetGenericArguments().Any(IsHubContext);
        }

        var fields = type.GetFields(All).Any(f => IsHubContext(f.FieldType))
            || type.GetProperties(All).Any(p => IsHubContext(p.PropertyType))
            || type.GetConstructors(All).SelectMany(c => c.GetParameters()).Any(p => IsHubContext(p.ParameterType))
            || type.GetMethods(All).Any(m =>
                IsHubContext(m.ReturnType) || m.GetParameters().Any(p => IsHubContext(p.ParameterType)));

        return fields;
    }

    private sealed record DeliveryRun(
        DeliveryCycleOutcome Outcome,
        string[] RecipientStates,
        int PortCalls);

    /// <summary>One real delivery through a real worker, store and port wrapper.</summary>
    private static async Task<DeliveryRun> DeliverOnceAsync(TestHost host, ITrafficEvents events)
    {
        var store = host.Services.GetRequiredService<QueueStore>();
        var port = new RecordingDeliveryPort();

        var accepted = await store.AcceptAsync(
            new QueueSubmission
            {
                TenantId = TestPrincipals.AcmeTenant,
                InternalMessageId = $"msg_{Guid.NewGuid():N}",
                Direction = MailDirection.Outbound,
                TrustedPrincipalId = TestPrincipals.AcmeSenderPrincipal,
                MailFrom = "sender@acme.test",
                MimeDigest = "digest",
                Payload = "Subject: hub down\r\n\r\nbody"u8.ToArray(),
                Recipients = [new RecipientAdmission { Recipient = "rcpt@example.test" }],
                IdempotencyKey = $"idem-{Guid.NewGuid():N}",
            },
            CancellationToken.None);

        Assert.True(accepted.IsAccepted, accepted.Detail);

        var worker = new QueueDeliveryWorker(
            store,
            new TrafficEmittingDeliveryPort(port, events, host.Services.GetRequiredService<TimeProvider>()),
            host.Services.GetRequiredService<QueueOptions>());

        var result = await worker.RunOnceAsync();

        var item = await store.GetItemAsync(accepted.QueueId!, TestPrincipals.AcmeTenant, CancellationToken.None);
        Assert.NotNull(item);

        return new DeliveryRun(
            result.Outcome,
            [.. item.Recipients.Select(r => r.State.ToString()).Order(StringComparer.Ordinal)],
            port.Calls);
    }

    private sealed record Assessed(string AssessmentId, string Canonical);

    /// <summary>Assesses one message and answers the decision with its two identifiers removed.</summary>
    private static async Task<Assessed> AssessAsync(TestHost host)
    {
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);
        var response = await client.PostAsJsonAsync("/v1/assessments", TestMessages.Request());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return new Assessed(
            body.RootElement.GetProperty("assessmentId").GetString()!,
            Canonical(body.RootElement));
    }

    /// <summary>
    /// The decision as text, with the two generated identifiers replaced.
    /// </summary>
    /// <remarks>
    /// Everything else is compared, including the reasoning, the versions and the dispositions,
    /// because "the same decision" is a claim about all of it. A comparison that stopped at the
    /// action would be satisfied by an assessment that had quietly lost its reasons.
    /// </remarks>
    private static string Canonical(JsonElement decision)
        => "{"
           + string.Join(
               ",",
               decision.EnumerateObject()
                   .OrderBy(p => p.Name, StringComparer.Ordinal)
                   .Select(p => p.Name is "assessmentId" or "internalMessageId"
                       ? $"\"{p.Name}\":\"<generated>\""
                       : $"\"{p.Name}\":{p.Value.GetRawText()}"))
           + "}";
}
