# Chat channel Core contract: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the three Core types that let a non-email channel be assessed, and make the delivery timing of every assessment a stated fact rather than an inferred one.

**Architecture:** Additive only. No `Mail*` type is renamed and no behaviour changes. `DeliveryTiming` and `ChannelContext` become `required` properties, so an existing call site stops compiling until it says what channel it is on and whether it could have stopped the message. That is deliberate: a silently defaulted `DeliveryTiming` would let a chat assessment claim it could have prevented a message it only reacted to, which is the one claim this extension must never make by accident.

**Tech Stack:** .NET 10, xUnit, `StyloMail.slnx`.

**Spec:** `docs/chat-channels-design.md` and `.styloagent/spec.md` section 13. This plan implements the "Core additions, all additive" section and nothing else.

**Scope:** this is plan 1 of 3 for the chat extension. Plan 2 is the read-only Slack connector, plan 3 is triage. This plan produces no user-visible capability on its own; it produces a contract that compiles and is tested, which is what the next two plans build on.

## Global Constraints

- `dotnet` is **not on PATH**. Every command runs after:
  `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`
- The solution is `StyloMail.slnx`, never a `.sln`.
- Analyzers run as **errors**. A build with a warning is a failed build.
- **Core references nothing.** Everything references Core. No new package reference may be added to `StyloMail.Core.csproj`.
- **Never use an em-dash** in code, comments, documentation or a commit message. Use a colon or a full stop. This is an operator correction that still binds.
- Every commit message ends with the `Co-Authored-By: Claude <noreply@anthropic.com>` trailer.
- Judge your own work with `dotnet test tests/StyloMail.Core.Tests/StyloMail.Core.Tests.csproj`, never the whole solution.

---

### Task 1: The three channel types

**Files:**
- Create: `src/StyloMail.Core/ChannelKind.cs`
- Create: `src/StyloMail.Core/DeliveryTiming.cs`
- Create: `src/StyloMail.Core/ChannelContext.cs`
- Test: `tests/StyloMail.Core.Tests/ChannelContractTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ChannelKind` (`Email`, `Slack`, `Discord`), `DeliveryTiming` (`PreAcceptance`, `PostDelivery`), and `ChannelContext` with `Kind`, `WorkspaceId`, `ChannelId`, `ThreadId` and a static `ChannelContext.Email`.

- [ ] **Step 1: Write the failing test**

Create `tests/StyloMail.Core.Tests/ChannelContractTests.cs`:

```csharp
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
        Assert.Equal(
            new[] { "Email", "Slack", "Discord" },
            Enum.GetNames<ChannelKind>());
    }

    [Fact]
    public void Delivery_timing_has_exactly_two_answers()
    {
        // PreAcceptance means we were in the delivery path. PostDelivery means the platform had
        // already delivered it and every action available is post-hoc. There is no third answer,
        // and "unknown" is not one of them: an assessment that cannot say which it was has not
        // established something it needs to know.
        Assert.Equal(
            new[] { "PreAcceptance", "PostDelivery" },
            Enum.GetNames<DeliveryTiming>());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/StyloMail.Core.Tests/StyloMail.Core.Tests.csproj`
Expected: FAIL, with `The type or namespace name 'ChannelKind' could not be found`.

- [ ] **Step 3: Write the types**

Create `src/StyloMail.Core/ChannelKind.cs`:

```csharp
namespace StyloMail.Core;

/// <summary>The channel a communication arrived on.</summary>
public enum ChannelKind
{
    /// <summary>SMTP. The only channel with an envelope, and the only one we can refuse before delivery.</summary>
    Email,

    /// <summary>Slack. A message event arrives after the platform has already delivered it.</summary>
    Slack,

    /// <summary>Discord. Same post-delivery constraint as Slack.</summary>
    Discord,
}
```

Create `src/StyloMail.Core/DeliveryTiming.cs`:

```csharp
namespace StyloMail.Core;

/// <summary>
/// Whether an assessment was made before the recipient could see the message.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is recorded rather than derived.</b> It would be possible to infer it from
/// <see cref="ChannelKind"/> today and wrong to, for the same reason an envelope is not inferred
/// from a header: a channel that changes its delivery model, or a future channel that offers both,
/// would silently keep the old answer.
/// </para>
/// <para>
/// <b>It is required, so no call site can omit it.</b> A defaulted value would let an assessment
/// claim <see cref="PreAcceptance"/> by not thinking about it, and a console showing a post-hoc
/// hold as though the system could have stopped the message is the specific false statement this
/// property exists to prevent.
/// </para>
/// </remarks>
public enum DeliveryTiming
{
    /// <summary>We were in the delivery path and could decline responsibility before delivery.</summary>
    PreAcceptance,

    /// <summary>The platform had already delivered it. Every action available is post-hoc.</summary>
    PostDelivery,
}
```

Create `src/StyloMail.Core/ChannelContext.cs`:

