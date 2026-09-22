# Slack ingress: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Slack app's events arrive, are proven to have come from Slack, and become a normalised `ChatMessage` that the rest of the system can assess. Nothing is assessed yet.

**Architecture:** One new project, `src/StyloMail.Chat`, with no dependency on the assessment pipeline. It owns exactly four things: signature verification, event deduplication, normalisation, and the bounded shapes they produce. It makes no judgement about content.

**Tech Stack:** .NET 10, xUnit, `SlackNet` for the wire types.

**Spec:** `docs/chat-channels-design.md`, the "Chat connector" section, and `.styloagent/spec.md` section 13.

**Scope:** plan 2a of 3 for the chat extension. **Plan 2b** is the pipeline hookup, where a channel-neutral analysis input is decided and the semantic classifier learns to read one. That decision is deliberately not made here, and this plan is written so that it does not have to be: everything in it ends at `ChatMessage`.

## Global Constraints

- `dotnet` is **not on PATH**: `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`
- The solution is `StyloMail.slnx`. **Add the new project to it.**
- Analyzers run as **errors**. A warning is a failed build.
- **Never use an em-dash** in code, comments, documentation or commit messages. Use a colon or a full stop.
- Every commit message ends with the `Co-Authored-By: Claude <noreply@anthropic.com>` trailer.
- **Do not run `git add` or `git commit`.** Leave the work in the tree and report. Never `--amend`, never `reset`.
- **Never print, log or commit a signing secret or a token.** Generate test secrets in the test.

---

### Task 1: A request that claims to be from Slack, and the proof that it is

**Files:**
- Create: `src/StyloMail.Chat/StyloMail.Chat.csproj`
- Create: `src/StyloMail.Chat/Slack/SlackSignatureVerifier.cs`
- Create: `tests/StyloMail.Chat.Tests/SlackSignatureVerifierTests.cs`
- Modify: `StyloMail.slnx`

**Interfaces:**
- Produces: `SlackSignatureVerifier.Verify(string signingSecret, string timestamp, string signature, string rawBody, DateTimeOffset now)` returning `SlackSignatureVerdict` (`Valid`, `MissingSignature`, `MalformedSignature`, `StaleRequest`, `Mismatch`).

- [ ] **Step 1: Create the project**

```bash
cd /Users/scottgalloway/RiderProjects/stylomail
dotnet new classlib -o src/StyloMail.Chat
dotnet new xunit -o tests/StyloMail.Chat.Tests
dotnet sln StyloMail.slnx add src/StyloMail.Chat/StyloMail.Chat.csproj
dotnet sln StyloMail.slnx add tests/StyloMail.Chat.Tests/StyloMail.Chat.Tests.csproj
dotnet add src/StyloMail.Chat package SlackNet
dotnet add tests/StyloMail.Chat.Tests reference src/StyloMail.Chat
```

Delete the generated `Class1.cs` and `UnitTest1.cs`. **Record the SlackNet version `dotnet add` resolved in your report.** If `SlackNet` does not resolve, stop and report rather than picking a different library: the choice of client was made deliberately and changing it is a decision, not a workaround.

- [ ] **Step 2: Write the failing test**

Create `tests/StyloMail.Chat.Tests/SlackSignatureVerifierTests.cs`:

```csharp
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
    [InlineData("not-a-v0-signature")]
    [InlineData("v0=zzzz")]
    public void A_signature_that_is_not_a_signature_is_refused_rather_than_thrown(string? signature)
    {
        var timestamp = Stamp(Now);

        var verdict = SlackSignatureVerifier.Verify(Secret, timestamp, signature!, Body, Now);

        Assert.Equal(SlackSignatureVerdict.MalformedSignature, verdict);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/StyloMail.Chat.Tests/StyloMail.Chat.Tests.csproj`
Expected: FAIL, with `The type or namespace name 'SlackSignatureVerifier' could not be found`.

- [ ] **Step 4: Write the verifier**

