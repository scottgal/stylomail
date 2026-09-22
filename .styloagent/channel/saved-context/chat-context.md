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

## Item 1 of overview-'s order: DONE, frozen, commit-ready (2026-09-22)

The ingress was committed by overview- at `66a0da6` (with SlackNet removed; the rationale is in the
csproj). His order: (1) ledger read-path fix standalone, (2) the Core move, (3) plan 2b. Not 2 and 3
together.

**Defect:** `MailAssessment` is persisted as a JSON document and read with `JsonSerializer.Deserialize`,
which enforces `required` on deserialisation. `DeliveryTiming` became required at `6b11add`, so every
row written before it throws `JsonException` on `FindAsync`/`ListAsync`. Reproduced first, on a row
this build wrote.

**Fix (all of it):**
- new `src/StyloMail.Host/Serialization/PersistedAssessmentConverter.cs`
- modified `src/StyloMail.Host/Serialization/HostJson.cs` (`PersistedRead` = `Options` + converter)
- modified `src/StyloMail.Host/Decisions/SqliteDecisionLedger.cs` (2 read sites; write path untouched)
- new `tests/StyloMail.Host.Tests/LedgerLegacyRowTests.cs`

Measured: build 0/0, solution **1333 passed, 0 failed, 21 skipped**; Host.Tests 300 -> 303.

**Design rules baked in, keep them:** the converter is confined to the persisted read path and must NOT
go into `HostJson.Options` (that would undo the `required` guarantee everywhere). Back-fills are
back-fills, not defaults: the justification is that `MailAssessor` is the only production construction
site and it is the email path, so legacy rows really were `Email`/`PreAcceptance`. **When 2b adds
`Channel` required, add a second back-fill call with `Email` for the same reason.**

**Two carry-forward findings:** `DecisionResponse` does not expose `deliveryTiming` at all, so the
interface doc's "the console shows deliveryTiming on every chat decision" is not yet satisfiable for
any decision. And `SqliteDecisionLedger.cs` has pre-existing em-dashes at lines 132, 133, 160 (not
mine, left alone).

## Item 2 of overview-'s order: DONE, frozen, reported (2026-09-22)

**Do not hold refactors for overview- again.** He ruled: "Waiting for a clean tree to hand me is the
right instinct when the change is a defect fix and the wrong one when it is a refactor." For a
refactor, state the transient red in the report and carry on.

**The URL and IDN analysis now lives in Core.**
- new `src/StyloMail.Core/UrlTools.cs` (`UrlTools`, `UrlObservation`, `IdnObservation`, all public)
- new `src/StyloMail.Core/LinkAnalysis.cs` (`LinkFinding` + extraction + **`FromPlainText(text, maxLinks)`**)
- modified `src/StyloMail.Mime/LinkExtractor.cs` (thin adapter over `LinkAnalysis.Extract`)
- deleted `src/StyloMail.Mime/UrlTools.cs`
- new `tests/StyloMail.Core.Tests/LinkAnalysisTests.cs` (11 tests)

Measured: build 0/0, solution 1362 passed 0 failed. **Core 20 -> 31, Mime 91 unchanged** (that is the
evidence it moved rather than changed).

**Two defects I introduced and caught by diffing against the original:** the `ScriptOf` ranges had been
transcribed from hex escapes into literal glyphs, and `KeySeparator` became a raw NUL byte instead of
the escape text. Both restored. **Always diff moved text against its origin; never trust transcription.**

**A tooling gotcha worth remembering:** writing `\u0000` or `\u0041` in an edit is decoded to the
character, so escape sequences in C# source must be written via a script (python with `chr(92)`) or
they silently become control characters. Scan new files for control characters after any such edit.