```csharp
namespace StyloMail.Core;

/// <summary>
/// Where a message came from, and what the channel tells us about who could see it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kept separate from message content</b>, the same way tagged context is, so a reader can tell
/// what the platform asserted from what the message said. On Slack the platform authenticates the
/// member and not the message, which means a compromised account is authenticated exactly like a
/// legitimate one. Membership is therefore context and never a verdict.
/// </para>
/// <para>
/// <b>Every field beyond <see cref="Kind"/> is nullable and null means the channel does not have
/// that concept.</b> Email has no workspace, so <see cref="ChannelContext.Email"/> leaves it null
/// rather than inventing one. Absence is a distinct state here as it is everywhere else in this
/// engine.
/// </para>
/// </remarks>
public sealed record ChannelContext
{
    public required ChannelKind Kind { get; init; }

    /// <summary>The tenant's workspace on the platform, where the channel has one.</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>The channel or conversation the message was posted in.</summary>
    public string? ChannelId { get; init; }

    /// <summary>The thread it belongs to, where the platform has threads.</summary>
    public string? ThreadId { get; init; }

    /// <summary>The email channel, which has none of the fields above.</summary>
    public static ChannelContext Email { get; } = new() { Kind = ChannelKind.Email };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/StyloMail.Core.Tests/StyloMail.Core.Tests.csproj`
Expected: PASS, 3 new tests green.

- [ ] **Step 5: Commit**

```bash
git add src/StyloMail.Core/ChannelKind.cs src/StyloMail.Core/DeliveryTiming.cs \
        src/StyloMail.Core/ChannelContext.cs tests/StyloMail.Core.Tests/ChannelContractTests.cs
git commit -m "Add the channel and delivery-timing vocabulary

PreAcceptance means we were in the delivery path; PostDelivery means the platform
had already delivered it. Recorded rather than derived from the channel kind,
because a channel that changes its delivery model would silently keep the old
answer.

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 2: Delivery timing is required on the assessment

**Files:**
- Modify: `src/StyloMail.Core/MailAssessment.cs`
- Modify: `src/StyloMail.Assessment/MailAssessor.cs` (the single construction site)
- Modify: `tests/StyloMail.Core.Tests/ChannelContractTests.cs`
- Test: `tests/StyloMail.Assessment.Tests/MailAssessorTests.cs` (existing, must stay green)

**Interfaces:**
- Consumes: `DeliveryTiming` from Task 1.
- Produces: `MailAssessment.DeliveryTiming`, a required `DeliveryTiming`.

- [ ] **Step 1: Write the failing test**

Append to `tests/StyloMail.Core.Tests/ChannelContractTests.cs` and add `using System.Reflection;` and `using System.Runtime.CompilerServices;` to its usings:

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/StyloMail.Core.Tests/StyloMail.Core.Tests.csproj`
Expected: FAIL, with `Assert.NotNull() Failure: Value of type 'PropertyInfo' is null`.

- [ ] **Step 3: Add the property and fix the call site**

In `src/StyloMail.Core/MailAssessment.cs`, add beside the other required members:

```csharp
    /// <summary>
    /// Whether this assessment could have stopped the message or only reacts to it.
    /// </summary>
    /// <remarks>
    /// Required, so it is stated at every construction site rather than defaulted. See
    /// <see cref="DeliveryTiming"/> for why a default is the dangerous option.
    /// </remarks>
    public required DeliveryTiming DeliveryTiming { get; init; }
```

In `src/StyloMail.Assessment/MailAssessor.cs`, at the single `new MailAssessment` site, add:

```csharp
        DeliveryTiming = DeliveryTiming.PreAcceptance,
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/StyloMail.Core.Tests/StyloMail.Core.Tests.csproj`
Expected: PASS.

Run: `dotnet test tests/StyloMail.Assessment.Tests/StyloMail.Assessment.Tests.csproj`
Expected: PASS, 124 tests, unchanged. This is the check that the added property changed no behaviour.

- [ ] **Step 5: Commit**

```bash
git add src/StyloMail.Core/MailAssessment.cs src/StyloMail.Assessment/MailAssessor.cs \
        tests/StyloMail.Core.Tests/ChannelContractTests.cs
git commit -m "Require every assessment to state its delivery timing

The email path states PreAcceptance at its one construction site. Required rather
than defaulted, because a default would let a chat assessment claim it could have
stopped a message it only reacted to.

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 3: The channel is required on the analysis input

**Files:**
- Modify: `src/StyloMail.Core/MailAnalysisInput.cs`
- Modify: `src/StyloMail.Mime/BoundedMimeMessageAnalyzer.cs`
- Modify: `src/StyloMail.Host/Hosting/HostIngressSink.cs`
- Modify: `tests/StyloMail.Host.Tests/ProviderCredentialTests.cs`
- Modify: `tests/StyloMail.Jev.Tests/JevSemanticMailClassifierTests.cs`
- Modify: `tests/StyloMail.Core.Tests/ChannelContractTests.cs`

**Interfaces:**
- Consumes: `ChannelContext` and `ChannelContext.Email` from Task 1.
- Produces: `MailAnalysisInput.Channel`, a required `ChannelContext`.

- [ ] **Step 1: Write the failing test**

Append to `tests/StyloMail.Core.Tests/ChannelContractTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/StyloMail.Core.Tests/StyloMail.Core.Tests.csproj`
Expected: FAIL, with `Assert.NotNull() Failure`.

- [ ] **Step 3: Add the property and fix the five call sites**

In `src/StyloMail.Core/MailAnalysisInput.cs`, add:

```csharp
    /// <summary>
    /// The channel this message arrived on, and what it tells us about reach.
    /// </summary>
    /// <remarks>
    /// Required, so the adapter that produced this input names the channel rather than leaving a
    /// reader to infer it from the shape of the message.
    /// </remarks>
    public required ChannelContext Channel { get; init; }
