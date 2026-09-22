using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Host.Chat;
using StyloMail.Host.Endpoints;

namespace StyloMail.Host.Tests;

/// <summary>
/// The Slack events intake.
/// </summary>
/// <remarks>
/// <b>The body shapes here are taken from the platform's documentation rather than captured from a
/// workspace.</b> They exercise the endpoint, and they settle nothing about what the platform
/// actually sends. Three facts in the reader are still reasoned rather than measured: where
/// <c>user_team</c> appears, whether <c>bot_id</c> and the install's <c>bot_user_id</c> are the same
/// identifier, and whether the conversation type is on the event at all. A fixture written from
/// documentation would encode the same assumption it is meant to check, so these are labelled and
/// the flags stay until a real payload exists.
/// </remarks>
public sealed class SlackEventsEndpointTests
{
    private const string Secret = "test-signing-secret-not-a-real-one";
    private const string OurBotId = "B0OWN";

    private const string MessageEvent = """
        {"type":"event_callback","event_id":"Ev01","event_time":1760000000,
         "team_id":"T01",
         "event":{"type":"message","channel":"C01","user":"U01","text":"hello",
                  "ts":"1760000000.000100"}}
        """;

    private static string Sign(string timestamp, string body)
    {
        var mac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret)).ComputeHash(
            Encoding.UTF8.GetBytes($"v0:{timestamp}:{body}"));
        return $"v0={Convert.ToHexString(mac).ToLowerInvariant()}";
    }

    private static string Stamp(TestHost host) =>
        host.Services.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeSeconds().ToString();

    private static HttpRequestMessage Signed(TestHost host, string body)
    {
        var timestamp = Stamp(host);
        var request = new HttpRequestMessage(HttpMethod.Post, SlackEventsEndpoints.Route)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.Add(SlackEventsEndpoints.TimestampHeader, timestamp);
        request.Headers.Add(SlackEventsEndpoints.SignatureHeader, Sign(timestamp, body));
        return request;
    }

    private static IChatIntakeStore Intake(TestHost host) =>
        host.Services.GetRequiredService<IChatIntakeStore>();

    [Fact]
    public async Task An_unsigned_request_is_refused()
    {
        // This is the host's second surface that carries no principal, and the signature is the only
        // thing between it and an unauthenticated injection point.
        using var host = new TestHost().WithSlackIngress(Secret, OurBotId);
        using var client = host.Anonymous();

        var response = await client.PostAsync(
            SlackEventsEndpoints.Route,
            new StringContent(MessageEvent, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(Intake(host).Waiting(8));
    }

    [Fact]
    public async Task A_body_changed_after_signing_is_refused()
    {
        // The signature covers the bytes, so a request whose body was swapped underneath a valid
        // signature is the attack the verification exists for.
        using var host = new TestHost().WithSlackIngress(Secret, OurBotId);
        using var client = host.Anonymous();

        var timestamp = Stamp(host);
        var tampered = MessageEvent.Replace("\"hello\"", "\"goodbye\"");

        using var request = new HttpRequestMessage(HttpMethod.Post, SlackEventsEndpoints.Route)
        {
            Content = new StringContent(tampered, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SlackEventsEndpoints.TimestampHeader, timestamp);
        request.Headers.Add(SlackEventsEndpoints.SignatureHeader, Sign(timestamp, MessageEvent));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(Intake(host).Waiting(8));
    }

    [Fact]
    public async Task A_replayed_request_is_refused()
    {
        // A correctly signed request does not expire on its own, so without the window one captured
        // request replays forever. The signature here is genuinely valid for the string it covers:
        // what is wrong is that it was made ten minutes ago.
        using var host = new TestHost().WithSlackIngress(Secret, OurBotId);
        using var client = host.Anonymous();

        var stale = (DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10)).ToUnixTimeSeconds().ToString();

        using var request = new HttpRequestMessage(HttpMethod.Post, SlackEventsEndpoints.Route)
        {
            Content = new StringContent(MessageEvent, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SlackEventsEndpoints.TimestampHeader, stale);
        request.Headers.Add(SlackEventsEndpoints.SignatureHeader, Sign(stale, MessageEvent));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(Intake(host).Waiting(8));
    }

    [Fact]
    public async Task The_challenge_handshake_is_echoed_back()
    {
        // The platform proves the endpoint is ours by sending a challenge once, and the only correct
        // answer is to return it. It must never reach the assessment path: it has no author, no
        // channel and no text.
        using var host = new TestHost().WithSlackIngress(Secret, OurBotId);
        using var client = host.Anonymous();

        const string body = """{"type":"url_verification","challenge":"abc123"}""";

        var response = await client.SendAsync(Signed(host, body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("abc123", json.RootElement.GetProperty("challenge").GetString());

        // A challenge is not a message, so nothing was taken on for assessment.
        Assert.Empty(Intake(host).Waiting(8));
    }

    [Fact]
    public async Task A_verified_message_is_taken_on_for_assessment_before_it_is_answered()
    {
        // The acknowledgement is this path's equivalent of the mail path's 250, so what we answered
        // for has to exist. This asserts the row is there when the answer arrives, which is the
        // property the durable intake exists for.
        using var host = new TestHost().WithSlackIngress(Secret, OurBotId);
        using var client = host.Anonymous();

        var response = await client.SendAsync(Signed(host, MessageEvent));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var waiting = Intake(host).Waiting(8);
        Assert.Equal("Ev01", Assert.Single(waiting).EventId);
    }

    [Fact]
    public async Task A_message_the_deployment_posted_itself_is_acknowledged_and_not_assessed()
    {
        // The loop this prevents needs no attacker: we post, the platform delivers it back, we
        // assess it, and an action posts again. Acknowledged rather than refused, because refusing
        // would have the platform resend something we have already decided not to act on.
        using var host = new TestHost().WithSlackIngress(Secret, OurBotId);
        using var client = host.Anonymous();

        var ours = MessageEvent.Replace(
            "\"user\":\"U01\"",
            $"\"user\":\"U01\",\"bot_id\":\"{OurBotId}\"");

        var response = await client.SendAsync(Signed(host, ours));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(Intake(host).Waiting(8));
    }

    [Fact]
    public async Task Another_bots_message_is_assessed()
    {
        // A workspace whose integration token has been stolen posts phishing through a bot, and that
        // is exactly the inbound traffic this extension exists to catch. Refusing our own output is
        // not the same as refusing bots.
        using var host = new TestHost().WithSlackIngress(Secret, OurBotId);
        using var client = host.Anonymous();

        var theirs = MessageEvent.Replace("\"user\":\"U01\"", "\"user\":\"U01\",\"bot_id\":\"B0OTHER\"");

        var response = await client.SendAsync(Signed(host, theirs));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Ev01", Assert.Single(Intake(host).Waiting(8)).EventId);
    }

    [Fact]
    public async Task A_retried_event_is_acknowledged_and_taken_on_once()
    {
        // A retried delivery is the same message. Taking it on twice would double every observation
        // the behavioural engine counts, which is a rate change it cannot tell from real traffic.
        using var host = new TestHost().WithSlackIngress(Secret, OurBotId);
        using var client = host.Anonymous();

        var first = await client.SendAsync(Signed(host, MessageEvent));
        var retry = await client.SendAsync(Signed(host, MessageEvent));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Single(Intake(host).Waiting(8));
    }

    [Fact]
    public async Task An_event_not_for_us_is_acknowledged_rather_than_refused()
    {
        // An edit arrives as a nested event describing a message already read. Acknowledged, because
        // refusing would make the platform retry something we have decided not to act on, and a
        // retry is traffic we would see again on every attempt.
        using var host = new TestHost().WithSlackIngress(Secret, OurBotId);
        using var client = host.Anonymous();

        const string edited = """
            {"type":"event_callback","event_id":"Ev09","team_id":"T01",
             "event":{"type":"message","subtype":"message_changed","channel":"C01"}}
            """;

        var response = await client.SendAsync(Signed(host, edited));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(Intake(host).Waiting(8));
    }
}
