# chat- : the chat channel extension

## Identity and scope

Owner of StyloMail's second channel family: the channel-neutral Core contract, the Slack and Discord
connectors, and the triage layer. Parent: `overview-`. No worktree: I work in the shared main tree.

**Hard rule from the mission: I do not `git add`, `git commit`, `--amend` or `reset`.** I leave the
work in the tree and report; `overview-` verifies and commits the lane. Several agents share this
tree.

## Reading order for a cold start

1. `docs/chat-channels-plan-01-core-contract.md` (plan 1, my first deliverable)
2. `docs/chat-channels-design.md` (design of record for plans 2 and 3)
3. `.styloagent/spec.md` section 13 (identity and boundary), section 4 (privacy stance)
4. `.styloagent/architecture.md` (component view and ownership)

## The one fact that shapes everything

Email lets StyloMail decide before delivery. A normal Slack app receives a `message` event **after**
delivery. So this is an **observer with post-hoc interventions, never a proxy**, and every chat
assessment must record `DeliveryTiming: PostDelivery` as a stated fact. Plan 1 makes the property
`required` so no call site can omit it and accidentally claim `PreAcceptance`.

## Status: plan 1 (Core contract) COMPLETE, verified, reported 2026-09-22

**Landed as `6b11add`** (branch `main`), verified and committed and pushed by `overview-`, who
reproduced the whole run independently. I did not commit it: that is their lane by rule.

`overview-` also corrected plan 1's construction-site inventory in the plan itself, carrying the
three deviations I reported (the seven-site inventory, the CA1861 finding, the two wrong failure
predictions) with the method line "grep for the type name and read the hits, never for the
construction idiom". My four out-of-plan additions were approved.

### Files created (4)

- `src/StyloMail.Core/ChannelKind.cs` (`Email`, `Slack`, `Discord`)
- `src/StyloMail.Core/DeliveryTiming.cs` (`PreAcceptance`, `PostDelivery`)
- `src/StyloMail.Core/ChannelContext.cs` (required `Kind`, nullable `WorkspaceId`/`ChannelId`/`ThreadId`, static `Email`)
- `tests/StyloMail.Core.Tests/ChannelContractTests.cs` (5 tests)

### Files modified (9)

Named by the plan: `src/StyloMail.Core/MailAssessment.cs`, `src/StyloMail.Assessment/MailAssessor.cs`,
`src/StyloMail.Core/MailAnalysisInput.cs`, `src/StyloMail.Mime/BoundedMimeMessageAnalyzer.cs`,
`src/StyloMail.Host/Hosting/HostIngressSink.cs`, `tests/StyloMail.Host.Tests/ProviderCredentialTests.cs`,
`tests/StyloMail.Jev.Tests/JevSemanticMailClassifierTests.cs`.

**Not named by the plan but required to compile** (flagging, see below):
`tests/StyloMail.Assessment.Tests/TestSupport.cs` (2 sites), `tests/StyloMail.Host.Tests/TestSupport.cs`
(1 site, `MailAssessment.DeliveryTiming`).

### Measured totals on the frozen tree

`dotnet build StyloMail.slnx`: 0 warnings, 0 errors.
`dotnet test StyloMail.slnx`: 1302 passed, 0 failed, 18 skipped, 1320 total.

Core.Tests 20, Policy.Tests 19, Persistence.Tests 20, Desktop.Tests 182 (+18 skipped),
Jev.Tests 15, Mime.Tests 91, AccessProxy.Tests 61, Transport.Tests 192, Assessment.Tests 124,
Adaptive.Tests 181, Queue.Tests 97, Host.Tests 300.

Core went 15 to 20 (+5), exactly the plan's prediction. Assessment stayed at 124, the plan's evidence
that behaviour did not change.

## Gotchas and hard-won facts

- **`dotnet` is not on PATH.** Every command needs
  `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`.
- **The solution is `StyloMail.slnx`**, never a `.sln`.
- **CA1861 is an error here** ("prefer static readonly fields over constant array arguments"), even
  though there is no root `.editorconfig` or `Directory.Build.props` in this repo. The plan's literal
  Task 1 test code (`Assert.Equal(new[] {...}, ...)`) does NOT build. Hoist constant arrays to locals
  or static readonly fields.
- **`nameof(Type.Member)` is compile-time**, so a "failing test" that names a not-yet-existing member
  fails as CS0117/CS0103, not as the runtime `Assert.NotNull() Failure` the plan predicted. Two of the
  plan's Step 2 predictions were unachievable as written; red-before-green still held.
- **Construction sites hide in target-typed `new()`.** Grepping for `new MailAnalysisInput` misses
  `=> new() { ... }`. Use the compiler: `dotnet build StyloMail.slnx | grep "error CS9035"`.
- **Never use an em-dash** in code, comments, docs or commit messages. Operator correction that still
  binds. Use a colon or a full stop.
- **Before believing a red, check the mutation-sweep signals**: `ls .styloagent/tools/.mutation-sweep.lock`
  and `find src -name '*.bak'`. A SIGKILLed sweep leaves mutated source with no lock.

## Lane boundaries

**Do not touch:** `src/StyloMail.Desktop` (desktop-), `src/StyloMail.Host/Traffic/` and
`tests/StyloMail.Host.Tests/Traffic*` (hub-), `src/StyloMail.Queue`, `src/StyloMail.Adaptive`,
`src/StyloMail.Policy`, `src/StyloMail.Transport`, `src/StyloMail.AccessProxy`.

