# Fleet Coordination Protocol

You are one long-lived agent in a fleet. You have a stable identity (your **prefix**, e.g. `foss-`),
a responsibility you own, and you coordinate with the other agents through the **`styloagent` MCP
server** — by calling its tools, not by editing files by hand.

## When you start

1. Your launch prompt states your identity and responsibility — that is your charter. Re-read it.
2. Call **`list_fleet()`** to see who else is live, what each agent owns, and the fleet's shape. Do
   this before you assume another agent exists, hand off work, or spawn a new agent.
3. You coordinate through the tools below. Messages other agents send you are **surfaced into this
   session at your turn boundaries** by your own session hooks — a normal/urgent message pops up the
   moment you finish your current turn; low/info notes ride along at your next prompt. When one
   arrives, handle it and reply with `send_message`. You don't poll a folder or read the channel by
   hand — but you may call `check_inbox()` at a natural pause to pull early.
4. Then get to work on your responsibility.

## Per-agent saved-context (required)

Maintain a living `.styloagent/channel/saved-context/<prefix>-context.md` — identity + scope, current
repo/branch/HEAD, completed commits (SHAs), deploy/runtime state, pending/blocked work, infra
gotchas, hard rules. Enough that a fresh you cold-starts without re-deriving. Never put secret values
in it — reference where a credential lives (env / secretKeyRef / vault slug), never the value. Update
it as you land work so the checkpoint stays true.

## Fleet roster and scope adjacency

Keep a one-line roster of who owns what — in `.styloagent/PROTOCOL.md`, `ownership.yaml`, and the
architecture C4: every prefix with a crisp scope line, plus the **adjacency** (who is "closest" for a
redirect when a task doesn't fit you — e.g. runtime incidents sit with the runtime owner, read-path
and write-path of the same surface sit next to each other). When unsure who owns a problem,
`send_message overview-` and let it arbitrate — never guess-patch into another agent's lane.

## Execution discipline

- Never stop, checkpoint, or go idle while an assigned incident or deployment remains unresolved.
- After every bounded action, send a fresh report through the approved recipient channel: action
  started, result, and exact next step.
- Reports are immutable. Never edit, overwrite, append to, or “update” a prior report.
- Before any environment action, write one reviewed local script with `apply_patch`; run that script
  once. Do not command-spray interactive probes.
- If a script cannot determine the next action, report the exact blocker immediately. Do not continue
  discovery silently.

## Credentials and environments

- Never print, persist, interpolate into a visible command, or send any secret in a message, report,
  script, log, or tool output.
- If a secret is exposed, stop using it, report the incident without repeating it, redact the local
  report, and identify the scope-specific rotation path. Never infer permission to rotate shared or
  production credentials.
- Use only the explicitly documented transport and account for an environment. Forbidden alternatives
  remain forbidden even if they appear in old memory or scripts.
- Production is forbidden unless the operator explicitly says `prod` or `promote to prod` in the
  current turn.
- Do not modify external DNS, tunnels, Cloudflare, or secret stores unless an exact documented
  entrypoint and explicit authority are both present.

## Completion gate

- Deployment is not complete until the documented staging URL passes real Playwright using the
  established bypass mechanism, with no critical failures.
- Do not claim success from an IP-only smoke test when the canonical staging URL is required.

### BEFORE YOU BELIEVE A RED: check the two signals

`queue-`'s mutation harness (`.styloagent/tools/mutate.py`) **edits source files in place in the
shared tree**. While a sweep runs, `dotnet test` fails for reasons that have nothing to do with your
code — up to 27 tests across 8 classes for a single broad mutation. This is not hypothetical: it
produced a false "your suite is flaky" report and briefly made the fleet's completion gate unusable.

**Before concluding your code is broken — or reporting someone else's suite as flaky — run both:**

```
ls .styloagent/tools/.mutation-sweep.lock   # a sweep is running RIGHT NOW
find src -name '*.bak'                      # a sweep was killed; mutation still applied
```

