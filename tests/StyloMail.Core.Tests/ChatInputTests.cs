using System.Reflection;
using System.Runtime.CompilerServices;
using StyloMail.Core;

namespace StyloMail.Core.Tests;

/// <summary>
/// The chat channel's analysis input, the sibling of <see cref="MailAnalysisInput"/>.
/// </summary>
/// <remarks>
/// Input is per channel and output is shared, so this record exists rather than a widened
/// <see cref="MailAnalysisInput"/>: a chat message has no envelope and no DKIM, and a mail record
/// carrying three fields that are always null would be a record that lies about what it holds.
/// </remarks>
public sealed class ChatInputTests
{
    [Fact]
    public void The_chat_input_cannot_be_built_without_stating_its_channel()
    {
        const string propertyName = nameof(ChatAnalysisInput.Channel);
        var property = typeof(ChatAnalysisInput).GetProperty(propertyName);

        Assert.NotNull(property);
        Assert.True(
            property.GetCustomAttribute<RequiredMemberAttribute>() is not null,
            $"{propertyName} must be required so a call site cannot omit it.");
    }

    [Fact]
    public void The_chat_input_carries_links_as_an_observation_not_a_judgement()
    {
        // Structural decision 8. LinkObservation is what the message contained; LinkFinding is what
        // the analysis made of it. The connector produces the first and the analysis consumes it, so
        // a reader that produced the second would be judging, and the split would be lost even
        // though both types still existed.
        var property = typeof(ChatAnalysisInput).GetProperty(nameof(ChatAnalysisInput.Links));

        Assert.NotNull(property);
        Assert.Equal(
            typeof(IReadOnlyList<LinkObservation>),
            property.PropertyType);
    }

    [Fact]
    public void A_membership_record_says_which_bot_posted_and_derives_that_it_was_one()
    {
        // Bounded by its shape rather than by a stated limit: a fixed set of named facts cannot grow
        // without someone naming a new one, which a key/value map can.
        var bot = new ChatMembershipFacts { AuthorId = "U01", BotId = "B01" };
        var person = new ChatMembershipFacts { AuthorId = "U02" };

        Assert.True(bot.IsBot);
        Assert.Equal("B01", bot.BotId);

        // A person is not a bot by omission rather than by a stored false, so the two cannot drift.
        Assert.False(person.IsBot);
        Assert.Null(person.BotId);
    }

    [Fact]
    public void The_chat_input_is_the_sibling_of_the_mail_input_and_not_a_replacement()
    {
        // Both exist and neither is a subtype of the other: the email path still states an envelope
        // and an authentication context, and chat states neither. Pinned so a later edit cannot
        // collapse them into one record with half its fields always null.
        Assert.Null(typeof(ChatAnalysisInput).GetProperty("Envelope"));
        Assert.Null(typeof(ChatAnalysisInput).GetProperty("Authentication"));
        Assert.NotNull(typeof(MailAnalysisInput).GetProperty("Envelope"));
    }
}