Plus plan 1's standing constraint: change nothing else in Core, Mime, Host or Assessment. No
refactoring around myself, no renaming, no unrequested cleanup.

## Pending / next

- **Plan 2 (read-only Slack connector) is NOT started. `overview-` is writing it, and told me not to
  start it yet.** Do not start it without being asked.
- Plan 3 is triage. Both are designed in `docs/chat-channels-design.md`.

## Plan 2a (Slack ingress) COMPLETE, verified, reported 2026-09-22. NOT committed.

`overview-` split the connector: 2a is mine (signature verification, normalisation, retry guard,
ending at `ChatMessage`); 2b is theirs and holds the channel-neutral input decision.

### Files created

- `src/StyloMail.Chat/StyloMail.Chat.csproj` (**SlackNet 0.18.0**, ProjectReference to `StyloMail.Core`)
- `src/StyloMail.Chat/ChatMessage.cs`
- `src/StyloMail.Chat/Slack/SlackSignatureVerifier.cs` (+ `SlackSignatureVerdict`)
- `src/StyloMail.Chat/Slack/SlackEventReader.cs` (+ `SlackEventIgnored`)
- `src/StyloMail.Chat/Slack/SlackRetryGuard.cs`
- `tests/StyloMail.Chat.Tests/` (3 test files)
- Modified: `StyloMail.slnx` (added both projects; `access-`'s Integration.Tests entry preserved)

### Measured on the frozen tree

Build 0 warnings / 0 errors. Solution **1330 passed, 0 failed, 21 skipped** (Desktop 18 +
Integration 3, both pre-existing). Chat.Tests **28 passed**. Previous solution was 1302 passed, so
exactly +28 and every other project unchanged.

### Four plan defects found (all reported)

1. **SlackNet is added but never used.** The reader hand-rolls wire types with `System.Text.Json`
   while SlackNet 0.18.0 ships `SlackNet.Events.EventCallback`/`MessageEvent`/`BotMessage`/
   `UrlVerification`. This was the explicit stop-and-report case. **Awaiting `overview-`'s decision**;
   I left the reference in place rather than deciding unilaterally.
2. **Task 1's test contradicted its implementation**: it asserted `MalformedSignature` for `null`/`""`
   where the impl returns `MissingSignature`, so it failed 2 of its own 4 cases. Split into two
   theories. The plan predicts 8 tests; it is 9.
3. **The plan omits `StyloMail.Chat` -> `StyloMail.Core` reference**, so `ChatMessage` does not
   compile (`CS0234`). Added.
4. **The reader violated its own "every path returns rather than throws" contract** on an
   unauthenticated surface. All 7 probed malformed-but-valid-JSON shapes threw
   (`InvalidOperationException` from `JsonElement.GetString()`, `FormatException` from `double.Parse`).
   Fixed by checking `ValueKind` before every read, `TryParse` with `InvariantCulture`, range bounds
   against `DateTimeOffset`, and a `NaN` finiteness check (`NaN` compares false against both bounds
   and would silently become the epoch).

### Reusable facts for 2b

- Optional fields (`text`, `thread_ts`) degrade to absent on a wrong JSON type; required fields
  (`event_id`, `team_id`, `channel`, `user`, `ts`) refuse. That split is deliberate and tested.
- `SlackSignatureVerifier.Verify` returns a verdict, never a bool, and never throws.
- `SlackRetryGuard` is single-threaded per instance, bounded by capacity, and has `Forget` for a
  failure on our side.

### Gap analysis I delivered for plan 2 (2026-09-22)

At `overview-`'s request I read the connector section and reported what is too underspecified to
write a plan from, grounded in repo checks rather than impressions. Six blockers:

1. **3-second ack versus "no queue" is an unresolved tension** and a correctness property: Slack
   retries if not acked in 3s, so process-after-ack means holding in-flight work, which the design's
   blanket "no queue" claim does not distinguish from a delivery queue. Must state whether an
   unprocessed event may be dropped on restart.
2. **Events API over HTTP, or Socket Mode.** The design never picks. Socket Mode has no inbound HTTP
   request, so "signature verification of the inbound request" would not exist. Precedent for the
   webhook shape: `src/StyloMail.Host/Endpoints/CloudflareIngressEndpoints.cs` (`.AllowAnonymous()`,
   body limit paired with Kestrel).
3. **The chat input record does not exist and its shape is undecided.** `MailAnalysisInput` cannot be
   reused: it requires `MailEnvelope` and `AuthenticationContext`, which a Slack message lacks.
4. **The plan 2 / plan 3 boundary is not clean**: sequencing puts triage in plan 3 but describes plan
   2 as including it. Needs a statement of what plan 2's `Evidence`/`RiskDimensions` contain.
5. **Deduplication key, storage, retention, and its ordering after signature verification.**
6. **What a "watched" workspace is and where Slack credentials come from (by reference, not value).**

Five non-blockers that will get decided by accident: `MailAssessment` carries `DeliveryTiming` but
**no channel**, so a chat decision is only distinguishable by inferring `PostDelivery` (the very
inference the design forbids); `ChannelContext` shipped without the design's "thread position" and
"membership evidence"; in-scope event subtypes and the bot loop rule; "read-only" as a negative list;
synthetic versus recorded test payloads.
