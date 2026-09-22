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
| `mime-` | Mime plus deterministic evidence | complete, 91 |
| `adaptive-` | Profiles, drift, velocity, learning | idle, committed at `9be37c1` |
| `queue-` | Queue, spool, delivery worker | idle, committed at `05f3e31` |
| `host-` | Host, handed over on budget | stopped |
| `ingress-` | **Host current owner** | working: management surface; lane committed at `1f9cf98` |
| `assess-` | Composition root plus semantic cache | idle, committed at `0a62690` |
| `transport-` | SMTP/MTA plus Cloudflare | complete, 192 |
| `access-` | IMAP/POP3 proxy, credential seam | complete, 61 |
| `desktop-` | **Avalonia operator console** | working; commits its own lane |

## Ruled this session (delivered to `ingress-`)

Minted API keys, direction already decided by the operator, approved with guards: precedence is total
and **never merged** across store and environment (a privilege union is a silent escalation);
`key list` reports which source resolved each principal; `key revoke` refuses on an environment
principal and names the config that owns it; the digest is a **slow KDF** with per-key salt and
constant-time comparison, not a bare hash; revocation must defeat any resolution cache; `key create`
prints once to stdout and to nothing else.

SignalR hub: approved **last**, behind a **default-off** flag, with the hard rule that **no pipeline
code may depend on it** and an emission must never be able to fail an assessment or a delivery.
Emission belongs at `ingress-`'s own boundary (ledger writes, listing routes, delivery-worker hosting)
before any other lane is asked for a hook.

Also adopted: `posture` and `notificationTarget` are stored and shown but read by nothing, and are
labelled as not yet acted on. A control that looks like it works is a false statement about the system.

## In flight

- `ingress-`: the management slice (sender settings, companies). `docs/console-management-design.md`
  is the design of record, committed at `a16174c`.
- `desktop-`: the API key entry screen and the connection screen.

## Open for the operator

- **LICENSE** for the repository: not chosen.
- **Desktop distribution**: self-contained single-file like mylo, or developer-only.
- ~~Desktop console authentication~~: answered, API key entry screen plus a `stylomail key` CLI.
- **AccessProxy**: durable credential store unowned, per-tenant key isolation unmet, SMTP submission
  deferred behind retrieval.

## Next step

Get `ingress-`'s management slice committed when it is green, and have `desktop-` run the live console
smoke (`ux-scripts/run-console-smoke.sh`) against the committed Host rather than the working tree.
