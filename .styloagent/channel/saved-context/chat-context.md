# chat- : the chat channel extension (resume doc, distilled 2026-09-23)

## Who I am

Owner of StyloMail's second channel family: the channel-neutral Core contract, the Slack and Discord
connectors, and the triage layer. Parent: `overview-`. **No worktree**: I work in the shared main tree.

## Hard rules, all of them binding

- **I do not `git add` or `git commit`**, never `--amend`, never `reset`. My mission and `overview-`
  both say so explicitly and it has held for every lane. I leave work in the tree and report; he
  verifies and commits. **The cockpit's "commit your WIP atomically" advice does not apply here**, and
  on this tree it would be actively wrong: `src/StyloMail.Host/Hosting/HostServices.cs` currently
  holds both my recorder registration and `overview-`'s kill-switch work.
- Build: `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`.
  Solution is `StyloMail.slnx`, never `.sln`.
- **Analyzers are errors.** A warning is a failed build. (See CA1861 and CA1822 below.)
- **Never an em-dash**, in code, comments, docs or commit messages. Use a colon or a full stop.

## The one fact that shapes everything

Email lets StyloMail decide before delivery. A normal Slack app receives a `message` event **after**
delivery. So this is an **observer with post-hoc interventions, never a proxy**, and every chat
assessment records `DeliveryTiming: PostDelivery` as a stated fact.

## Where the lane stands

Plans 1, 2a and 2b (Tasks 1 through 5) are **complete and committed**: `6b11add`, `66a0da6`, `db4a0e8`,
`1fe10ce`, `9ece646`, `df4e6dc`, `c31380a`, `9c04d71`, `1bb1603`, `8bb4991`, plus the plan-3 triage
work `overview-` has committed since.

**What the extension does end to end:** a signed Slack event arrives at `POST /v1/ingress/slack`, is
verified as raw bytes, has the challenge handshake echoed, is refused if it is our own app's post, is
**persisted before it is acknowledged**, and is drained off the request path through triage into an
assessment that records `PostDelivery` and an explicit semantic gap, then into the ledger.

### Plan 3 (triage), the current task

`docs/chat-channels-plan-03-triage.md` is the decision record, approved. **Every check is specified by
which way it fails**: dismissing what should have been looked at is a missed detection, escalating
what did not need it is money. Rules `overview-` settled: counts not rows, **visible on the operator
surface**; triage runs **in the drain before the assessor**; the near-duplicate threshold **starts
conservative and stays**.

- **Check 1, scope: DONE.** Empty watched set watches nothing, a decision rather than a default.
- **Check 2, near-duplicate: DONE.** Basis is **normalised text + link destinations** in one
  fingerprint, exact match, "near" deliberately not attempted. The fifty-first message escalates **by
  construction**. Empty basis agrees with nothing.
- **Check 3, links: DONE.** **No dismiss disposition at all**: a lure escalates, clean links continue.
  (An earlier version of the record said "safer error: escalate" as though it were a disposition; that
  was a failure direction read as an outcome and is corrected in the record.)
- **Drain integration + separated write: DONE, uncommitted** (see below).
- **Check 4, behaviour: NOT DONE.** The one whose safer error depends on whether anything acts, so its
  disposition is a function of what the deployment has enabled. Write it with that stated.
- **Campaign wiring into the drain: NOT DONE**, and it makes a path unreachable (below).
- **Counts on the operator surface: NOT DONE.** Part of the work, not a follow-up.

## Uncommitted working set, mine

- `src/StyloMail.Assessment/ChatObservationRecorder.cs` (new): the profile keys and the observation,
  shared so the read and the write cannot disagree about which profile a message belongs to.
- `src/StyloMail.Assessment/ChatAssessor.cs`: delegates to the recorder.
- `src/StyloMail.Host/Chat/ChatIntakeDrain.cs`: runs triage before the assessor.
- `src/StyloMail.Host/Hosting/SlackIngressOptions.cs`: `WatchedChannels`.
- `src/StyloMail.Host/Hosting/HostServices.cs`: recorder registration. **Also holds `overview-`'s
  kill-switch work**, so do not treat the whole diff as mine.
- `tests/StyloMail.Host.Tests/ChatIntakeDrainTests.cs`.

