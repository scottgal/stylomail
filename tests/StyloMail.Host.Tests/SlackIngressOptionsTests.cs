using StyloMail.Host.Hosting;

namespace StyloMail.Host.Tests;

/// <summary>
/// What the Slack events endpoint refuses to start with.
/// </summary>
/// <remarks>
/// A missing value that degrades into a permissive default works perfectly in tests and is wrong in
/// production. The identity is the sharpest case of that here: with none configured the deployment
/// cannot recognise its own posts, so it assesses its own output, and an assessment can post again.
/// </remarks>
public sealed class SlackIngressOptionsTests
{
    private static SlackIngressOptions Enabled() => new()
    {
        Enabled = true,
        SigningSecret = "configured-in-a-test-only",
        OwnBotId = "B0OWN",
    };

    [Fact]
    public void An_enabled_endpoint_with_no_identity_refuses_to_start()
    {
        var options = new SlackIngressOptions { Enabled = true, SigningSecret = "configured" };

        var thrown = Assert.Throws<InvalidOperationException>(options.Validate);

        // The message names the setting, because an operator reading a startup failure needs to know
        // what to set rather than that something was wrong.
        Assert.Contains(nameof(SlackIngressOptions.OwnBotId), thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Either_identity_form_alone_is_enough()
    {
        // Which identifier the platform carries on a given post is a fact about its payloads rather
        // than something to assume, so the administrator supplies whichever their install shows them
        // and either one identifies our own output.
        var byBotId = Enabled();
        var byUserId = new SlackIngressOptions
        {
            Enabled = true,
            SigningSecret = "configured",
            OwnBotUserId = "U0OWN",
        };

        byBotId.Validate();
        byUserId.Validate();
    }

    [Fact]
    public void An_enabled_endpoint_with_no_signing_secret_refuses_to_start()
    {
        var options = new SlackIngressOptions { Enabled = true, OwnBotId = "B0OWN" };

        var thrown = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("SigningSecret", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_disabled_endpoint_is_not_validated_at_all()
    {
        // A deployment with no Slack intake has no identity and no secret to configure, and refusing
        // to start it would enforce a requirement for a feature it does not use.
        new SlackIngressOptions().Validate();
    }

    [Fact]
    public void A_zero_capacity_is_refused_rather_than_refusing_every_event()
    {
        var options = Enabled();
        options.PendingCapacity = 0;

        var thrown = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("PendingCapacity", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_configured_identifiers_are_what_recognises_our_own_posts()
    {
        var identity = Enabled().Identity();

        Assert.True(identity.IsOurOwnPost("B0OWN", null));
        Assert.False(identity.IsOurOwnPost("B0OTHER", "U0OTHER"));
    }
}
