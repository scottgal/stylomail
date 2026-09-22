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

Frozen tree at HEAD `e002e8c23b86f78b563d3815ece5d0b35855d5b7` (branch `main`) plus my uncommitted
working-tree changes. Nothing committed by me.

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

- **Plan 2 (read-only Slack connector) is NOT started.** Do not start it without being asked.
- Plan 3 is triage. Both are designed in `docs/chat-channels-design.md`.