Create `src/StyloMail.Chat/Slack/SlackSignatureVerifier.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace StyloMail.Chat.Slack;

/// <summary>What a request claiming to come from Slack turned out to be.</summary>
/// <remarks>
/// <b>A verdict rather than a bool.</b> "Not from Slack" and "from Slack, replayed" are different
/// facts, and an operator reading a log needs to tell them apart. A rejected request that only says
/// "no" is the kind of diagnostic this project keeps having to add afterwards.
/// </remarks>
public enum SlackSignatureVerdict
{
    Valid,
    MissingSignature,
    MalformedSignature,
    StaleRequest,
    Mismatch,
}

/// <summary>
/// Proves a request came from Slack and was not tampered with or replayed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Slack's scheme, in full.</b> The signed string is <c>v0:{timestamp}:{rawBody}</c>, signed with
/// HMAC-SHA256 under the app's signing secret, sent as <c>v0=&lt;hex&gt;</c>. The timestamp is part
/// of the signed string and is also checked against the clock, because a signature alone never
/// expires: without the window, one captured request replays forever.
/// </para>
/// <para>
/// <b>The body must be the raw bytes as received.</b> Any normalisation before verification, a
/// reserialisation or a whitespace trim, changes the signed string and either fails every request or
/// forces the caller to compare against something other than what will be parsed.
/// </para>
/// </remarks>
public static class SlackSignatureVerifier
{
    /// <summary>Slack's own guidance, and the window this replicates.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public static SlackSignatureVerdict Verify(
        string signingSecret,
        string? timestamp,
        string? signature,
        string rawBody,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return SlackSignatureVerdict.MissingSignature;
        }

        if (!signature.StartsWith("v0=", StringComparison.Ordinal))
        {
            return SlackSignatureVerdict.MalformedSignature;
        }

        if (!long.TryParse(timestamp, out var sentAtUnix))
        {
            return SlackSignatureVerdict.MalformedSignature;
        }

        byte[] presented;
        try
        {
            presented = Convert.FromHexString(signature[3..]);
        }
        catch (FormatException)
        {
            return SlackSignatureVerdict.MalformedSignature;
        }

        var sentAt = DateTimeOffset.FromUnixTimeSeconds(sentAtUnix);
        var skew = (now - sentAt).Duration();
        if (skew > Tolerance)
        {
            // Checked after the shape and before the comparison, so a replay is named as a replay
            // rather than reported as a mismatch against a secret that was never the problem.
            return SlackSignatureVerdict.StaleRequest;
        }

        var basestring = $"v0:{timestamp}:{rawBody}";
        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(signingSecret),
            Encoding.UTF8.GetBytes(basestring));

        return CryptographicOperations.FixedTimeEquals(expected, presented)
            ? SlackSignatureVerdict.Valid
            : SlackSignatureVerdict.Mismatch;
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/StyloMail.Chat.Tests/StyloMail.Chat.Tests.csproj`
Expected: PASS, 8 tests (the `[Theory]` contributes four).

- [ ] **Step 6: Commit**

```bash
git add src/StyloMail.Chat tests/StyloMail.Chat.Tests StyloMail.slnx
git commit -m "Verify that a request claiming to come from Slack actually did

Slack's scheme in full: the signed string is v0:{timestamp}:{rawBody} under
HMAC-SHA256, and the timestamp is checked against the clock as well as being
signed, because a signature alone never expires and one captured request would
replay forever.

A verdict rather than a bool, so a replay and a forgery are distinguishable in a
log rather than both reading as no.

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 2: A verified event becomes a message the rest of the system can read

**Files:**
- Create: `src/StyloMail.Chat/ChatMessage.cs`
- Create: `src/StyloMail.Chat/Slack/SlackEventReader.cs`
- Create: `src/StyloMail.Chat/Slack/SlackRetryGuard.cs`
- Create: `tests/StyloMail.Chat.Tests/SlackEventReaderTests.cs`
- Create: `tests/StyloMail.Chat.Tests/SlackRetryGuardTests.cs`

**Interfaces:**
- Consumes: `SlackSignatureVerdict` from Task 1.
- Produces: `ChatMessage` with `ChannelKind`, `WorkspaceId`, `ChannelId`, `ThreadId`, `AuthorId`, `Text`, `OccurredAt`, `EventId`; `SlackEventReader.TryRead(string json, out ChatMessage message, out SlackEventIgnored reason)`; `SlackRetryGuard.TryBegin(string eventId)`.

- [ ] **Step 1: Write the failing test for normalisation**

Create `tests/StyloMail.Chat.Tests/SlackEventReaderTests.cs`:

```csharp
using StyloMail.Chat.Slack;

