# overview- resume doc

Read this first after a compaction. It is a snapshot, not a history. Refresh it before the next one.

## Who I am

`overview-`, the architect for **StyloMail**. I own the spec, the architecture, the model policy, the
fleet roster, the environment control plane, `Core`/`Jev`/`Policy`, and I commit the fleet's work.
**I do not implement features. I spawn agents and keep the shape.**

Operator corrections that still bind:
- "You should have agents do it" and "You keep the overall shape and decisions".
- Spawn agents on `runtime: claude-deepseek`, `model: deepseek-flash`. A `claude`/`sonnet` pair silently
  exits without erroring.
- **No em-dashes anywhere.** Not in prose, comments, commit messages or replies. Use a colon or a full
  stop. Substitute by codepoint, not literal:
  `perl -CSD -i -pe 's/\s*\x{2014}\s+/, /g; s/\x{2014}/-/g'`, then read it, because a comma for a
  clause separator creates run-ons.

## What StyloMail is

An adaptive two-way email security proxy: an SMTP security edge in front of back-end mail servers. It
terminates TLS, authenticates senders, bounds volume and owns the queue. It detects suspicious
**communication** rather than suspicious words.

Three commitments everything follows from: probabilistic components produce **evidence** and only
deterministic policy authorises **side effects**; **unknown is a distinct state**, never a zero score;
**intervene minimally**, so thin evidence yields a bounded hold rather than an irreversible rejection.

## Repo state (verified this session)

- Root `/Users/scottgalloway/RiderProjects/stylomail`, remote `https://github.com/scottgal/stylomail`
  (**private**), branch `main`. Twelve source projects, twelve test projects.
- `dotnet` is **not on PATH**: `export DOTNET_ROOT=/usr/local/share/dotnet` and
  `export PATH="/usr/local/share/dotnet:$PATH"`. SDK 10.0.201. Solution is `StyloMail.slnx`, not `.sln`.
  Analyzers run as **errors**.
- Spec: `.styloagent/spec.md`. Architecture: `.styloagent/architecture.md`. Those two are the ground.
- **`.gitignore` was silently excluding an entire source directory.** A bare `profiles/` in the
  runtime-state block also matched `src/StyloMail.Adaptive/Profiles/`, so eight Adaptive source files
  were never in the repository at all. **No commit in this repo has ever built from a fresh clone.**
  Fixed and pushed at `f2bc1f7`; the rules are now anchored to `**/data/...`. Do not re-add a bare
  `profiles/`, `spool/` or `quarantine/`.
- **A working tree that compiles hides what the repository does not contain.** Verify with a detached
  worktree, never by building in place:
  `git worktree add --detach /private/tmp/check <sha>` then `dotnet build StyloMail.slnx` and
  `dotnet test StyloMail.slnx` there. `git worktree prune` when the tree is deleted with `rm -rf`.

## How commits work on this fleet

Agents' missions say **"No `git add`/`commit`"**, beside "watching the solution is `overview-`'s job".
The model is: **agents produce, I commit their lanes after verifying the solution.** `desktop-` is the
one mission without the prohibition and commits its own lane cleanly.

This is why the backlog accumulated: the previous session did not do the committing job, then reported
the tree "in sync". When a lane reports done, commit it. Check `git log -- <path>` to confirm a lane is
actually committed rather than trusting a report, and re-run the suite because a lane that is green in
the shared tree is not proof the *commit* is green.

**Never `git commit --amend` or `git reset` in this tree.** Multiple agents commit concurrently and
`--amend` targets whatever HEAD happens to be. I amended a commit `desktop-` landed between my commit
and my amend, destroying its message. The blemish is in history at `f2bc1f7`: it carries
`desktop--context.md` under my "Restore the Adaptive profile types" message, and `desktop-`'s own
checkpoint message ("Checkpoint desktop- context: management surface started", `1600666`) was
overwritten. Content is intact; the record is not. Plain `git add` of an explicit path list, then
`git commit`, and nothing else.

## Rules this session produced, all of them paid for

1. **When a measurement contradicts a diagnosis, the measurement wins, and an explanation that
   preserves the diagnosis is the thing to distrust.** `access-` wrote a regression test that passed
   while its diagnosis said it should fail, explained it away as "socket versus pipe", and filed a
   high-severity defect that did not exist. Bisecting found the truth. The cost was a fabricated
   workstream committed to `149f25e` and retracted an hour later.
2. **A completion report describes a frozen tree, and its numbers are the numbers measured on it.**
   A lane mid-cycle is indistinguishable from a lane that is broken, and only the freeze
   distinguishes them. When a report and my measurement disagree, re-run before concluding either way:
   a single failure followed by two clean runs is a flake or a moving tree, not a result.
3. **Verify in a detached clone, never in a working tree that compiles.** A working tree that builds
   cannot see a file that was never committed, and it cannot see a required member that breaks
   deserialisation. Eight Adaptive sources were absent from every commit in this repo's history while
   every local build passed.
4. **Never `git commit --amend` or `git reset` here.** Several agents share this tree. I destroyed a
   commit `desktop-` landed between my commit and my amend. Plain path-list `git add`, then `git
   commit`, nothing else.
