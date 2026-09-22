namespace StyloMail.Core.Tests;

/// <summary>
/// Pins the null-sender predicate at the single source, independent of any caller.
/// </summary>
/// <remarks>
/// <para>
/// This predicate previously existed in two components as mirrored copies. Within an hour of being
/// written twice they had silently diverged: one accepted <c>"&lt; &gt;"</c> (brackets with a blank
/// inside) as the null sender and the other did not. Consolidating to one source is what surfaced it
///, under the "ping me if it changes" convention it would have sat there until someone passed a
/// <c>"&lt; &gt;"</c> and got one outcome from the assessor and another from the queue.
/// </para>
/// <para>
/// These cases therefore pin the <em>rule</em>, not any caller's use of it: caller-side theories
/// exercise a call site, and a divergence in a copy is exactly what a call-site test cannot see.
/// </para>
/// </remarks>
public sealed class SenderAddressesTests
{
    [Theory]
    [InlineData("")]                    // the value that travels between components
    [InlineData("<>")]                  // RFC 5321 wire notation, tolerated at the boundary
    [InlineData("  ")]                  // whitespace only
    [InlineData(" <> ")]                // wire form surrounded by whitespace
    [InlineData("\t")]
    public void Recognises_the_null_sender(string address)
    {
        Assert.True(SenderAddresses.IsNullSender(address));
    }

    [Theory]
    [InlineData("< >")]                 // malformed: brackets with a blank inside, NOT the null sender
    [InlineData(" < > ")]               // ...and trimming does not rescue it; the interior blank stays
    [InlineData("<>x")]
    [InlineData("x<>")]
    [InlineData("null")]
    [InlineData("@")]
    [InlineData("a@b.example")]
    public void Does_not_recognise_a_non_null_sender(string address)
    {
        Assert.False(SenderAddresses.IsNullSender(address));
    }

    /// <summary>
    /// A null reference is not the null sender. They are different statements, "no address supplied
    /// at all" versus "the empty reverse-path", and collapsing them would make an absent field look
    /// like a DSN.
    /// </summary>
    [Fact]
    public void A_missing_address_is_not_the_null_sender()
    {
        Assert.False(SenderAddresses.IsNullSender(null));
    }

    [Fact]
    public void The_travelling_value_is_the_empty_string_not_the_wire_form()
    {
        // Recorded as an assertion because the two components disagreed about it once already.
        Assert.Equal(string.Empty, SenderAddresses.NullSenderValue);
        Assert.Equal("<>", SenderAddresses.NullSenderWireForm);
    }
}
