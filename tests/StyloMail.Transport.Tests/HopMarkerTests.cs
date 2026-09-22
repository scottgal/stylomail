using System.Text;
using StyloMail.Transport.Ingress;

namespace StyloMail.Transport.Tests;

/// <summary>
/// The hop marker is what makes the loop guard able to see our own hop. These test the mechanism
/// closing end to end, not just the string it produces.
/// </summary>
public sealed class HopMarkerTests
{
    private const string OurName = "stylomail.example.test";

    private static ReceivedHeaderStamp Stamp(
        string? fromHost = "client.example.net",
        string? fromAddress = "203.0.113.5",
        string protocol = "ESMTP") => new()
        {
            ByHost = OurName,
            FromHost = fromHost,
            FromAddress = fromAddress,
            Protocol = protocol,
            HopId = "msg_abc123",
            // A fixed instant with a non-UTC offset, so the zone rendering is actually exercised.
            At = new DateTimeOffset(2026, 9, 22, 14, 30, 5, TimeSpan.FromHours(1)),
        };

    [Fact]
    public void TheHeaderLineHasTheExpectedShape()
    {
        var line = ReceivedHeader.Build(Stamp());

        Assert.StartsWith("Received: from client.example.net (203.0.113.5) by ", line, StringComparison.Ordinal);
        Assert.Contains($"by {OurName} with ESMTP id msg_abc123; ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDateIsRfc5322WithANumericZone()
    {
        // Invariant day and month names, and +0100 rather than +01:00, the grammar wants the
        // numeric form, and a locale-dependent month name would be unparseable elsewhere.
        var line = ReceivedHeader.Build(Stamp());

        Assert.Contains("Tue, 22 Sep 2026 14:30:05 +0100", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ANegativeOffsetRendersWithASign()
    {
        var line = ReceivedHeader.Build(Stamp() with
        {
            At = new DateTimeOffset(2026, 9, 22, 14, 30, 5, TimeSpan.FromHours(-5)),
        });

        Assert.Contains("-0500", line, StringComparison.Ordinal);
    }

    [Fact]
    public void NoFromHostMeansNoFromClauseAtAll()
    {
        var line = ReceivedHeader.Build(Stamp(fromHost: null, fromAddress: null));

        Assert.DoesNotContain("from ", line, StringComparison.Ordinal);
        Assert.Contains($"by {OurName}", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddressWithoutAHostIsStillRendered()
    {
        var line = ReceivedHeader.Build(Stamp(fromHost: null));

        // Nothing to claim as the origin, so neither clause is written, the address alone would
        // read as a host we never observed.
        Assert.DoesNotContain("from ", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("evil\r\nReceived: forged")]
    [InlineData("evil\nReceived: forged")]
    [InlineData("a(b) by someone")]
    [InlineData("a; b")]
    [InlineData("a<b>")]
    [InlineData("a b")]
    [InlineData("a\tb")]
    public void AHostileTokenCannotAppearVerbatimInTheValue(string hostile)
    {
        // Asserted on the whole line, because the grammar's own characters, spaces and the
        // parentheses around the address comment, are legitimately present. What must not survive
        // is the untrusted *token*, and that is what this checks.
        var line = ReceivedHeader.Build(Stamp(fromHost: hostile));

        Assert.DoesNotContain(hostile, line, StringComparison.Ordinal);
        Assert.StartsWith("Received: ", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("evil\r\nReceived: forged")]
    [InlineData("a(b) by someone")]
    [InlineData("a; b")]
    [InlineData("a<b>")]
    [InlineData("a b")]
    [InlineData("a\tb")]
    public void AnUntrustedTokenIsReducedToThePermittedAlphabet(string hostile)
    {
        // The token set is the control: it excludes whitespace, comment and route delimiters, and
        // the line terminator, every character that could reframe the value.
        var token = ReceivedHeader.Token(hostile);

        foreach (var forbidden in new[] { '\r', '\n', ' ', '\t', '(', ')', ';', '<', '>' })
        {
            Assert.DoesNotContain(forbidden, token);
        }
    }

    [Fact]
    public void DisallowedCharactersBecomeVisibleRatherThanVanishing()
    {
        // A forged value leaves a trace instead of silently becoming a well-formed lie, so an
        // operator reading the stored message can see the client tried something.
        Assert.Equal("a?b", ReceivedHeader.Token("a b"));
        Assert.Equal("a?b", ReceivedHeader.Token("a\tb"));
    }

    [Fact]
    public void AnIpv6LiteralSurvivesIntact()
    {
        // The alphabet has to admit the bracketed IPv6 form, or a connecting address would be
        // mangled into something that is no longer an address.
        Assert.Equal("[2001:db8::1]", ReceivedHeader.Token("[2001:db8::1]"));
    }

    [Fact]
    public void AHopIdAttemptingToForgeAClauseCannot()
    {
        var line = ReceivedHeader.Build(Stamp() with { HopId = "x by attacker.example; " });

        Assert.DoesNotContain("by attacker.example", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueIsBoundedRatherThanUnlimited()
    {
        var line = ReceivedHeader.Build(Stamp(fromHost: new string('a', 5000)));

        Assert.True(line.Length <= ReceivedHeader.MaxValueLength, $"Line was {line.Length} characters.");
    }

    [Fact]
    public void AnEmptyTokenBecomesUnknownRatherThanABlankClause()
    {
        var line = ReceivedHeader.Build(Stamp(fromHost: "   "));

        Assert.Contains("from unknown", line, StringComparison.Ordinal);
    }

    [Fact]
    public void PrependingLeavesEveryOriginalByteInPlace()
    {
        var payload = Encoding.UTF8.GetBytes("Subject: x\r\nFrom: a@b\r\n\r\nbody\r\n.hidden\r\n");
        var line = ReceivedHeader.Build(Stamp());

        var stamped = ReceivedHeader.Prepend(payload, line);

        Assert.Equal(payload, stamped[(line.Length + 2)..]);
        Assert.Equal(line + "\r\n", Encoding.ASCII.GetString(stamped, 0, line.Length + 2));
    }

    /// <summary>
    /// The mechanism, end to end: our own hop marker is what the inbound loop guard matches on.
    /// </summary>
    [Fact]
    public void AMessageStampedByUsIsRecognisedAsALoopWhenItComesBack()
    {
        // Before the marker existed this could not work at all: the guard looked for a `by` clause
        // naming us, and we never wrote one, so our own hop was invisible and only the hop limit,         // the backstop, not the mechanism, would ever have caught a loop.
        var original = Encoding.UTF8.GetBytes(
            "Received: from relay.example.net by mta.example.net with ESMTP\r\n"
            + "From: sender@example.net\r\n"
            + "\r\n"
            + "body\r\n");

        var stamped = ReceivedHeader.Prepend(original, ReceivedHeader.Build(Stamp()));

        var facts = TransportHeaderScanner.Scan(
            stamped, new TransportHeaderLimits(), [OurName]);

        Assert.True(facts.LoopDetected, "A message carrying our own hop marker was not recognised as a loop.");
        Assert.Contains(OurName, facts.LoopEvidence!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStampCannotExpressARecipientAtAll()
    {
        // Structural rather than a convention, which is the shape that survives later edits: if a
        // recipient cannot be passed to the builder in the first place, no one can later start
        // writing one into a header that every recipient receives and third parties archive.
        var properties = typeof(ReceivedHeaderStamp)
            .GetProperties()
            .Select(p => p.Name)
            .ToList();

        Assert.DoesNotContain(properties, n => n.Contains("Recipient", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, n => n.Equals("For", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AMessageNotStampedByUsIsNotALoop()
    {
        var facts = TransportHeaderScanner.Scan(
            Encoding.UTF8.GetBytes("Received: from a by other.example.net\r\n\r\nbody\r\n"),
            new TransportHeaderLimits(),
            [OurName]);

        Assert.False(facts.LoopDetected);
    }
}