namespace StyloMail.Chat.Tests;

public sealed class SlackEventReaderTests
{
    private const string MessageEvent = """
        {"type":"event_callback","event_id":"Ev01","event_time":1760000000,
         "team_id":"T01",
         "event":{"type":"message","channel":"C01","user":"U01","text":"hello",
                  "ts":"1760000000.000100"}}
        """;

    [Fact]
    public void A_message_event_becomes_a_chat_message()
    {
        Assert.True(SlackEventReader.TryRead(MessageEvent, out var message, out _));

        Assert.Equal(Core.ChannelKind.Slack, message.ChannelKind);
        Assert.Equal("T01", message.WorkspaceId);
        Assert.Equal("C01", message.ChannelId);
        Assert.Equal("U01", message.AuthorId);
        Assert.Equal("hello", message.Text);
        Assert.Equal("Ev01", message.EventId);
        Assert.Null(message.ThreadId);
    }

    [Fact]
    public void A_threaded_reply_carries_its_thread()
    {
        var json = MessageEvent.Replace("\"ts\":\"1760000000.000100\"",
            "\"ts\":\"1760000000.000200\",\"thread_ts\":\"1760000000.000100\"");

        Assert.True(SlackEventReader.TryRead(json, out var message, out _));
        Assert.Equal("1760000000.000100", message.ThreadId);
    }

    [Fact]
    public void A_message_the_system_posted_itself_is_ignored()
    {
        // A bot's own posts come back as message events. Assessing them would make the system read
        // its own output as traffic, and act on it.
        var json = MessageEvent.Replace("\"user\":\"U01\"", "\"bot_id\":\"B01\"");

        Assert.False(SlackEventReader.TryRead(json, out _, out var reason));
        Assert.Equal(SlackEventIgnored.FromABot, reason);
    }

    [Fact]
    public void An_edited_message_is_ignored_because_it_was_already_read()
    {
        // message_changed and message_deleted wrap the original inside a nested event, and reading
        // one as a fresh message would assess the same content twice with different words.
        var json = """{"type":"event_callback","event_id":"Ev02","team_id":"T01","event":{"type":"message","subtype":"message_changed","channel":"C01"}}""";

        Assert.False(SlackEventReader.TryRead(json, out _, out var reason));
        Assert.Equal(SlackEventIgnored.NotAPlainMessage, reason);
    }

