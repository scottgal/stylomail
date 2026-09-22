namespace StyloMail.Host.Hosting;

/// <summary>
/// Configuration for the Slack events endpoint.
/// </summary>
/// <remarks>
/// <para>
/// <b>Disabled unless an operator turns it on and supplies an identity and a signing secret.</b>
/// The route is mapped only when <see cref="Enabled"/>, following the Cloudflare ingress and the
/// session endpoints: a route that exists but always refuses invites someone to "fix" it with a
/// configuration change, whereas its absence says plainly that this deployment has no such intake.
/// </para>
/// <para>
/// <b>Nothing here may hold a secret value in source.</b> The signing secret is read from
/// configuration, which in a deployment means a secret store, and it is never logged, echoed or
/// stored.
/// </para>
/// </remarks>
public sealed class SlackIngressOptions
{
    public const string SectionName = "StyloMail:Slack";

    public bool Enabled { get; set; }

    /// <summary>
    /// The app's signing secret, used to prove a request came from the platform.
    /// </summary>
    /// <remarks>Never printed, never logged, compared in constant time and discarded.</remarks>
    public string? SigningSecret { get; set; }

    /// <summary>
    /// The deployment's own bot id, as the platform reports it on a message.
    /// </summary>
    /// <remarks>
    /// See <see cref="OwnBotUserId"/>: which of the two the platform carries on a given post is a
    /// fact about its payloads rather than something to assume, so both are configured and either
    /// match identifies our own output.
    /// </remarks>
    public string? OwnBotId { get; set; }

    /// <summary>
    /// The deployment's own bot user id, as the install reports it.
    /// </summary>
    /// <remarks>
    /// Not listed in <c>docs/chat-pipeline-design.md</c> as a separate setting on purpose. An
    /// administrator does not want two settings either side of a distinction the system can make
    /// for itself, and the platform reports both forms, so the administrator supplies whichever
    /// their install shows them.
    /// </remarks>
    public string? OwnBotUserId { get; set; }

    /// <summary>
    /// The tenant a chat event is assessed under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This surface carries no principal, so the tenant is configuration rather than a claim.</b>
    /// The platform tells us which workspace an event belongs to, and nothing it sends is an
    /// assertion about which of our tenants that workspace is: taking it from the payload would let
    /// a caller choose whose ledger their message lands in. The Cloudflare ingress resolves this the
    /// same way, for the same reason.
    /// </para>
    /// <para>
    /// The default matches that ingress deliberately, so a deployment running both does not end up
    /// with two names for one inbound tenant.
    /// </para>
    /// </remarks>
    public string InboundTenantId { get; set; } = "inbound";

    /// <summary>
    /// How many verified events may be waiting for assessment before the endpoint starts refusing.
    /// </summary>
    /// <remarks>
    /// <b>A bound rather than tuning.</b> The endpoint answers before the assessment runs, so
    /// without a bound an unbounded amount of work would be accepted from a caller and held in
    /// memory. Past this the endpoint refuses and the platform retries, which is honest backpressure
    /// rather than a queue that grows.
    /// </remarks>
    public int PendingCapacity { get; set; } = 512;

    /// <summary>
    /// The deployment's own identity, or <c>None</c> when it has not been configured.
    /// </summary>
    /// <remarks>
    /// Returned as a value rather than validated here, so the degenerate case is nameable and
    /// testable. <see cref="Validate"/> is what refuses it.
    /// </remarks>
    public StyloMail.Chat.Slack.SlackBotIdentity Identity() => new()
    {
        BotId = OwnBotId,
        BotUserId = OwnBotUserId,
    };

    /// <summary>
    /// Rejects a configuration that cannot be run safely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An ingress with no bot identity is a configuration error and the host refuses to start
    /// with it.</b> Without one, every message the deployment's own app posts comes back through
    /// this endpoint and is assessed, and an assessment can post again: that is a self-sustaining
    /// loop, and it needs no attacker. A missing value that degrades into a permissive default works
    /// perfectly in tests and is wrong in production, which is why this project already fails loudly
    /// on a missing secret for the same reason.
    /// </para>
    /// <para>
    /// A disabled endpoint is not validated at all: a deployment with no Slack intake has no
    /// identity to configure, and refusing to start it would be enforcing a requirement for a
    /// feature it does not use.
    /// </para>
    /// </remarks>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SigningSecret))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(SigningSecret)} must be configured when the Slack events "
                + "endpoint is enabled. Without it every request claiming to come from the platform "
                + "would be unverifiable.");
        }

        if (string.IsNullOrWhiteSpace(OwnBotId) && string.IsNullOrWhiteSpace(OwnBotUserId))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(OwnBotId)} or {nameof(OwnBotUserId)} must be configured "
                + "when the Slack events endpoint is enabled. Without one the deployment cannot "
                + "recognise its own posts, so it would assess its own output and could act on it, "
                + "which is a loop that needs no attacker.");
        }

        if (string.IsNullOrWhiteSpace(InboundTenantId))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(InboundTenantId)} must be configured when the Slack events "
                + "endpoint is enabled, because an assessment has to name the tenant it is for and "
                + "this surface carries no principal to take it from.");
        }

        if (PendingCapacity < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(PendingCapacity)} must be at least 1. A bound of zero would "
                + "refuse every event rather than bounding the work held.");
        }
    }
}
