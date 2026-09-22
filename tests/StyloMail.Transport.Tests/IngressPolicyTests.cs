using System.Text;
using StyloMail.Transport.Ingress;

namespace StyloMail.Transport.Tests;

/// <summary>
/// The two pure pieces of the ingress boundary, tested directly for the edge cases that decide
/// whether inbound mail is refused.
/// </summary>
public sealed class IngressPolicyTests
{
    // ---- Recipient domain policy ---------------------------------------------------------------

    [Theory]
    [InlineData("user@example.test", true)]
    [InlineData("USER@EXAMPLE.TEST", true)]
    [InlineData("user@sub.example.test", false)]
    [InlineData("user@example.test.evil.com", false)]
    [InlineData("user@notexample.test", false)]
    [InlineData("user@evil-example.test", false)]
    [InlineData("no-at-sign", false)]
    [InlineData("user@", false)]
    [InlineData("", false)]
    public void RecipientDomainMatchingIsExact(string recipient, bool expected)
    {
        // Suffix matching is the trap: it would silently authorise evil-example.test for a policy
        // listing example.test, and an attacker only has to register the neighbour.
        var policy = new RecipientDomainPolicy(["example.test"]);

        Assert.Equal(expected, policy.Allows(recipient));
    }

    [Fact]
    public void AnEmptyPolicyAcceptsNothing()
    {
        // A missing configuration must not read as a universal open relay.
        Assert.False(RecipientDomainPolicy.None.IsConfigured);
        Assert.False(RecipientDomainPolicy.None.Allows("user@example.test"));
    }

    [Fact]
    public void AConfiguredDomainListIsCaseInsensitiveAndToleratesLeadingAtAndDots()
    {
        var policy = new RecipientDomainPolicy(["@Example.Test.", ""]);

        Assert.True(policy.IsConfigured);
        Assert.True(policy.Allows("user@example.test"));
        Assert.True(policy.Allows("user@EXAMPLE.test"));
    }

    [Fact]
    public void AQuotedLocalPartContainingAnAtSignStillResolvesTheDomain()
    {
        // The last '@' delimits the domain, not the first.
        var policy = new RecipientDomainPolicy(["example.test"]);

        Assert.True(policy.Allows("\"odd@local\"@example.test"));
    }

    // ---- Transport header scanning -------------------------------------------------------------

    private static TransportHeaderFacts Scan(string message, params string[] identities) =>
        TransportHeaderScanner.Scan(
            Encoding.UTF8.GetBytes(message),
            new TransportHeaderLimits(),
            identities);

    [Fact]
    public void ReceivedHeadersAreCounted()
    {
        var facts = Scan(
            "Received: from a by b\r\n"
            + "Received: from c by d\r\n"
            + "Subject: x\r\n"
            + "\r\n"
            + "body\r\n");

        Assert.Equal(2, facts.ReceivedCount);
        Assert.Null(facts.BoundExceeded);
        Assert.False(facts.LoopDetected);
    }

    [Fact]
    public void MessageIdIsCapturedAndIsNotTreatedAsAKey()
    {
        var facts = Scan("Message-ID: <abc@example.test>\r\nSubject: x\r\n\r\nbody\r\n");

        Assert.Equal("<abc@example.test>", facts.UntrustedMessageId);
    }

    [Fact]
    public void AFoldedReceivedHeaderIsCountedOnceAndUnfolded()
    {
        var facts = Scan(
            "Received: from a.example.net\r\n\tby b.example.net with ESMTP\r\n"
            + "\r\n"
            + "body\r\n",
            "b.example.net");

        Assert.Equal(1, facts.ReceivedCount);
        Assert.True(facts.LoopDetected);
    }

    [Fact]
    public void AByClauseNamingUsIsALoop()
    {
        var facts = Scan(
            "Received: from relay.example.net by stylomail.example.test with ESMTP id x\r\n\r\nbody\r\n",
            "stylomail.example.test");

        Assert.True(facts.LoopDetected);
        Assert.NotNull(facts.LoopEvidence);
    }

    [Fact]
    public void AForClauseNamingUsIsNotALoop()
    {
        // Ordinary inbound mail carries our own domain in a "for" clause on nearly every message.
        // Matching the whole value instead of the "by" clause would refuse most of the internet.
        var facts = Scan(
            "Received: from relay.example.net by mta.example.net for <user@stylomail.example.test>\r\n\r\nbody\r\n",
            "stylomail.example.test");

        Assert.False(facts.LoopDetected);
    }

    [Fact]
    public void AByClauseWithTrailingPunctuationStillMatches()
    {
        var facts = Scan(
            "Received: from a by stylomail.example.test;\r\n\r\nbody\r\n",
            "stylomail.example.test");

        Assert.True(facts.LoopDetected);
    }

    [Fact]
    public void NoLocalIdentitiesMeansNoLoopIsEverDeclared()
    {
        var facts = Scan("Received: from a by anything.example.test\r\n\r\nbody\r\n");

        Assert.False(facts.LoopDetected);
    }

    [Fact]
    public void AnOversizeHeaderBlockIsRefusedRatherThanPartlyRead()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 20; i++)
        {
            builder.Append("X-Pad-").Append(i).Append(": ").Append(new string('p', 200)).Append("\r\n");
        }

        builder.Append("\r\nbody\r\n");

        var facts = TransportHeaderScanner.Scan(
            Encoding.UTF8.GetBytes(builder.ToString()),
            new TransportHeaderLimits { MaxHeaderBytes = 512 },
            []);

        Assert.Equal("header-bytes", facts.BoundExceeded);
    }

    [Fact]
    public void TooManyHeaderFieldsIsRefused()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 30; i++)
        {
            builder.Append("X-H").Append(i).Append(": v\r\n");
        }

        builder.Append("\r\nbody\r\n");

        var facts = TransportHeaderScanner.Scan(
            Encoding.UTF8.GetBytes(builder.ToString()),
            new TransportHeaderLimits { MaxHeaderCount = 10 },
            []);

        Assert.Equal("header-count", facts.BoundExceeded);
    }

    [Fact]
    public void ScanningNeverThrowsForHostileInput()
    {
        // The hop guard is most needed on a message designed to break parsers.
        var hostile = new[]
        {
            string.Empty,
            "\r\n\r\n",
            "no colon here\r\n\r\nbody",
            "Received:\r\n\r\n",
            "\r\n",
            ":::\r\n\r\n",
            "Received: from a by b\r\n\r\n",
        };

        foreach (var message in hostile)
        {
            var facts = Scan(message);
            Assert.True(facts.ReceivedCount >= 0);
        }
    }
}
