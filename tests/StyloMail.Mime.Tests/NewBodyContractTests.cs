using StyloMail.Mime;

namespace StyloMail.Mime.Tests;

public sealed class NewBodyContractTests
{
    [Theory]
    [InlineData("> Only quoted history", "", "> Only quoted history", "quote-prefix")]
    [InlineData("Ordinary new body", "Ordinary new body", null, "none")]
    [InlineData("New preamble\n\n> Previous message", "New preamble", "> Previous message", "quote-prefix")]
    public void BodyText_contains_only_identified_new_text(string body, string expectedNew,
        string? expectedQuoted, string expectedMarker)
    {
        var result = new BoundedMimeMessageAnalyzer().Analyze(
            FixtureMessage.FromBytes(SyntheticMessage.WithBody(body)));

        Assert.True(result.IsAnalysable);
        Assert.Equal(expectedNew, result.Message!.BodyText);
        Assert.Equal(expectedQuoted, result.Message.QuotedText);
        Assert.Equal(expectedMarker, result.Signal(MimeSignals.QuotedHistory).Attribute("marker"));
    }
}
