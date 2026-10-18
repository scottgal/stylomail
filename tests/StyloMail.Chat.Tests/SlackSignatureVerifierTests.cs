using System.Security.Cryptography;
using System.Text;
using StyloMail.Chat.Slack;

namespace StyloMail.Chat.Tests;

public sealed class SlackSignatureVerifierTests
{
    private const string Secret = "test-signing-secret-not-a-real-one";
    private const string Body = """{"type":"event_callback","event":{"type":"message"}}""";

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000);

    private static string Sign(string timestamp, string body, string secret = Secret)
    {
        var basestring = $"v0:{timestamp}:{body}";
        var mac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)).ComputeHash(
            Encoding.UTF8.GetBytes(basestring));
        return $"v0={Convert.ToHexString(mac).ToLowerInvariant()}";
    }

    private static string Stamp(DateTimeOffset when) => when.ToUnixTimeSeconds().ToString();

    [Fact]
    public void A_request_signed_with_the_secret_is_accepted()
    {
        var timestamp = Stamp(Now);
        var verdict = SlackSignatureVerifier.Verify(
            Secret, timestamp, Sign(timestamp, Body), Body, Now);

        Assert.Equal(SlackSignatureVerdict.Valid, verdict);
    }

    [Fact]
    public void A_body_that_changed_after_signing_is_refused()
    {
        // The whole point. Anyone can replay a valid signature over different content if the body
        // is not in the signed string, and the body is what gets assessed.
        var timestamp = Stamp(Now);
        var signature = Sign(timestamp, Body);
        var tampered = """{"type":"event_callback","event":{"type":"message","text":"new"}}""";

        var verdict = SlackSignatureVerifier.Verify(Secret, timestamp, signature, tampered, Now);

        Assert.Equal(SlackSignatureVerdict.Mismatch, verdict);
    }

    [Fact]
    public void A_signature_from_another_secret_is_refused()
    {
        var timestamp = Stamp(Now);

        var verdict = SlackSignatureVerifier.Verify(
            Secret, timestamp, Sign(timestamp, Body, "a-different-secret"), Body, Now);

        Assert.Equal(SlackSignatureVerdict.Mismatch, verdict);
    }

    [Fact]
    public void A_request_older_than_the_tolerance_is_refused_even_though_it_is_correctly_signed()
    {
        // A correctly signed request does not expire on its own, so without this a captured request
        // replays forever. Slack's own guidance is five minutes.
        var timestamp = Stamp(Now.AddMinutes(-6));

        var verdict = SlackSignatureVerifier.Verify(
            Secret, timestamp, Sign(timestamp, Body), Body, Now);

        Assert.Equal(SlackSignatureVerdict.StaleRequest, verdict);
    }

    [Fact]
    public void A_request_from_the_future_is_refused()
    {
        // A clock skewed the other way is the same replay in a different direction.
        var timestamp = Stamp(Now.AddMinutes(6));

        var verdict = SlackSignatureVerifier.Verify(
            Secret, timestamp, Sign(timestamp, Body), Body, Now);

        Assert.Equal(SlackSignatureVerdict.StaleRequest, verdict);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_absent_signature_is_named_absent_rather_than_malformed(string? signature)
    {
        // Absent and malformed are different facts, which is why the verdict is not a bool. A caller
        // that sent nothing and a caller that sent rubbish are different incidents, and collapsing
        // them would leave MissingSignature unreachable and the distinction undocumented.
        var timestamp = Stamp(Now);

        var verdict = SlackSignatureVerifier.Verify(Secret, timestamp, signature!, Body, Now);

        Assert.Equal(SlackSignatureVerdict.MissingSignature, verdict);
    }

    [Theory]
    [InlineData("not-a-v0-signature")]
    [InlineData("v0=zzzz")]
    public void A_signature_that_is_not_a_signature_is_refused_rather_than_thrown(string? signature)
    {
        var timestamp = Stamp(Now);

        var verdict = SlackSignatureVerifier.Verify(Secret, timestamp, signature!, Body, Now);

        Assert.Equal(SlackSignatureVerdict.MalformedSignature, verdict);
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(253_402_300_800)]  // One second after 9999-12-31T23:59:59Z, the last instant.
    [InlineData(-62_135_596_801)]  // One second before 0001-01-01T00:00:00Z, the first instant.
    public void A_timestamp_outside_the_calendar_is_refused_rather_than_thrown(long timestamp)
    {
        // long.TryParse accepts any Int64 and DateTimeOffset.FromUnixTimeSeconds throws outside the
        // calendar, so a caller who controls this header could reach an unhandled exception before
        // the signature was ever compared. An unrepresentable instant is not a timestamp at all, so
        // it is refused in the same shape as a value that is not a number.
        var verdict = SlackSignatureVerifier.Verify(
            Secret, timestamp.ToString(), Sign(timestamp.ToString(), Body), Body, Now);

        Assert.Equal(SlackSignatureVerdict.MalformedSignature, verdict);
    }

    [Theory]
    [InlineData("-62135596800")]  // DateTimeOffset.MinValue.ToUnixTimeSeconds()
    [InlineData("253402300799")]  // DateTimeOffset.MaxValue.ToUnixTimeSeconds()
    public void The_boundaries_of_the_calendar_are_representable_and_refused_as_stale(string timestamp)
    {
        // The control for the range check above. These two instants are the outermost ones the type
        // accepts, so they must not be collapsed into MalformedSignature: that would make an age
        // decision out of a representability one. The round trip is what keeps the literals from
        // drifting away from the range the check uses.
        var boundary = long.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(boundary, DateTimeOffset.FromUnixTimeSeconds(boundary).ToUnixTimeSeconds());

        var verdict = SlackSignatureVerifier.Verify(
            Secret, timestamp, Sign(timestamp, Body), Body, Now);

        Assert.Equal(SlackSignatureVerdict.StaleRequest, verdict);
    }
}
