using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StyloMail.Core;
using StyloMail.Host.Chat;
using StyloMail.Host.Decisions;
using StyloMail.Host.Hosting;

namespace StyloMail.Host.Tests;

/// <summary>
/// What turns a stored event into an assessed one.
/// </summary>
/// <remarks>
/// <b>The drain is driven directly here rather than through the host.</b> Its dependencies are
/// injected, so this exercises the logic the drain actually runs. Whether the host constructs it at
/// all is a separate question and is reported as untested rather than implied by these passing.
/// </remarks>
public sealed class ChatIntakeDrainTests
{
    private const string MessageEvent = """
        {"type":"event_callback","event_id":"Ev01","event_time":1760000000,
         "team_id":"T01",
         "event":{"type":"message","channel":"C01","user":"U01","text":"hello",
                  "ts":"1760000000.000100"}}
        """;

    /// <summary>Records what it was asked to assess, so the drain's calls can be seen.</summary>
    private sealed class RecordingChatAssessor : IChatAssessor
    {
        public int Count { get; private set; }

        public string? LastTenantId { get; private set; }

        public bool Throw { get; set; }

        /// <summary>Fails this many times, then works, so a transient fault can be staged.</summary>
        public int FailuresRemaining { get; set; }

        public ValueTask<MailAssessment> AssessAsync(
            ChatAnalysisInput input,
            AssessmentContext context,
            CancellationToken cancellationToken)
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("assessment failed");
            }

            if (Throw)
            {
                throw new InvalidOperationException("assessment failed");
            }

            Count++;
            LastTenantId = context.TenantId;