Other modified files in the tree (`src/StyloMail.Desktop/*`, `ux-scripts/*`, `tests/StyloMail.Desktop.Tests/*`,
`docs/blog/*`) belong to `desktop-`. **Do not touch them.**

## Two findings I raised and did not fix

1. **The drain builds its triage context without a campaign**, so check 2 can never settle in
   production and the record-but-do-not-assess path is **unreachable** until it is wired.
2. **Enabling the ingress with no `WatchedChannels` dismisses everything on scope.** That is the
   designed decision, and it is exactly why check 1's dismissal count must be visible.

## Rules learned the hard way, with the reason

- **Derive the direction from the author's relationship to the workspace.** A member is an
  authenticated principal and is `Outbound`; an external author is `Inbound`. I proposed always-Inbound
  and was refused: compromised-account detection *is* the outbound case, and the pools are never merged.
- **A required persisted member needs a read-path back-fill.** `MailAssessment` is stored as JSON and
  `required` is enforced on deserialisation, so a member added later makes older rows unreadable.
- **Persist before ack.** Slack's ack is this path's `250`; answering and then losing the event is
  accepting a responsibility we cannot honour.
- **A conditional registration at composition-root time reads the wrong configuration.** Gate at
  resolution, not at registration: the failure mode is silence.
- **A fixture I write cannot settle a platform fact.** It encodes the assumption and then carries the
  authority of a measurement. The three Slack flags (`user_team`, `bot_id` vs `bot_user_id`, the
  conversation-type field) stay flagged until a real capture exists; the operator chose "capture later".
- **A test proves what it exercises, not what it was written about.** Put the test where production
  makes the call. Three green results in this lane described something other than what they appeared to.
- **Never count test results through an inline pipe.** Print per-project lines and sum them; a pipeline
  silently dropped lines and printed 1428 where the truth was 1447.
- **`awk -F'Passed: +'` undercounts by one.** Use `python3` over `Passed:\s+(\d+)` per line.
- **A mutation probe must still touch instance data** or CA1822 fails the build and the probe reports
  nothing while looking green.
- **CA1861 is an error here** (constant array arguments) with no root `.editorconfig` to explain it.
- **Construction sites hide in target-typed `new()`.** Grep for the type name, or use
  `dotnet build StyloMail.slnx | grep "error CS9035"`.
- **Before believing a red**, check `ls .styloagent/tools/.mutation-sweep.lock` and
  `find src -name '*.bak'`.
- **`nameof(Type.Member)` is compile-time**, so a test naming a not-yet-existing member fails as
  CS0117/CS0103 rather than at runtime.
- **A behavioural signal is emitted once per profile read** (velocity once per window), so
  `Assert.Single` on one is wrong by construction. Match on `ObservedScope` too.
- **The class cannot be named `Triage` in namespace `StyloMail.Assessment.Triage`**: the compiler
  resolves `Triage.Evaluate` as a namespace lookup. It is `TriageEngine`.

## Reading order for a cold start

1. `docs/chat-channels-design.md` (the design of record)
2. `docs/chat-channels-plan-03-triage.md` (the current plan)
3. `docs/chat-pipeline-design.md` (local-only first, and why)
4. `.styloagent/spec.md` sections 13 and 4; `.styloagent/architecture.md`

## Lane boundaries

**Do not touch:** `src/StyloMail.Desktop` (desktop-), `src/StyloMail.Host/Traffic/` and
`tests/StyloMail.Host.Tests/Traffic*` (hub-), `src/StyloMail.Queue`, `src/StyloMail.Policy`,
`src/StyloMail.Transport`, `src/StyloMail.AccessProxy`. `overview-` authorised changes inside
`src/StyloMail.Mime` for the two Core moves and inside `src/StyloMail.Assessment/Campaign` for the
fingerprint fallback; both are done and committed.

**Not mine:** the emergency kill switch. It was unreachable by every path and I reported it; `overview-`
built it and wired `ChatAssessor` to read it through `IEmergencyKillSwitch`.

## Next step

**Check 4, behaviour**, then the campaign wiring, then the counts. Failing that, the highest-value
thing left is the campaign wiring, because it is what makes the separated write reachable at all.
