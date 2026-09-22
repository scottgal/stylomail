using System.Text;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Adaptive.Profiles;
using StyloMail.Assessment;
using StyloMail.Core;
using StyloMail.Queue;
using StyloMail.Transport.Ingress;

namespace StyloMail.Host.Tests;

/// <summary>
/// The seam between what the ingress sink builds and what the real pipeline does with it.
/// </summary>
/// <remarks>
/// <para>
/// <b>These tests exist because the rest of the ingress suite cannot see this.</b> Every other test
/// here drives the sink against <see cref="RecordingAssessor"/>, which models the pipeline's
/// acceptance contract but never runs its first step — so a placeholder analysis view that the real
/// pipeline refuses on sight would leave the whole suite green while every message an ingress ever
/// handled was declined. The fake is kinder than production in exactly one place, and this is it.
/// </para>
/// <para>
/// The sink hands the pipeline a deliberately empty analysis view — no body, no links, no
/// attachments, coverage saying nothing was parsed — because the pipeline's own MIME parser is
/// authoritative and replaces it from the original bytes. That is only true if the pipeline decides
/// to run the parser, which it does only after step one has passed. Step one therefore sees the
/// empty view, and the empty view has to survive it.
/// </para>
/// </remarks>
public sealed class IngressPipelineSeamTests
{
    [Fact]
    public async Task The_empty_analysis_view_the_sink_hands_over_survives_the_pipelines_first_step()
    {
        using var host = new TestHost();
        var sink = host.Services.GetRequiredService<ISmtpIngressSink>();

        // The outbound submission shape: an authenticated principal whose sender is in the identity
        // list it is authorised for. The two have to agree — the transport enforces that before the
        // sink is reached, and step one enforces it again afterwards.
        await sink.SubmitAsync(
            Submission("msg_seam_out", MailDirection.Outbound, "user-acme-sender",
                mailFrom: "user-acme-sender@acme.example",
                authentication: Authentication("user-acme-sender", ["user-acme-sender@acme.example"])),
            CancellationToken.None);

        // And the inbound handoff shape: no principal, no approved identities, no provenance.
        await sink.SubmitAsync(
            Submission("msg_seam_in", MailDirection.Inbound, "smtp:stylomail",
                authentication: new AuthenticationContext
                {
                    ConnectingIp = null,
                    AuthenticatedAccount = null,
                    Results = [],
                    ApprovedSenderIdentities = [],
                    ProvenanceIncomplete = true,
                }),
            CancellationToken.None);

        var options = AssessorOptions();

        Assert.Equal(2, host.Assessor.Calls.Count);

        foreach (var call in host.Assessor.Calls)
        {
            var violations = AssessmentValidation.Validate(call.Input, call.Context, options);

            Assert.True(
                violations.Count == 0,
                $"The pipeline's own first step refuses this input for {call.Input.Envelope.Direction} "
                + $"traffic: {string.Join(", ", violations)}. The sink's placeholder analysis view is "
                + "what it is validating, and a refusal here means the parser never runs and every "
                + "message this ingress handles is declined.");
        }
    }

    [Fact]
    public async Task An_outbound_null_sender_is_refused_by_the_transport_the_pipeline_and_the_queue()
    {
        // Three components, one rule — and the reason this test exists is that they did not agree
        // until today. The ingress found `MaySendAs` permitting the null sender while
        // `AssessmentValidation` raised `envelope.unapproved_sender_identity` for it and
        // `ValidateSubmission` rejected it outright: one message, three answers, and no component
        // able to see the other two. `transport-` aligned to the ruling that we do not originate
        // bounces.
        //
        // Asserting the *agreement* rather than any one component's rule is the point. A test on
        // `MaySendAs` alone would pass while the pipeline refused every bounce, and a test on
        // `ValidateSubmission` alone would pass while the boundary let one through. This fails if
        // ANY of the three drifts, which is the shape of failure that was actually invisible.
        const string account = "user-acme-sender";
        IReadOnlyList<string> approved = ["user-acme-sender@acme.example"];

        // 1. The transport refuses it — in both the empty and the wire form, since `<>` is what a
        //    client actually sends and an empty string is what a caller constructs.
        var principal = new AuthenticatedPrincipal
        {
            PrincipalId = account,
            TenantId = TestPrincipals.AcmeTenant,
            ApprovedSenderIdentities = approved,
        };

        Assert.False(principal.MaySendAs(string.Empty));
        Assert.False(principal.MaySendAs("<>"));

        // 2. The pipeline's first step refuses it.
        using var host = new TestHost();
        host.Assessor.AcceptanceRefused = true;
        var sink = host.Services.GetRequiredService<ISmtpIngressSink>();

        await sink.SubmitAsync(
            Submission("msg_seam_bounce", MailDirection.Outbound, account,
                mailFrom: string.Empty,
                authentication: Authentication(account, approved)),
            CancellationToken.None);

        var call = Assert.Single(host.Assessor.Calls);

        Assert.Contains(
            AssessmentRules.UnapprovedSenderIdentity,
            AssessmentValidation.Validate(call.Input, call.Context, AssessorOptions()));

        // 3. And the store refuses it, so an ingress that let one through cannot store it either.
        //
        // Refused by *returning* rather than throwing, which is the better half of the same ruling: a
        // thrown exception is indistinguishable from a caller passing an empty tenant id, and an
        // ordinary policy decision should not leave the assessor as an unhandled exception.
        var store = host.Services.GetRequiredService<QueueStore>();

        var refused = await store.AcceptAsync(
            new QueueSubmission
            {
                TenantId = TestPrincipals.AcmeTenant,
                InternalMessageId = "msg_seam_bounce",
                Direction = MailDirection.Outbound,
                TrustedPrincipalId = account,
                MailFrom = string.Empty,
                MimeDigest = new string('a', 64),
                Payload = new byte[] { 1 },
                Recipients = [new RecipientAdmission { Recipient = "recipient@example.com" }],
            },
            CancellationToken.None);

        Assert.False(refused.IsAccepted);
        Assert.Equal(QueueAdmission.RefusedNullSender, refused.Admission);
    }

