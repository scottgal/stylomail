# overview- resume doc

Read this first after a compaction. It is a snapshot, not a history. Refresh it before the next one.

## Who I am

`overview-`, the architect for **StyloMail**. I own the spec, the architecture, the model policy, the
fleet roster, the environment control plane, `Core`/`Jev`/`Policy`, and I commit the fleet's work.
**I do not implement features; I spawn agents and keep the shape.** The kill switch was the exception
and it was mine by ownership.

Operator corrections that bind, all of them blunt:
- "You should have agents do it" and "You keep the overall shape and decisions".
- Spawn on `runtime: claude-deepseek`, `model: deepseek-flash`. A `claude`/`sonnet` pair silently exits.
- **No em-dashes anywhere.** Use a colon or a full stop. The check is a `grep -c` over the files you
  touched for **U+2014**, written as a codepoint here so that the rule does not itself contain the
  character it forbids and leave the check returning one forever.
- **Specialists own an area persistently; they are not workers moving through a task list.** "Tired"
  is not a constraint, a finite context is handled by the checkpoint rather than by stopping early,
  and the only legitimate pause is a decision the agent cannot make. The operator corrected me for
  drifting here, and it is the drift that sounds most like care.
- **Do not over-claim context pressure.** The cockpit's notices are guidance, not a stop signal. Keep
  working until a decision or a real limit stops you.

## What StyloMail is

An adaptive two-way email security proxy with a **second channel family** (chat) added beside it.
Three commitments: probabilistic components produce **evidence** and only deterministic policy
authorises **side effects**; **unknown is a distinct state**, never a zero; **intervene minimally**.

Spec: `.styloagent/spec.md` (§13 is chat). Architecture: `.styloagent/architecture.md`. Chat design:
`docs/chat-channels-design.md`, `docs/chat-pipeline-design.md`, `docs/chat-channels-plan-03-triage.md`.

## Repo state

- Root `/Users/scottgalloway/RiderProjects/stylomail`, remote `https://github.com/scottgal/stylomail`
  (private), branch `main`. Twelve source projects, fourteen test projects.
- **`main` at `2236af0`, tree clean, 1465 tests passing, 0 failing.** Everything pushed.
- `dotnet` is **not on PATH**: `export DOTNET_ROOT=/usr/local/share/dotnet` and
  `export PATH="/usr/local/share/dotnet:$PATH"`. Solution is `StyloMail.slnx`. Analyzers are errors.
- **Never count test totals through an inline pipe** (`| awk` on a truncated `tail` silently drops
  projects). Print the per-project lines and sum them where the output can be seen.
- **Verify a lane in a detached clone, not in a working tree that compiles.** A working tree cannot see
  a file that was never committed, or a required member that breaks deserialisation.

## How commits work here

Agents' missions say **no `git add`/`git commit`**; the model is that agents produce and I commit their
lanes after verifying the solution. `desktop-` is the exception and commits its own lane.

**Never `git commit --amend` or `git reset` in this tree.** Several agents share it and `--amend`
targets whatever HEAD happens to be; I destroyed a commit `desktop-` landed that way.

Commit an agent's lane by explicit path, and **hold when the tree is mid-increment** rather than
committing an unreported half.

## Rules this session produced

1. **A measurement beats a diagnosis.** `access-` wrote a regression test that passed while its
   diagnosis said it should fail, explained it away, and filed a high defect that did not exist.
2. **A report describes a frozen tree, and its numbers are the ones measured on it.** A lane mid-cycle
   is indistinguishable from a broken one.
3. **A test proves what it exercises, not what it was written about.** Three green results in the chat
   lane described something other than what they appeared to; all three were found by reading the call
   site rather than the unit.
4. **A fixture that reports success at something it did not verify will mimic a defect in the code
   under test.** Twice: `WithResourceMapping` not placing a file, and a port wait that does not wait
   for the account to exist.
5. **Never duplicate a security-relevant heuristic.** Two copies drift, and the one that drifts is the
   one nobody re-reads.
6. **A gap must be visible in the output**, not in a conversation.
7. **Signal is not state, and hint is not truth.** The console re-reads; the emitter never pushes state.
8. **Input is per channel, output is shared**, and `MailDirection` is derived, never defaulted.
9. **Error here, not there.** A refusal that a store three layers down throws is an exception, not a
   sentence telling the operator what to type.

## The fleet

| Owner | Owns | State |
| --- | --- | --- |
| `chat-` | **The chat extension**: Core contract, Slack ingress, evidence, assessment path, plan 3 triage | **working, plan 3 check 3 next** |
| `desktop-` | Avalonia operator console | idle |
| `hub-` | Live traffic events, `ITrafficEvents` | idle, merged |
| `access-` | IMAP/POP3 proxy **and the protocol harness** | idle, both lanes complete |
| `mime-`, `adaptive-`, `queue-`, `host-` | their lanes, merged | exited |
| `assess-`, `transport-`, `ingress-` | their lanes, merged | dehydrated |
| `keys-` | minted keys, principal store, `stylomail key` | exited; **unowned now, route to me** |
| `agent-1-` | *(none set)* | exited |
| `agent-2-` | *(none set)*, **renamed "article"**, a Codex runtime agent | working |

**`agent-1-` and `agent-2-` were spawned directly by the operator, not by me**, and neither has a
responsibility set. So do not assume what either owns: ask before routing work to it, and ask before
touching a file it may be in. They are depth 1 under `overview-` in the graph because everything is.

The roster also shows `access-`, `desktop-` and `hub-` back to **working** after I had them idle, so
the operator has been waking lanes. The fleet is larger than the cap in `fleet.yaml` says, which means
the cockpit's limit has been changed rather than that the file is being honoured.

The **Host has no owner** and neither does `Policy` except me. `overview-` holds Core, Jev and Policy.

**The fleet cap is 12 and enforced by the running cockpit**, which does not re-read `fleet.yaml` (I set
it to 16). Only the operator can raise it.

## What is done

- **The emergency kill switch is wired** (`adf2012`): `IEmergencyKillSwitch` as its own port,
  `SqliteEmergencyKillSwitch` persisting transitions, `HostPolicyContextSource` supplying it to the
  mail path, `ChatAssessor` reading the same port, and `stylomail killswitch engage|disengage --by`.
  It was false on every assessment in every deployment before this.
- **Issue list reconciled**: 19 entries to 8 genuinely open. Six were already fixed, resolved or
  retracted and still said open.
- Chat: plans 1, 2a and 2b complete; plan 3 (triage) has the outcome shape and checks 1 and 2.

## Eight issues remain open

Medium and low only, all in lanes whose owners are parked or exited: two IMAP robustness gaps
(`access-`), the mutation-sweep isolation rule, the SMTP listener's shutdown (`transport-`), two
`SqliteSchema` problems, the queue suite's non-determinism, and the intermittent schema test.

## Next step

1. **Rule on chat-'s check 2 basis** if it asks (it is building check 3 now). Anything it brings is a
   design call and mine.
2. The eight open issues, by whoever can take them; the fleet is full, so they need owners or me.
3. The chat three-platform-facts capture, which the operator deferred with "capture later".

**The chat lane is built, not proven**: nothing in it has been exercised against a real Slack
workspace, so every claim about Slack's payloads is a claim about its documentation. Say "built"
rather than "proven" until a capture exists.
