using System.Reflection;
using System.Runtime.CompilerServices;
using StyloMail.Core;

namespace StyloMail.Core.Tests;

public sealed class ChannelContractTests
{
    [Fact]
    public void The_email_context_names_the_email_channel_and_nothing_else()
    {
        // The email path supplies this value at every existing call site, so it has to say email
        // and it has to say nothing about a workspace, a channel or a thread. A populated field
        // here would be an invented fact about a message that has no such thing.
        Assert.Equal(ChannelKind.Email, ChannelContext.Email.Kind);
        Assert.Null(ChannelContext.Email.WorkspaceId);
        Assert.Null(ChannelContext.Email.ChannelId);
        Assert.Null(ChannelContext.Email.ThreadId);
    }

    [Fact]
    public void The_channel_kinds_are_exactly_the_ones_this_system_speaks_for()
    {
        // A kind added without a decision about its delivery timing is a channel nobody has
        // thought about, so this fails rather than silently growing.
        string[] expectedKinds = ["Email", "Slack", "Discord"];
        Assert.Equal(expectedKinds, Enum.GetNames<ChannelKind>());
    }

    [Fact]
    public void Delivery_timing_has_exactly_two_answers()
    {
        // PreAcceptance means we were in the delivery path. PostDelivery means the platform had
        // already delivered it and every action available is post-hoc. There is no third answer,
        // and "unknown" is not one of them: an assessment that cannot say which it was has not
        // established something it needs to know.
        string[] expectedTimings = ["PreAcceptance", "PostDelivery"];
        Assert.Equal(expectedTimings, Enum.GetNames<DeliveryTiming>());
    }

    [Fact]
    public void The_assessment_cannot_be_built_without_stating_its_delivery_timing()
    {
        // A required member is the only version of this that cannot be got wrong by omission. A
        // default would compile and would claim PreAcceptance for a chat message, which is the
        // false statement that a console reading a post-hoc hold as a prevention would believe.
        const string propertyName = nameof(MailAssessment.DeliveryTiming);
        var property = typeof(MailAssessment).GetProperty(propertyName);

        Assert.NotNull(property);
        Assert.True(
            property.GetCustomAttribute<RequiredMemberAttribute>() is not null,
            $"{propertyName} must be required so a call site cannot omit it.");
    }

    [Fact]
    public void The_analysis_input_cannot_be_built_without_stating_its_channel()
    {
        // Same rule one layer down: the adapter that produced this input states the channel, so a
        // chat adapter cannot inherit an email default by not thinking about it.
        const string propertyName = nameof(MailAnalysisInput.Channel);
        var property = typeof(MailAnalysisInput).GetProperty(propertyName);

        Assert.NotNull(property);
        Assert.True(
            property.GetCustomAttribute<RequiredMemberAttribute>() is not null,
            $"{propertyName} must be required so a call site cannot omit it.");
    }

    [Fact]
    public void The_assessment_cannot_be_built_without_stating_its_channel()
    {
        // The channel used to be reachable only from the input, which meant a decision could not say
        // which channel it was about without a reader going and finding the input. A decision that
        // cannot name its own channel leaves a console showing a chat decision and an email decision
        // the same way, and infers the difference from something else.
        const string propertyName = nameof(MailAssessment.Channel);
        var property = typeof(MailAssessment).GetProperty(propertyName);

        Assert.NotNull(property);
        Assert.True(
            property.GetCustomAttribute<RequiredMemberAttribute>() is not null,
            $"{propertyName} must be required so a call site cannot omit it.");
    }
}