    [Fact]
    public async Task An_inbound_null_sender_is_accepted_end_to_end()
    {
        // The same rule must not apply in the other direction. A DSN delivered to one of our users
        // arrives with a null sender and no authenticated principal — the listener's inbound path
        // never consults approved identities — and it is ordinary, legitimate mail.
        //
        // This was written to be RED and was, correctly: `Require(submission.MailFrom)` ran
        // unconditionally in `ValidateSubmission` and threw on the empty string for *both*
        // directions, so a routine case died at the last step after being read, assessed and
        // authorised. `queue-` scoped the refusal to outbound and moved it out of the validator,
        // because refusing is a policy outcome and not a construction error. It is green now, and it
        // stays as the regression guard for exactly this case.
        using var host = new TestHost();
        var sink = host.Services.GetRequiredService<ISmtpIngressSink>();

        var decision = await sink.SubmitAsync(
            Submission("msg_seam_dsn", MailDirection.Inbound, "smtp:stylomail",
                mailFrom: string.Empty,
                authentication: new AuthenticationContext
                {
                    ConnectingIp = null,
                    AuthenticatedAccount = null,
                    Results = [],
                    ApprovedSenderIdentities = [],
                    ProvenanceIncomplete = true,
                }),
            CancellationToken.None);

        Assert.Equal(IngressOutcome.Accepted, decision.Outcome);

        // A real queue row, not a mock: the claim is that responsibility transferred.
        var queueId = Assert.IsType<string>(decision.QueueId);
        var item = await host.Services.GetRequiredService<QueueStore>()
            .GetItemAsync(queueId, TestPrincipals.AcmeTenant);

        Assert.NotNull(item);
        Assert.Equal(MailDirection.Inbound, item!.Envelope.Direction);
    }

    private static MailAssessorOptions AssessorOptions() => new()
    {
        // Length matters and nothing else does: the hasher is only ever asked whether it exists.
        ProfileKeyHasher = new ProfileKeyHasher(
            Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef")),
    };

    private static AuthenticationContext Authentication(string account, IReadOnlyList<string> approved) => new()
    {
        ConnectingIp = null,
        AuthenticatedAccount = account,
        Results = [],
        ApprovedSenderIdentities = approved,
        ProvenanceIncomplete = true,
    };

    private static IngressSubmission Submission(
        string internalMessageId,
        MailDirection direction,
        string trustedPrincipalId,
        string mailFrom = "sender@example.com",
        AuthenticationContext? authentication = null) => new()
        {
            InternalMessageId = internalMessageId,
            TenantId = TestPrincipals.AcmeTenant,
            Direction = direction,
            TrustedPrincipalId = trustedPrincipalId,
            MailFrom = mailFrom,
            Recipients = ["recipient@example.com"],
            RawMessage = Encoding.UTF8.GetBytes(TestMessages.SampleMime.Replace("\r\n", "\n").Replace("\n", "\r\n")),
            Authentication = authentication ?? new AuthenticationContext
            {
                ConnectingIp = null,
                AuthenticatedAccount = null,
                Results = [],
                ApprovedSenderIdentities = [],
                ProvenanceIncomplete = true,
            },
            HopCount = 0,
            UntrustedMessageIdHeader = null,
        };
}