    [Theory]
    [InlineData("""{"type":"url_verification","challenge":"abc"}""")]
    [InlineData("""{"type":"event_callback","event":{"type":"reaction_added"}}""")]
    [InlineData("not json at all")]
    public void Anything_that_is_not_a_plain_message_is_ignored_rather_than_thrown(string json)
    {
        Assert.False(SlackEventReader.TryRead(json, out _, out _));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/StyloMail.Chat.Tests/StyloMail.Chat.Tests.csproj`
Expected: FAIL, with `The type or namespace name 'SlackEventReader' could not be found`.

- [ ] **Step 3: Write the message and the reader**

Create `src/StyloMail.Chat/ChatMessage.cs`:

```csharp
using StyloMail.Core;

namespace StyloMail.Chat;

/// <summary>
/// One message from a chat platform, in the only shape the rest of the system sees.
/// </summary>
/// <remarks>
/// <para>
/// <b>Platform fields are decoded here and nowhere else.</b> Everything downstream reads this and
/// nothing reads Slack's JSON, so a second platform is a second reader rather than a second
/// pipeline.
/// </para>
/// <para>
/// <b><see cref="EventId"/> is the platform's own identifier, not ours.</b> It is what makes
/// deduplication possible when the platform retries a delivery, and it is carried rather than
/// derived from the content so that two identical messages are still two messages.
/// </para>
/// </remarks>
public sealed record ChatMessage
{
    public required ChannelKind ChannelKind { get; init; }

    public required string EventId { get; init; }

    public required string WorkspaceId { get; init; }

    public required string ChannelId { get; init; }

    public string? ThreadId { get; init; }

    public required string AuthorId { get; init; }

    /// <summary>The message text as posted, unmodified.</summary>
    public required string Text { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}
```

Create `src/StyloMail.Chat/Slack/SlackEventReader.cs`:

```csharp
using System.Text.Json;

namespace StyloMail.Chat.Slack;

/// <summary>Why an event that arrived was not read as a message.</summary>
public enum SlackEventIgnored
{
    NotJson,
    NotAnEventCallback,
    NotAPlainMessage,
    FromABot,
    MissingFields,
}

/// <summary>
/// Turns a verified Slack event into a <see cref="ChatMessage"/>, or says why it did not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every path returns rather than throws.</b> This runs on an unauthenticated HTTP surface, and a
/// parse failure there is a response code, not an exception out of the host.
/// </para>
/// <para>
/// <b>The system's own posts are ignored.</b> A bot's messages come back as message events, so
/// reading them would make the system assess its own output and act on it.
/// </para>
/// <para>
/// <b>Edits and deletions are ignored, and that is a decision rather than an omission.</b> Slack
/// delivers those as a nested event describing a message that was already read once. Re-reading one
/// as fresh traffic would assess the same message twice, under words it no longer has.
/// </para>
/// </remarks>
public static class SlackEventReader
{
    public static bool TryRead(string json, out ChatMessage message, out SlackEventIgnored reason)
    {
        message = null!;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            reason = SlackEventIgnored.NotJson;
            return false;
        }

        using (document)
        {
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var type) || type.GetString() != "event_callback")
            {
                reason = SlackEventIgnored.NotAnEventCallback;
                return false;
            }

            if (!root.TryGetProperty("event", out var evt))
            {
                reason = SlackEventIgnored.MissingFields;
                return false;
            }

            if (evt.TryGetProperty("bot_id", out _))
            {
                reason = SlackEventIgnored.FromABot;
                return false;
            }

            var subtype = evt.TryGetProperty("subtype", out var s) ? s.GetString() : null;
            if (evt.TryGetProperty("type", out var kind) || subtype is not null)
            {
                if (kind.GetString() != "message" || subtype is not null)
                {
                    reason = SlackEventIgnored.NotAPlainMessage;
                    return false;
                }
            }
            else
            {
                reason = SlackEventIgnored.NotAPlainMessage;
                return false;
            }

            if (!TryString(root, "event_id", out var eventId)
                || !TryString(root, "team_id", out var workspaceId)
                || !TryString(evt, "channel", out var channelId)
                || !TryString(evt, "user", out var authorId)
                || !TryString(evt, "ts", out var ts))
            {
                reason = SlackEventIgnored.MissingFields;
                return false;
            }

            var text = evt.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var threadId = evt.TryGetProperty("thread_ts", out var th) ? th.GetString() : null;

            message = new ChatMessage
            {
                ChannelKind = Core.ChannelKind.Slack,
                EventId = eventId,
                WorkspaceId = workspaceId,
                ChannelId = channelId,
                ThreadId = threadId,
                AuthorId = authorId,
                Text = text,
                OccurredAt = DateTimeOffset.FromUnixTimeSeconds((long)double.Parse(ts)),
            };

            reason = default;
            return true;
        }
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property)
            && property.GetString() is { Length: > 0 } found)
        {
            value = found;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
```

- [ ] **Step 4: Run the reader tests**

Run: `dotnet test tests/StyloMail.Chat.Tests/StyloMail.Chat.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Write the failing test for the retry guard**

Create `tests/StyloMail.Chat.Tests/SlackRetryGuardTests.cs`:

```csharp
using StyloMail.Chat.Slack;

namespace StyloMail.Chat.Tests;

public sealed class SlackRetryGuardTests
{
    [Fact]
    public void The_same_event_twice_is_only_admitted_once()
    {
        // Slack retries a delivery it thinks failed. Assessing the retry would double every
        // observation the behavioural engine counts, which is a rate change it cannot tell from
        // real traffic.
        var guard = new SlackRetryGuard(capacity: 64);

        Assert.True(guard.TryBegin("Ev01"));
        Assert.False(guard.TryBegin("Ev01"));
        Assert.True(guard.TryBegin("Ev02"));
    }

    [Fact]
    public void Forgetting_an_event_does_not_forget_a_different_one()
    {
        var guard = new SlackRetryGuard(capacity: 64);
        guard.TryBegin("Ev01");
        guard.Forget("Ev01");

        // A retry after a genuine failure has to be admitted, or a transient fault on our side
        // becomes a message that is never assessed and never retried either.
        Assert.True(guard.TryBegin("Ev01"));
    }

    [Fact]
    public void The_oldest_events_are_evicted_rather_than_growing_without_bound()
    {
        var guard = new SlackRetryGuard(capacity: 2);
        guard.TryBegin("Ev01");
        guard.TryBegin("Ev02");
        guard.TryBegin("Ev03");

        // Bounded cardinality is a feature of this system, not tuning. Past the bound an old id may
        // be admitted again, which is the correct trade: a duplicated assessment is recoverable and
        // an unbounded set is not.
        Assert.True(guard.TryBegin("Ev01"));
    }
}
```

- [ ] **Step 6: Run it to verify it fails, then write the guard**

Run: `dotnet test tests/StyloMail.Chat.Tests/StyloMail.Chat.Tests.csproj --filter SlackRetryGuardTests`
Expected: FAIL, `SlackRetryGuard` not found.

Create `src/StyloMail.Chat/Slack/SlackRetryGuard.cs`:

```csharp
namespace StyloMail.Chat.Slack;

/// <summary>
/// Admits an event once, so a platform retry is not assessed as new traffic.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded, and the bound is the point.</b> A set that grew with every event would be an
/// unbounded allocation driven by an external caller. Past the capacity the oldest id is evicted and
/// may be admitted again, and that is the right trade: a rare duplicated assessment is recoverable,
/// unbounded growth on an unauthenticated surface is not.
/// </para>
/// <para>
/// <b><see cref="Forget"/> exists for a failure on our side.</b> If the work after admission fails,
/// the caller forgets the id so a genuine retry is admitted rather than being mistaken for a
/// duplicate of an attempt that never completed.
/// </para>
/// <para>
/// Single-threaded per instance. The host calls this from one request at a time per guard, and a
/// shared guard would need its own locking and its own reasoning.
/// </para>
/// </remarks>
public sealed class SlackRetryGuard(int capacity)
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public bool TryBegin(string eventId)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventId);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        if (!_seen.Add(eventId))
        {
            return false;
        }

        _order.Enqueue(eventId);

        while (_order.Count > capacity)
        {
            _seen.Remove(_order.Dequeue());
        }

        return true;
    }

    public void Forget(string eventId)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventId);
        _seen.Remove(eventId);
    }
}
```

- [ ] **Step 7: Run everything and the solution**

```bash
dotnet test tests/StyloMail.Chat.Tests/StyloMail.Chat.Tests.csproj
dotnet build StyloMail.slnx
dotnet test StyloMail.slnx
```

Expected: the new project green, the solution 0 errors and 0 warnings, and every pre-existing test unchanged.

- [ ] **Step 8: Commit**

```bash
git add src/StyloMail.Chat tests/StyloMail.Chat.Tests
git commit -m "Read a verified Slack event as a chat message, or say why not

Platform fields are decoded in one place so nothing downstream reads Slack's
JSON. The system's own posts are ignored, because a bot's messages come back as
message events and reading them would make the system assess its own output.
Edits and deletions are ignored too: they arrive as a nested event describing a
message already read, so re-reading one would assess the same message twice under
words it no longer has.

The retry guard is bounded, and past the bound an old id may be admitted again. A
rare duplicated assessment is recoverable, unbounded growth on an unauthenticated
surface is not.

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## What this plan deliberately does not do

- **No HTTP endpoint, no host wiring.** The events arrive over HTTP and that is plan 2b, together
  with the channel-neutral input decision. This plan has no dependency on that decision, which is
  why it could be written and started before it was made.
- **No assessment, no ledger, no evidence.** A `ChatMessage` is where this stops.
- **No Discord.** The interface earns its shape from one platform first.
- **No signature verification on the URL-verification handshake**, which is a `challenge` echoed
  back and is handled with the endpoint in 2b.