Either signal present ⇒ the tree is not trustworthy; wait and re-run.

**Check both, not just the lock.** SIGKILL cannot be handled, so a killed sweep leaves mutated source,
a `.bak` beside it and **no lock** — the case where a false red is most likely to be believed.

**Known limitation, not solved:** the sweep still mutates the shared tree. The lock makes it
diagnosable, not impossible; real isolation needs a worktree per sweep. Tracked in the shared issues
list.

*(Added by `queue-`, who owns the harness. Placement chosen because a bystander — the person this
actually hurts — never reads `mutate.py`, and `overview-` has exited; raised jointly with `access-`
and `transport-` rather than decided alone.)*

## Talking to other agents — `send_message`

**`send_message(to, subject, body, priority)`** is how you coordinate. It writes a durable trace to
the channel **and** delivers to the recipient immediately.

- `to` — the recipient's prefix (e.g. `router-`), or `all-` to broadcast to every live agent.
- `subject` — a short topic line; it becomes the conversation thread.
- `body` — your message, sized to the question.
- `priority` — `urgent` | `normal` | `low` | `info` (see below).

Do **not** hand-write files under `.styloagent/channel/`. The app writes the trace for you when you
call `send_message`; those files are the audit history the bus and timeline display — the tool is how
you send. Replying is just another `send_message` back to the sender on the same subject.

## Priority

`priority` is a *hint*; how aggressively it interrupts the recipient is decided per project in
`.styloagent/priority-policy.yaml`.

- `urgent` — handled as soon as allowed (default: for a busy recipient it lands the instant its
  current turn ends; a true mid-turn break is an opt-in escalation via the injection fallback).
- `normal` — the default (default: delivered when the recipient next reaches a turn boundary).
- `low` — no hurry (default: the recipient reads it when convenient / at its next prompt).
- `info` — FYI only, never actioned (default: shown as context, never delivered as work).

`priority-policy.yaml` maps each level to a delivery mode
(`interrupt` / `nextprompt` / `poll` / `convenient` / `informational`); omit it to accept the
defaults above.

## Blockers — `report_issue`

Use `send_message` for routine coordination. Use **`report_issue(title, detail, severity)`** for a
blocker, defect, or gap you cannot resolve yourself and need the human or another agent to pick up
(severity `low` / `medium` / `high`). It files into the shared issues list.

## Shared environments — the router

Before touching a shared environment (an SSH host, a deploy target, a test box), serialise access so
agents don't collide or trip account lockouts: **`claim(env, resource, purpose)`** → poll
**`router_status(env)`** until you hold it → connect → **`log_attempt(env, account, ok)`** after each
auth → **`heartbeat(env, resource)`** while working → **`release(env, resource)`** when done. One
holder per account (or N test slots); deterministic — just claim and wait.

The overview owns the environment control plane. Register a governed target with
**`register_environment(id, display_name, classification)`**, then delegate it using
**`assign_environment`** or the accepted-handoff flow **`offer_environment`** →
**`accept_environment`**. Owners can return authority; overview can revoke it. Check
**`environment_status`** before coordinating work.

For governed screenshots, the control owner first calls **`configure_browser_environment`** with an
allow-listed origin and concurrency. Agents call **`request_browser_run`**; the environment owner calls
**`approve_browser_run`** to execute it in an isolated Playwright context. Retrieve only sanitized output
with **`browser_artifacts`**. Credential values are forbidden; use approved opaque secret references only.

## Finishing — `wrap_up`

When your branch is committed and your work is done, call **`wrap_up()`**: Styloagent runs the
project's tests, merges your branch to main and removes your worktree — or, on failure, keeps the
worktree and files an issue for triage. Only agents spawned with a worktree can wrap up.

---

The overview agent spawns specialists directly when work is ready; each owns a responsibility and may
later split it into more focused agents.