**For 2b Task 2:** a channel with a separate display text (HTML anchors, Slack's `<url|label>`) passes
both through `LinkAnalysis.Extract`; a channel whose text merely contains URLs uses `FromPlainText`.
Slack's markup parsing is new and belongs in `StyloMail.Chat`.
**Note for the contract review:** Core now has two link types side by side, `LinkObservation` (input
shape) and `LinkFinding` (analysis result). Not merged; flagged to overview-.

## 2b Task 4: the Slack events endpoint (STARTED 2026-09-22; solution 1419 passed)

Task 3 committed as `df4e6dc`. Task 4 = the endpoint, `url_verification`, signature verification,
dedup, normalisation, a ledgered decision, answering Slack fast with the assessment off the request
path. Own-post drop ships here, with **startup refusing to run with no identity**.

**Done:** `src/StyloMail.Host/Hosting/SlackIngressOptions.cs` (`StyloMail:Slack` section; `Enabled`,
`SigningSecret`, `OwnBotId`/`OwnBotUserId`, `PendingCapacity`, `Identity()`, `Validate()`).
`Validate` **throws when enabled with no identity or no signing secret**; a disabled endpoint is not
validated. `tests/StyloMail.Host.Tests/SlackIngressOptionsTests.cs` (6 tests); the identity test
**mutation-checked** (removing the check fails exactly that test).

**Template for the endpoint:** `src/StyloMail.Host/Endpoints/CloudflareIngressEndpoints.cs` -
`Map(app, ...)` + `.AllowAnonymous()`, `IngestAsync(HttpContext, connector, ct)`, bounded
`ReadBodyAsync`. Options bound in `HostServices.cs` via `services.Configure<T>(config.GetSection(T.SectionName))`.

**RULINGS (2026-09-22):** (1) fixtures I write cannot settle platform facts - label them as documented
shapes, keep the three flags; overview- is asking the operator for a capture. (2) **PERSIST BEFORE
ACK** - "Slack's ack is this path's 250". A durable bounded intake, written before the answer, drained
off the request path, bound enforced by REFUSING so the platform retries. `Complete` MARKS
(`assessed_at`), never deletes, or a retry inside the window is assessed twice.

**DONE: the durable intake (solution 1425).**
- `host_chat_intake` DDL in `HostDatabase.cs` (`event_id` PK, `received_at`, `payload`,
  `assessed_at` NULL) + a partial index on waiting rows + an index for pruning.
- `src/StyloMail.Host/Chat/ChatIntakeStore.cs`: `IChatIntakeStore` (`Admit`/`Waiting`/`Complete`/
  `Prune`), `ChatIntakeAdmission` (`Admitted`/`AlreadyKnown`/`Full`), `SqliteChatIntakeStore`.
  Registered in `HostServices.cs` unconditionally (inert unless the ingress is enabled).
  **Full rolls back** so the platform's retry is not mistaken for a duplicate.
- `tests/StyloMail.Host.Tests/ChatIntakeStoreTests.cs` (6 tests; durability proved by
  `ReusingStorageOf`). **Mutation-checked**: `Complete` deleting instead of marking fails exactly the
  double-assessment test.

**DONE: the endpoint (solution 1433).**
- `src/StyloMail.Host/Endpoints/SlackEventsEndpoints.cs`, `POST /v1/ingress/slack`, `MaxBodyBytes`.
  Flow: read raw body -> **verify as received bytes** -> 401 on any non-Valid verdict ->
  `url_verification` echoes the challenge -> `SlackEventReader.TryRead` with our identity (ignore ->
  200, no assessment) -> **`intake.Admit` BEFORE answering** -> 200, or **503 + Retry-After** when Full.
- `SlackEventReader.TryReadChallenge(json, out challenge)` added to `StyloMail.Chat`.
- `SlackIngressOptions` bound in `HostServices.cs`; mapped and **`Validate()`d** in `Program.cs` only
  when enabled.
- `TestHost.WithSlackIngress(secret, botId)`; `tests/StyloMail.Host.Tests/SlackEventsEndpointTests.cs`
  (8 tests). **Mutation-checked**: breaking `IsOurOwnPost` fails exactly the own-post test.
- **Analyzer gotcha:** a mutation making a method instance-data-free fails to build with **CA1822**, so
  a mutation probe must still touch instance data or it silently reports nothing.

**TASK 4 COMPLETE (2026-09-22; solution 1437 passed, 0 failed, 23 skipped).**

**The drain** (`src/StyloMail.Host/Chat/ChatIntakeDrain.cs`): `Waiting` -> re-read the stored bytes ->
`ChatInputFactory` -> `IChatAssessor` -> `IDecisionLedger.RecordAsync` -> `Complete` -> `Prune`.
Unreadable payload -> Complete (it will never read). **Assessment failure -> leave it WAITING**, so
nothing is silently consumed; a stuck event is visible, a vanished one is not.
`SlackIngressOptions.InboundTenantId` (default "inbound", same as Cloudflare) because the surface
carries no principal.

**A REAL BUG I FOUND AND FIXED, worth remembering:** I gated the chat registrations on
`configuration.GetValue<bool>(...)` at composition-root time. **`HostServices.cs` documents this trap
in its own remarks**: "The host's composition root runs before a test host layers its own
configuration in, so a decision taken at registration reads the wrong values." My condition was false,
so the drain was registered-but-never-constructed: dead code that looked present. Fixed by registering
**unconditionally and deciding at resolution** (the drain checks `Enabled` in `ExecuteAsync` and
resolves the assessor only after).

**A REAL DEFECT FOUND BY A REQUIREMENT-DERIVED TEST (2026-09-22; solution 1441).**
I could not make the drain retroactively red-first (the implementation existed). Instead I wrote tests
from the *requirement* text - the drain's own doc claim and overview-'s two named properties - ran
them before assuming, and **one went red for the right reason**: the drain retried a failing event in
a **tight loop with no backoff** (the `while` re-took the same batch immediately), so a persistently
failing event would spin as fast as the CPU allows, hammering the assessor and the database.
**Fixed** by pacing when a pass makes no progress (`progressed == 0` -> idle delay); `AssessAsync`
now returns bool. That is what red-first buys and a mutation cannot.

**LEGIBILITY LINE DONE (overview- asked for it; committed Task 4 as `9c04d71`).**
`ChatAssessmentHealth` (set when `UnavailableChatAssessor` is registered) + a readiness check in
`ReadinessProbe.Evaluate`: **not-ready with `chat_assessment_unavailable` only when the assessor is
unavailable AND something is waiting**. Two tests. No startup refusal - overview- confirmed the
degradation is deliberate (`HostCredentials.Resolve` returns `NotConfigured` only when BOTH secrets
are absent).

**CADENCE RULE from overview-:** when mid-edit, say the tree is mid-edit and give the last measured
numbers as the last measured numbers, never as the current state.

**Degradation ruled by me, FLAGGED:** with no profile master key, chat gets
`UnavailableChatAssessor`, which **throws**; the drain leaves events waiting. Chosen over refusing to
start because the key is env-only with no configuration path, so refusing would make the host
unstartable in tests. Mirrors `UnavailableMailAssessor`.

**STILL TO DO in Task 4:** the **drain** (hosted service: `Waiting` -> read -> assess via
`IChatAssessor` -> `IDecisionLedger.RecordAsync` -> `Complete` -> `Prune`), plus measuring the
per-event write cost overview- asked for if it proves disproportionate.

**TWO THINGS RAISED, both block or shape the rest of Task 4:**
1. **A fixture I write cannot settle a platform fact.** overview- expects Task 4 to settle `user_team`,
   `bot_id` vs `bot_user_id`, and the conversation-type field by "a recorded payload". Inventing a
   fixture from my reading of the docs would encode the assumption and then look verified. Needs a
   payload captured from a real workspace.
2. **The off-path assessment is the day-one "3-second ack vs no queue" tension, now concrete.** Slack
   retries unless acked in 3s, so we must ack then assess, which means holding verified events. The
   design's "no queue" is about *delivery* durability. Proposed: bounded in-memory buffer; past the
   bound return 503 so the platform retries (honest backpressure); **flag that a crash loses acked-
   but-unassessed events silently.**

## 2b Task 3: planned, rulings in hand, NOT STARTED (2026-09-22)

Lane **confirmed**: `src/StyloMail.Assessment` is mine for Task 3 (`assess-` is dehydrated).
Compose the existing engines; do NOT extract `MailAssessor`'s inline steps 5/6.

### RULING that corrects my plan: direction is DERIVED, never always-Inbound

I proposed `Direction = Inbound` for all chat. **REFUSED, and the refusal is right.** A workspace
member IS an authenticated principal.

- **member of the tenant -> `Outbound`; author outside it -> `Inbound`.**
- Why it matters: `MailDirection` remarks say inbound/outbound profile pools are **never merged**;
  `MailPolicyEngine` gates two branches on `Outbound` (one the quota path); and
  **compromised-account detection IS the outbound case** (our authenticated principal fanning out).
  That is the job this extension gets behavioural evidence for free, and always-Inbound would hide it.
- I conflated **our app** with **our members**: refusing our own bot's posts says nothing about
  whether a member is a stranger.

**This needs a new fact on `ChatMembershipFacts`: whether the author is external to the workspace**
(Slack carries `user_team` for Enterprise Grid / Connect authors). **If the platform cannot supply it,
report the gap rather than picking a default**; if a default is unavoidable, **state the assumption in
the assessment's own evidence** so no reader infers the direction was observed.

### Approved: compose, plus a drift pin

Composing `CompositeRiskScorer` + `MailPolicyEngine` + `ProfileCoordinator` directly is approved.
**Amendment: pin the drift risk with a test**, not just a remark: push equivalent evidence down both
the mail and chat paths and assert the same action comes out. If it is expensive to construct, say so
and the remark is the fallback.

### Task 3 IN PROGRESS (started 2026-09-22, red-first)

**Done so far: the direction derivation, which was the refused call.**

- `ChatMembershipFacts.IsExternal` (required bool) + **`Direction` derived** (external -> `Inbound`,
  member -> `Outbound`), never stored. `MailDirection`'s remarks confirm direction selects the profile
  pool and the two are never merged; `ProfileKey.Direction` is where it lands.
- `ChatMessage.IsExternal`; `SlackEventReader.IsExternalAuthor(evt, workspaceId)` reads `user_team`
  and compares to `team_id` (**presence alone is not enough** - Enterprise Grid sends user_team for
  members too). `ChatInputFactory` passes it through.
- **Unverified offline:** that Slack puts `user_team` where I read it. Same class as `bot_id` vs
  `bot_user_id`; must be pinned by a recorded payload in Task 4.

Measured: build 0/0; Core 36 -> 39, Chat 47 -> 50.

**Direction DONE** (Core 39, Chat 50). `ChatMembershipFacts.IsExternal` required + `Direction` derived;
`SlackEventReader` derives it from `user_team` vs `team_id` (**presence alone is not enough** -
Enterprise Grid sends it for members). Unverified offline: that Slack puts `user_team` there.

**`ChatAssessor` first increment DONE** (Assessment 131): `IChatAssessor` in Core,
`ChatAssessor` in `StyloMail.Assessment`. Pins green: PostDelivery, channel recorded, all 12 semantic
dimensions `Unavailable` with a reason, `Action == Allow` + `ProposedActionInShadow`, deterministic
signal still recorded.

**NEW DEPENDENCY EDGE:** `StyloMail.Assessment` now references `StyloMail.Chat`.

**GAP flagged:** `IAssessmentPolicyContextSource.GetAsync` is typed on `MailAnalysisInput`, so it
cannot serve chat. Chat builds a stated `PolicyContext`; **the emergency kill switch does not reach
chat**. A gap, not a decision, and it is written into the code as one.

**ADAPTIVE INCREMENT DONE (Assessment 133).** `ChatAssessor` takes `IAdaptiveProfileStore` ->
`ProfileCoordinator`. **Member path wired**: `ProfileKeyHasher.Hash(tenantId, authorId)` ->
`ProfileScopes.OutboundSender(tenantId, pseudonym)` -> `BehaviouralEvidenceEvaluator.Evaluate(
snapshot, ProfileScopeKind.OutboundSender.ToString())` -> velocity/drift evidence. No traffic class is
declared for chat, so the fan-out question is not asked (matches the mail path's rule).
**External author path is an explicit gap**: `AssessmentReasonCodes.ChatBehaviouralUnavailable`
(new const in `MailAssessor.cs`), with the behavioural ids present but `Unavailable` and a reason
naming the missing qualification. Signal id consts are on **`BehaviouralEvidenceIds`**; `SourceVersion`
is on `BehaviouralEvidence`. Adaptive namespace for the evaluator is `StyloMail.Adaptive.Signals`.

**DRIFT PIN (Assessment 134).** The both-paths version **does not exist** and I tried it four ways.
Evidence sets differ structurally at three points unrelated to composition: chat always records 12
semantic dimensions unavailable + 3 deterministic signals; mail's relationship profiles and acceptance
step change what it carries; and mail's semantic-outage override turns an outage into a declined
responsibility chat has no counterpart for (`Defer` vs `Hold`, and it persisted after switching the
override off because the acceptance step also produces `Defer`). **Delivered instead:** a pin that
chat's proposal and risk index equal what `CompositeRiskScorer` + `MailPolicyEngine` over
`Builders.Options()` produce for chat's own evidence with `Direction.Outbound`. **If chat drifts to
other weights, another engine or another direction, it fails.** Gotcha found doing it:
`Builders.Envelope()` **defaults to `Inbound`** - match the direction or you compare two paths being
asked about different things.

**SCOPE WORK DONE (2026-09-22; Core 39, Chat 55, Assessment 134, solution 1409 passed).**

- `SlackConversationType` (`Unknown/Channel/Group/DirectMessage/MultiPersonDirectMessage`) on
  `ChatMessage`, read from the event's `channel_type` (**unverified offline** - record a payload).
  `Core.ChatConversationKind` (`Unknown/Audience/People`) is what the engine sees; the connector maps.
  `ChatAnalysisInput.Conversation` added.
- **`ProfileScopeKind.ChatAuthor = 6`** + `ProfileScopes.ChatAuthor(tenantId, platform, workspaceId,
  authorKey)`. **No provenance component**; `authorKey` is a **pseudonym** (caller hashes it).
  Remarks record the migration cost of adding provenance later.
- `Core.ChatPlatforms.Slack = "slack"` - a **persisted key component**, so it must not follow an enum
  rename.
- `ChatAssessor.Behavioural` now selects the scope by direction: member -> `OutboundSender`,
  external -> `ChatAuthor`. `AssessmentReasonCodes.ChatBehaviouralUnavailable` **deleted** (dead once
  the external path was wired).
- **Gotcha:** `BehaviouralEvidenceEvaluator.Trends` yields **velocity once per window** (Burst and
  Slow), so `Assert.Single` on `behavioural.trend.velocity` fails with two matches. Use `Contains`.

**TASK 3 COMPLETE (2026-09-22; solution 1413 passed, 0 failed, 23 skipped).**

- Relationship target = the conversation, kind in the key (`{Conversation}|{channelId}` hashed).
  `Unknown` kind -> no target, `RecipientKeys` absent (not empty).
- **Observed-state write is UNCONDITIONAL**, after the assessment, to the author pool and the target
  pool. `WasRejected = false` hard-coded (nothing is refused on this path; reading it from `Action`
  would look like a measurement when it is a constant). `Dimensions = null`.
- Tests added: write goes to both pools; writes even when the proposal is not Allow; unknown kind
  writes no relationship; a DM and a channel post do not share a target key.
- `FakeProfileStore` gained `Writes` (keyed) because "a write happened" and "it went to the right
  pool" are different claims.
- **Gotcha (cost me two failures):** a behavioural signal is emitted **once per profile read**, so now
  one per scope (author + target). Match on `ObservedScope` as well as `SignalId`, never `Assert.Single`.

**ONLY REMAINING ITEM, and it is a system finding rather than chat work:** the emergency kill switch
is unreachable by EVERY path, email included. `IAssessmentPolicyContextSource` is the only shape it
can arrive through; the only implementation is `StaticPolicyContextSource` ("Supplies nothing");
nothing in the Host constructs a real one. Reported; needs a decision on where operator state lives.

**STILL TO DO in Task 3:** (a) adaptive recipient/velocity evidence via `ProfileCoordinator` +
`ProfileKey` (needs MailAssessor's `BuildProfileTargets`/`ReadSnapshots` as the template), and
(b) the cross-path drift test (push equivalent evidence down both paths, assert the same action).

### Shape (from reconnaissance)

`MailAssessor`'s ten collaborators: **reuse** `ProfileCoordinator`, `CompositeRiskScorer`,
`MailPolicyEngine`/`PolicyInput`, `IAssessmentPolicyContextSource`, `TrustedLearningGate`,
`MailAssessorOptions.Policy`. **No counterpart:** MIME analyzer, semantic classifier (local-only),
`IMessageAcceptanceQueue` (no delivery responsibility), `IRawMessageSource`, `SendingQuotaLedger`.
**Out of scope:** the campaign window (triage, plan 3). **And no queue acceptance.**

Pins, in order: every chat assessment is `DeliveryTiming.PostDelivery` (the one overview- cares about
most), the semantic gap is an explicit `Unavailable` with a reason, and nothing takes an action.

Tree was clean and frozen at `a45fee0` when the break was called. `watchdog-` reported a rate limit.

### The EvidenceBuilder move: DONE, frozen, committed as `1fe10ce`.

Approved by overview- on the same reasoning as `UrlTools`: one construction site for a convention.

- new `src/StyloMail.Core/EvidenceBuilder.cs` — `EvidenceBuilder` + `EvidenceAttributeLimits`.
  **The producer version is a constructor parameter now**, which is what lets each channel share the
  stamping while stamping its own rules.
- new `src/StyloMail.Mime/Attr.cs` — `Attr` moved unchanged (trivial constructor, no convention, so it
  stays out of Core's public surface).
- deleted `src/StyloMail.Mime/EvidenceBuilder.cs`; `MimeParseLimits` gained `AttributeBudget`;
  the one `new EvidenceBuilder(...)` site updated; Chat's producer now stamps through the shared builder.

Evidence: **Mime 91, Chat 47, Assessment 124 all unchanged.** Diffed the moved bodies first: only the
version parameter and the limits rename differ. **Checked the literal `…` in `Truncate` byte for byte
(0x2026 in both)** because non-ASCII in moved text is exactly where this session's transcription bug
would strike.

Also fixed an imprecision in my own test: the shared builder APPENDS the truncation marker, so a
truncated value is bound+1 chars; the assertion now says "within the bound or visibly marked as cut".
And corrected Task 2's line in `docs/chat-pipeline-design.md` (overview- invited it).

Measured: build 0/0, solution **1388 passed, 0 failed, 23 skipped** (unchanged, as a refactor should be).

### 2b Task 2: DONE, frozen, reported (2026-09-22). Not committed.

**Note: overview- ruled Task 1 landed as `ed79342`, and `SlackBotIdentity.None` must be a startup
failure in Task 4 (a stated requirement, not a preference).**

New in `src/StyloMail.Chat/`:
- `Slack/SlackLinkMarkup.cs` parses `<https://host|label>` into display text + destination separately.
  Bare URLs are read from a **blanked copy** of the text so the pattern stays single-sourced in Core
  and a marked-up destination is not double-counted.
- `ChatInputFactory.cs`: `ChatMessage` -> `ChatAnalysisInput`, producing `LinkObservation`s only.
- `ChatEvidenceProducer.cs` + `ChatSignals.cs`: three deterministic signals mirroring MIME's
  (`deterministic.link_display_mismatch` ratio, `.link_idn` count, `.link_idn_homograph` count),
  `SourceVersion = "stylomail-chat/1"` (ids shared with MIME, version distinct).
  `NotApplicable` on no links; homographs `NotApplicable` only when there are no IDN links.

Measured: build 0/0, solution **1388 passed, 0 failed, 23 skipped**. Chat 34 -> 47.

**DISCIPLINE DEVIATION, own it:** I wrote Task 2's tests and implementation in one pass and never ran
them red. I substituted a mutation check (producer returning no evidence -> 5/5 producer tests fail),
which proves the tests are not vacuous but does NOT replace red-first. Do not repeat this.

**Finding raised, not fixed:** `EvidenceBuilder` is `internal` in Mime and takes `MimeParseLimits`, so
chat has its own `Build` helper: a second site for the "deterministic origin cannot be forgotten"
convention. Recommended moving `EvidenceBuilder` to Core taking a bound, same as the `UrlTools` move.
Awaiting overview-'s authorisation.

### Self-loop guard made structural (2026-09-22, after access-'s security review)

`access-` verified (not asserted) that my "the reader observes, the caller judges" split left the
self-loop rule **documented and unenforced**: no production callers, `BotId` written and never read.
Right call, and their argument was `overview-`'s own principle from an hour earlier: **the guarantee
has to live in the structure, not in everyone remembering.**

**Now: `TryRead(json, SlackBotIdentity ownIdentity, out message, out reason)`**, returning
`SlackEventIgnored.FromOurBot`. A caller with no identity cannot obtain a `ChatMessage` at all.
- new `src/StyloMail.Chat/Slack/SlackBotIdentity.cs` (`BotId`, `BotUserId`, `IsOurOwnPost`, `None`)
- **Both identifiers are matched**: the event carries `bot_id` (B...) while the install gives
  `bot_user_id` (U...). Cannot verify offline which a given post carries, so match either.
- `SlackBotIdentity.None` reads everything; that degenerate state is nameable and tested.
  **Task 4 must refuse to start with no identity** (raised with both agents).

Recognising our own app is NOT judging bots in general: another integration's post is still read.

### 2b Task 1: COMPLETE, frozen, reported (2026-09-22). Not committed.

**Bot correction (overview- ruled, correcting plan 2a's own plan).** Plan 2a dropped EVERY bot message;
that rule was wider than its reason. Now: drop only the system's own posts; assess other bots and carry
`IsBot` as a membership fact (a stolen integration token posting phishing is exactly the inbound job).
- `ChatMessage` gained `BotId`; `SlackEventReader` no longer refuses bots (decision 8: observe, do not
  judge). **`SlackEventIgnored.FromABot` deleted** as unreachable.
- Bug found: Slack omits `user` on some bot posts, so removing the early return would have made them
  fail as `MissingFields`. `AuthorId` falls back to `BotId`.
- **The own-post drop now has no home until Task 4** (that is where identity lives). No live gap: nothing
  calls the reader in production yet.
- **Own bot identity comes from the install, not a per-message call**: Slack's OAuth v2 response gives
  `bot_user_id`/`app_id`, `auth.test` gives the rest. **Cannot verify offline:** the event carries
  `bot_id` (B...) while the install gives `bot_user_id` (U...), so Task 4 must compare against both the
  event's `bot_id` and `user`, pinned by a recorded payload. Also unknown offline: whether Slack even
  delivers an app its own bot's messages back.

**`ChatAnalysisInput` + `ChatMembershipFacts`** in `src/StyloMail.Core/ChatAnalysisInput.cs`. Option (a):
fixed named facts (`AuthorId`, `BotId`), `IsBot` **derived** from `BotId`, never stored. Two members
beyond the design's list, flagged: `EventId` (the ledger needs an internal message id) and `OccurredAt`
(velocity needs the message's time). A test pins `Links` as `IReadOnlyList<LinkObservation>` so a later
edit that moved judgement into the reader would fail the suite.

Measured: build 0/0, solution **1371 passed, 0 failed, 23 skipped**. Chat 30, Core 36, Assessment 124
and Mime 91 unchanged.

### 2b Task 1, first half: DONE, frozen, reported (2026-09-22)

- `MailAssessment.Channel` required (test first, red as CS0117).
- Second back-fill in `PersistedAssessmentConverter` (`ChannelContext.Email`), with the why in the code.
- `DecisionResponse` now carries `channel` AND `deliveryTiming`, so "the console shows deliveryTiming
  on every chat decision" is finally satisfiable (it was unsatisfiable on *any* decision).
- Call sites: exactly two (`MailAssessor.cs`, `Host.Tests/TestSupport.cs`), compiler-confirmed.
- **Deviation flagged:** `MailAssessor` uses `Channel = analysis.Channel`, not the literal
  `ChannelContext.Email` the doc implies. Carries the channel the input actually declared.

Measured: build 0/0, solution **1365 passed, 0 failed, 23 skipped**. Core 32, Host 305,
**Assessment 124 unchanged** (evidence the assessor edit changed nothing).

**BLOCKED on one decision: what "the platform's bounded membership facts" are** for
`ChatAnalysisInput`. Recommended (a) a small record of named platform-asserted facts (author id,
bot/app, external-to-workspace via Slack's `user_team`). Rejected (b) a `TaggedContext`-style map
("bounded" then rests on discipline, not shape). **Related finding: plan 2a drops bot messages
entirely, so an `IsBot` fact would be dead on arrival**; either the connector records the fact or the
field does not exist. That choice changes Task 4 as well as Task 1.

**Structural decision 8 (architecture.md) binds Task 2:** observation and judgement stay in separate
layers. `LinkObservation` = what the message contained (observation, belongs on the input);
`LinkFinding` = what the analysis made of it (judgement). **The chat reader must produce a
`LinkObservation` and let the analysis consume it, never produce a `LinkFinding` directly.**

## Plan 2b (assigned 2026-09-22, NEXT)

`docs/chat-pipeline-design.md` is the decision record: **chat assessments run local-only, with an
explicit semantic-unavailable state.** Principle: **input is per channel, output is shared.**
`MailAssessment` stays the output record (name is a legacy wart). Five tasks in the doc, tests first:

1. `ChatAnalysisInput` in Core + `MailAssessment.Channel` required.
2. A deterministic evidence producer for chat, reusing MIME link/homograph analysis.
3. The chat assessment path in the composition root (PostDelivery + semantic-unavailable).
4. The Slack events endpoint in the Host (url_verification, verify, dedup, normalise, ledger; ack
   fast, assess off the request path).
5. The tests that pin the four decisions, above all that every chat assessment is `PostDelivery`.

### Two findings I raised before starting (both awaiting his call)

**1. The MIME link/homograph analysis is channel-neutral but NOT reachable.** `UrlTools.cs` is pure
BCL (System.Globalization/Net/Text, zero MimeKit) and holds `Observe`, `InspectIdn`, `DomainFamily`,
`ToAsciiDomain`, `NormalizeAddress` plus `IdnObservation` (Scripts/IsMixedScript/Confusables/
AsciiSkeleton). The MIME-specific part is only the entry point:
`LinkExtractor.Extract(HtmlAnalysis html, string plainBody, MimeParseLimits limits)` wants
`HtmlAnalysis` and `MimeParseLimits` even for the plain-text scan. Everything is `internal` with no
`InternalsVisibleTo`. Options: (a) widen Mime's API (drags **MimeKit 4.18.0** into Chat), (b)
`InternalsVisibleTo` (avoid), (c) **move the pure URL/IDN analysis into Core** (BCL-only, so Core's
"references nothing" holds). I recommended (c); it touches `mime-`'s lane, so it is his call.
Note: Slack's `<http://host|label>` markup is genuinely new and belongs in `StyloMail.Chat`.

**2. A required member on `MailAssessment` is not readable back out of the ledger.**
`SqliteDecisionLedger` stores the assessment as a JSON blob and reads it with
`JsonSerializer.Deserialize<MailAssessment>(payload, HostJson.Options)`. `HostJson.Options` is plain
web defaults plus a string-enum converter, and STJ enforces `required` on deserialization (proven
outside the repo: `JsonException ... was missing required properties`). `RespectRequiredConstructorParameters
= false` does NOT relax it. `SqliteSchema` has no migration/wipe path (`CurrentVersion = 1`).
**Already live from plan 1:** rows written before `6b11add` lack `DeliveryTiming` and are unreadable.
Back-fill is honest (not a guess) because `MailAssessor` is the only production construction site and
it is the email path, so every legacy row really was `Email`/`PreAcceptance`. Recommended: a
`JsonConverter<MailAssessment>` on the persisted read path only. **The missing test is a
deserialization of a payload written by an older shape.**

### Blast radius for `MailAssessment.Channel` (measured, read-only)

Only two construction sites exist: `src/StyloMail.Assessment/MailAssessor.cs:511` and
`tests/StyloMail.Host.Tests/TestSupport.cs` (`Build(...)`, target-typed `new()`), plus the new
reflection test in `ChannelContractTests.cs`.

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
