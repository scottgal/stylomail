using Microsoft.Extensions.DependencyInjection;
using StyloMail.Host.Chat;
using StyloMail.Host.Observability;

namespace StyloMail.Host.Tests;

/// <summary>
/// The readiness line for chat events that can never be assessed.
/// </summary>
/// <remarks>
/// Without it, a deployment with no profile master key still starts, still accepts events and still
/// stores them, and every one of them waits forever. That looks exactly like a drain that is merely
/// busy, so the cause is only inferable from a log line. Readiness is where the host states it.
/// </remarks>
public sealed class ChatAssessmentReadinessTests
{
    [Fact]
    public void Chat_events_that_cannot_be_assessed_make_the_host_not_ready()
    {
        using var host = new TestHost();
        var probe = host.Services.GetRequiredService<ReadinessProbe>();
        var intake = host.Services.GetRequiredService<IChatIntakeStore>();
        var health = host.Services.GetRequiredService<ChatAssessmentHealth>();

        // A host that cannot assess chat but has been told nothing is not degraded by an unused path.
        Assert.True(probe.Check().Ready);

        health.IsUnavailable = true;
        Assert.True(probe.Check().Ready);

        intake.Admit(new ChatIntakeEntry("Ev01", "{}", DateTimeOffset.UnixEpoch), capacity: 8);

        var result = probe.Check();

        Assert.False(result.Ready);
        Assert.Contains(ReadinessProbe.ChatAssessmentUnavailable, result.FailedChecks);
    }

    [Fact]
    public void A_host_that_can_assess_chat_stays_ready_while_events_wait()
    {
        // The check is about being unable to assess, not about having work to do. A busy drain is
        // the system working, and marking it not-ready would take a healthy deployment out of
        // rotation for being used.
        using var host = new TestHost();
        var probe = host.Services.GetRequiredService<ReadinessProbe>();
        var intake = host.Services.GetRequiredService<IChatIntakeStore>();

        intake.Admit(new ChatIntakeEntry("Ev01", "{}", DateTimeOffset.UnixEpoch), capacity: 8);

        Assert.True(probe.Check().Ready);
    }
}