5. **Agents do not commit; I commit their lanes.** Eight of nine missions forbid it. Report done, I
   verify the solution, then commit. The previous session skipped that job and reported the tree
   "in sync" with thirty modified source files in it.
6. **A gap must be visible in the output, not in a conversation.** A red test whose reason lives only
   in a message thread is not visible to the next person. `[BlockedHarnessFact(reason)]` is the shape
   that works: it skips, and the reason prints where a reader looks.
7. **Never duplicate a security-relevant heuristic.** The IDN and homograph analysis moves to Core
   rather than being copied into the chat connector: two copies drift, and the one that drifts is the
   one nobody re-reads.
8. **An in-memory fake is ours, and that is its limit.** The protocol harness found two defects that
   no in-memory suite could have: the first because the fake backend was ours, and the second because
   the fake client was ours. Put real clients on real sockets against real servers, or the suite only
   tests our assumptions about them.
9. **Specialists own an area persistently. They are not workers moving through a task list.** A plan's
   tasks are how ownership is expressed right now, not a queue to complete and step away from between
   items. Never frame a lane as something to pause between tasks, to "start fresh" later, or to hand
   back because a session has run long. **A finite context is handled by the checkpoint, not by
   stopping early:** when it fills, write down where you are and carry on. The operator corrected me
   for exactly this drift, and it is the easiest one to fall into because it sounds like care.
   The one legitimate pause is a decision the agent cannot make; that is not the same as an hour
   being late.

## The fleet

| Owner | Owns | State |
| --- | --- | --- |
| `chat-` | **The chat channel extension**: Core contract, Slack ingress and evidence, assessment path, later triage and Discord | **working, plan 2b Task 3** |
| `desktop-` | Avalonia operator console | idle |
| `hub-` | Live traffic events, the `ITrafficEvents` seam | idle, lane merged at `e06e7d8` |
| `access-` | IMAP/POP3 proxy **and the protocol harness** | idle, both lanes complete |
| `mime-`, `adaptive-`, `queue-`, `host-` | their lanes, all merged | exited |
| `assess-`, `transport-`, `ingress-` | their lanes, all merged | dehydrated |
| `keys-` | minted API keys, principal store, `stylomail key` CLI | **exited, lane merged; unowned now, so route to me** |

## Ruled this session, and still binding

**Minted keys:** precedence total and never merged across store and environment; `key list` reports
the resolving source; `key revoke` refuses an environment principal and names the config that owns it;
a slow KDF with per-key salt and constant-time comparison; revocation defeats any cache; `key create`
prints once to stdout.

**SignalR hub:** last, behind a default-off flag, no pipeline code depending on it, and an emission
that cannot fail an assessment or a delivery. Built and merged.

**Chat:** `DeliveryTiming` and `Channel` are `required`, never defaulted, because a default would let
a chat assessment claim it could have stopped a message it only reacted to. `MailDirection` is
**derived from membership, never defaulted**: inbound and outbound statistics are never merged into
one pool. The external author gets a **distinct scope kind with no provenance**, because the platform
asserts the identity rather than the message claiming it. The **target of "where it went" fills the
recipient slot with its conversation type in the key**, so "talking to new people" and "posting in new
channels" are never merged into one number. The emergency kill switch must reach chat.

**Posture and `notificationTarget`** are stored and shown but read by nothing, labelled as such. A
control that looks like it works is a false statement about the system.

## In flight

- **`chat-`, plan 2b Task 3** (`docs/chat-pipeline-design.md`): the member path, velocity and drift,
  and the external author's explicit `Unavailable` are in. The observed-state write, the fan-out half
  and the external scope follow the rulings above. **The cross-path drift pin I required is currently
  red** while it is mid-work, which is red-first as asked.
- Plans 2b Tasks 4 and 5 remain: the Slack events endpoint in the Host, and the tests that make the
  decisions real. Then plan 3, triage.
- The **protocol harness** is complete for tier one (`docs/protocol-harness-plan-01.md`). Its next
  tiers, SMTP and the upstream MTA handoff, then Cloudflare and OAuth, are **not yet written**.

## Open for the operator

- **LICENSE** for the repository: not chosen.
- **Desktop distribution**: self-contained single-file, or developer-only.
- **AccessProxy**: durable credential store unowned, per-tenant key isolation unmet, and SMTP
  submission deferred behind retrieval. The harness wrote the untested Gmail behaviour down:
  `tests/StyloMail.Integration.Tests/UNTESTED-GMAIL.md`, and the gap is at session establishment,
  because the proxy advertises its own capability list.
- **The fleet cap.** `.styloagent/fleet.yaml` says 16 but the running cockpit enforces 12 and does not
  re-read the file, so only the operator can raise it.

## Next step

`chat-` finishes Task 3 and the lane is committed when green. After that: Task 4, the Slack events
endpoint, where the recorded platform payloads settle `user_team`, `bot_id` versus `bot_user_id`, and
the conversation type, and where the own-post drop finally has a production home.