```

Then add `Channel = ChannelContext.Email,` to the four construction sites in `src/StyloMail.Mime/BoundedMimeMessageAnalyzer.cs`, `src/StyloMail.Host/Hosting/HostIngressSink.cs`, `tests/StyloMail.Host.Tests/ProviderCredentialTests.cs` and `tests/StyloMail.Jev.Tests/JevSemanticMailClassifierTests.cs`. `ChannelContext.Email` is a shared immutable instance, so there is nothing to allocate per message.

- [ ] **Step 4: Run tests to verify they pass**

Run each of these, all of which must be green:

```
dotnet test tests/StyloMail.Core.Tests/StyloMail.Core.Tests.csproj
dotnet test tests/StyloMail.Mime.Tests/StyloMail.Mime.Tests.csproj
dotnet test tests/StyloMail.Jev.Tests/StyloMail.Jev.Tests.csproj
dotnet test tests/StyloMail.Host.Tests/StyloMail.Host.Tests.csproj
```

Then the whole solution, because four projects changed:

```
dotnet build StyloMail.slnx
dotnet test StyloMail.slnx
```

Expected: build 0 errors and 0 warnings, and every test green with the same totals as before this plan started plus the 5 new Core tests.

- [ ] **Step 5: Commit**

```bash
git add src/StyloMail.Core/MailAnalysisInput.cs src/StyloMail.Mime/BoundedMimeMessageAnalyzer.cs \
        src/StyloMail.Host/Hosting/HostIngressSink.cs tests/StyloMail.Host.Tests/ProviderCredentialTests.cs \
        tests/StyloMail.Jev.Tests/JevSemanticMailClassifierTests.cs tests/StyloMail.Core.Tests/ChannelContractTests.cs
git commit -m "Require every analysis input to name its channel

The email adapter states ChannelContext.Email at each of its call sites, so a chat
adapter added later cannot inherit an email default by omission.

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Corrections found while executing (2026-09-22)

The plan was executed by `chat-`, and three things in it were wrong. They are recorded here rather
than quietly fixed, because the next plan will be written the same way and will make the same mistake.

**1. The construction-site inventory was incomplete, and the method that produced it is the reason.**
The plan says Task 3 touches "the four construction sites", from a grep for `new MailAnalysisInput`.
That pattern misses **target-typed `new()`**, so the real counts are **seven** `MailAnalysisInput`
sites and **two** `MailAssessment` sites. The two the grep missed entirely are both in
`tests/StyloMail.Assessment.Tests/TestSupport.cs`, and the second `MailAssessment` site is in
`tests/StyloMail.Host.Tests/TestSupport.cs`. **Grep for the type name and read the hits, never for
the construction idiom.**

**2. CA1861 is an error in this repository.** The Task 1 test code as written passes constant arrays
to `Assert.Equal`, which fails the build before any type exists. There is no root `.editorconfig` and
no `Directory.Build.props`, so this arrives from the .NET 10 SDK defaults. The executed version hoists
both arrays to locals; the assertions are otherwise identical. Any future plan's test code has to
assume the full analyzer set, not just the obvious ones.

**3. The predicted failure text in Task 2 and Task 3 was unachievable.** Both steps call `nameof(...)`
on a property that does not exist yet, which is a compile error `CS0117`, not the runtime
`Assert.NotNull() Failure` the plan predicts. Red before green still held; only the prediction was
wrong. When a test references a missing member by `nameof`, the failure is always a compile error.

## What this plan deliberately does not do

- **It does not add a chat input type.** The design says the chat analyser is a sibling of the MIME
  analyser producing the same `Evidence`, and that belongs in plan 2 with the connector that needs it.
- **It does not change any behaviour.** Every existing test must pass with its existing totals, and
  that is the evidence for the claim.
- **It does not touch `MailEnvelope` or `AuthenticationContext`.** A chat message has neither, and
  plan 2 decides how a sibling input record carries what a chat message does have. Doing it here
  would be designing the connector from the Core side.