            return ValueTask.FromResult(new MailAssessment
            {
                AssessmentId = $"asm_{input.EventId}",
                InternalMessageId = input.EventId,
                TenantId = context.TenantId,
                Channel = input.Channel,
                Evidence = [],
                RiskDimensions = [],
                RiskIndex = 0,
                Action = MailAction.Allow,
                DeliveryTiming = DeliveryTiming.PostDelivery,
                Reasons = [],
                Versions = new AssessmentVersions
                {
                    PolicyVersion = "policy/1",
                    QuestionSchemaVersion = "q/1",
                    PreprocessingVersion = "p/1",
                },
                Coverage = new AnalysisCoverage
                {
                    BodyParsed = true,
                    HtmlPresent = false,
                    HasAttachments = false,
                    HtmlTextDisagreement = false,
                    ParserLimitExceeded = false,
                    ContentEncrypted = false,
                    Truncated = false,
                    ConversationContextMissing = true,
                },
                RecipientDispositions = [],
                AssessedAt = context.TimeProvider.GetUtcNow(),
            });
        }
    }

    private sealed class RecordingLedger : IDecisionLedger
    {
        public List<MailAssessment> Recorded { get; } = [];

        /// <summary>Set to make recording fail, which is the storage being unavailable.</summary>
        public bool Throw { get; set; }

        public Task RecordAsync(MailAssessment assessment, CancellationToken cancellationToken)
        {
            if (Throw)
            {
                throw new InvalidOperationException("the ledger is unavailable");
            }

            Recorded.Add(assessment);
            return Task.CompletedTask;
        }

        public Task<MailAssessment?> FindAsync(string tenantId, string assessmentId, CancellationToken cancellationToken) =>
            Task.FromResult(Recorded.FirstOrDefault(a => a.AssessmentId == assessmentId));

        public Task<DecisionListingPage> ListAsync(DecisionListingQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new DecisionListingPage { Items = [], NextCursor = null });
    }

    /// <summary>Supplies the two services the drain resolves for itself, and nothing else.</summary>
    private sealed class StubProvider(IChatAssessor assessor, IDecisionLedger ledger) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType switch
        {
            var t when t == typeof(IChatAssessor) => assessor,
            var t when t == typeof(IDecisionLedger) => ledger,
            _ => null,
        };
    }

    private static SlackIngressOptions Configured()
    {
        var options = new SlackIngressOptions
        {
            Enabled = true,
            SigningSecret = "configured-in-a-test-only",
            OwnBotId = "B0OWN",
            InboundTenantId = "inbound-slack",
        };

        // Triage dismisses on scope, and an empty watched set watches nothing, so a test that wants a
        // message assessed has to say which channel it watches. That is the decision rather than a
        // convenience: an unconfigured deployment is not a permissive one.
        options.WatchedChannels.Add("C01");

        return options;
    }

    private static async Task DrainOnceAsync(
        TestHost host,
        IChatAssessor assessor,
        IDecisionLedger ledger)
    {
        // The drain takes a provider and resolves the assessor and ledger itself, after checking
        // whether the intake is enabled. This stub is how a test supplies the two without standing up
        // a whole host.
        var provider = new StubProvider(assessor, ledger);

        var drain = new ChatIntakeDrain(
            host.Services.GetRequiredService<IChatIntakeStore>(),
            provider,
            Options.Create(Configured()),
            host.Services.GetRequiredService<TimeProvider>());

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await drain.StartAsync(stop.Token);

        // The drain polls on an idle delay, so a short wait is what lets one pass happen without
        // reaching into its internals. Bounded so a failure to drain fails the test rather than
        // hanging it.
        await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);

        await drain.StopAsync(CancellationToken.None);
    }

    private static IChatIntakeStore Admit(TestHost host, string eventId = "Ev01")
    {
        var intake = host.Services.GetRequiredService<IChatIntakeStore>();
        intake.Admit(new ChatIntakeEntry(eventId, MessageEvent, DateTimeOffset.UnixEpoch), capacity: 8);
        return intake;
    }

    [Fact]
    public async Task A_message_in_a_channel_this_deployment_does_not_watch_never_reaches_the_assessor()
    {
        // Triage runs before the assessor, which is the point of it: the assessor is the expensive
        // thing triage exists to keep the majority of traffic out of. The event is still cleared,
        // because the platform was told it would be dealt with.
        using var host = new TestHost();
        var intake = host.Services.GetRequiredService<IChatIntakeStore>();
        intake.Admit(
            new ChatIntakeEntry(
                "Ev01",
                MessageEvent.Replace("\"channel\":\"C01\"", "\"channel\":\"C99\""),
                DateTimeOffset.UnixEpoch),
            capacity: 8);

        var assessor = new RecordingChatAssessor();

        await DrainOnceAsync(host, assessor, new RecordingLedger());

        Assert.Equal(0, assessor.Count);
        Assert.Empty(intake.Waiting(8));
    }

    [Fact]
    public async Task A_waiting_event_is_assessed_recorded_and_cleared()
    {
        // The three things that make the acknowledgement true rather than merely careful: the event
        // is assessed, the decision reaches the ledger, and the row stops waiting.
        using var host = new TestHost();
        var intake = Admit(host);
        var assessor = new RecordingChatAssessor();
        var ledger = new RecordingLedger();

        await DrainOnceAsync(host, assessor, ledger);

        Assert.Equal(1, assessor.Count);
        Assert.Equal("inbound-slack", assessor.LastTenantId);
        Assert.Single(ledger.Recorded);
        Assert.Empty(intake.Waiting(8));
    }

    [Fact]
    public async Task An_event_already_assessed_is_not_assessed_again()
    {
        // The drain must not re-read a row it has already answered, or every pass would assess the
        // same event and the intake would never empty.
        using var host = new TestHost();
        var intake = Admit(host);
        var assessor = new RecordingChatAssessor();

        await DrainOnceAsync(host, assessor, new RecordingLedger());
        await DrainOnceAsync(host, assessor, new RecordingLedger());

        Assert.Equal(1, assessor.Count);

        // Still remembered rather than removed, so a platform retry is recognised as one already
        // dealt with.
        Assert.Empty(intake.Waiting(8));
    }

    [Fact]
    public async Task An_event_that_cannot_be_assessed_stays_waiting_rather_than_being_dropped()
    {
        // It has not been assessed and the platform was told it would be, so clearing the row here
        // would be exactly the loss the durable intake exists to prevent. Leaving it waiting is
        // visible; a row that vanished would not be.
        using var host = new TestHost();
        var intake = Admit(host);
        var assessor = new RecordingChatAssessor { Throw = true };

        await DrainOnceAsync(host, assessor, new RecordingLedger());

        Assert.Equal("Ev01", Assert.Single(intake.Waiting(8)).EventId);
    }

    [Fact]
    public async Task An_event_admitted_before_a_crash_is_assessed_after_it()
    {
        // The acknowledgement is only true if what we answered for survives us. This admits into one
        // storage and drains from another over the same database, which is the closest the suite gets
        // to a restart: the endpoint answered, the process died before the assessment, and the event
        // is still there to be assessed.
        using var before = new TestHost();
        Admit(before, "Ev01");

        using var after = new TestHost().ReusingStorageOf(before);
        var assessor = new RecordingChatAssessor();
        var ledger = new RecordingLedger();

        await DrainOnceAsync(after, assessor, ledger);

        Assert.Equal(1, assessor.Count);
        Assert.Single(ledger.Recorded);
        Assert.Empty(after.Services.GetRequiredService<IChatIntakeStore>().Waiting(8));
    }

    [Fact]
    public async Task An_event_whose_decision_could_not_be_recorded_stays_waiting()
    {
        // What makes the intake more than a very careful way of losing messages. The assessment
        // happened but the decision did not reach the ledger, so the event is not finished with:
        // marking it complete here would drop the only record that it was ever assessed, which is
        // the same loss as never assessing it.
        using var host = new TestHost();
        var intake = Admit(host);
        var ledger = new RecordingLedger { Throw = true };

        await DrainOnceAsync(host, new RecordingChatAssessor(), ledger);

        Assert.Equal("Ev01", Assert.Single(intake.Waiting(8)).EventId);
    }

    [Fact]
    public async Task A_transient_fault_clears_itself_on_a_later_pass()
    {
        // The drain's remarks claim a fault that clears leaves nothing behind, and that claim is the
        // reason a failure is left waiting rather than being treated as final. This stages one: the
        // first pass cannot assess, the second can.
        using var host = new TestHost();
        var intake = Admit(host);
        var assessor = new RecordingChatAssessor { FailuresRemaining = 1 };

        await DrainOnceAsync(host, assessor, new RecordingLedger());
        Assert.Equal("Ev01", Assert.Single(intake.Waiting(8)).EventId);

        await DrainOnceAsync(host, assessor, new RecordingLedger());

        Assert.Equal(1, assessor.Count);
        Assert.Empty(intake.Waiting(8));
    }

    [Fact]
    public async Task A_disabled_intake_drains_nothing()
    {
        // The drain is registered for every host, so the guard that stops it polling on a deployment
        // with no chat intake is the only thing between that and every deployment running a chat
        // loop it has no use for.
        using var host = new TestHost();
        Admit(host);

        var assessor = new RecordingChatAssessor();

        var disabled = new ChatIntakeDrain(
            host.Services.GetRequiredService<IChatIntakeStore>(),
            new StubProvider(assessor, new RecordingLedger()),
            Options.Create(new SlackIngressOptions { Enabled = false }),
            host.Services.GetRequiredService<TimeProvider>());

        await disabled.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
        await disabled.StopAsync(CancellationToken.None);

        Assert.Equal(0, assessor.Count);
    }

    [Fact]
    public async Task A_payload_that_no_longer_reads_as_a_message_is_cleared()
    {
        // It will not start reading on a later pass, so leaving it would occupy the intake forever
        // while looking like a queue that is merely busy.
        using var host = new TestHost();
        var intake = host.Services.GetRequiredService<IChatIntakeStore>();
        intake.Admit(new ChatIntakeEntry("Ev09", "not json", DateTimeOffset.UnixEpoch), capacity: 8);

        var assessor = new RecordingChatAssessor();

        await DrainOnceAsync(host, assessor, new RecordingLedger());

        Assert.Equal(0, assessor.Count);
        Assert.Empty(intake.Waiting(8));
    }
}
