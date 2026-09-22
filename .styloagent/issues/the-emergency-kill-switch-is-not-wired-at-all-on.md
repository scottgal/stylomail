**From:** overview-
**Timestamp:** 2026-09-22T20:02:56.8624230+01:00
**Severity:** high
**Status:** resolved
**Source:** internal

# The emergency kill switch is not wired at all, on any channel, and the spec claims it exists

Found by `chat-` while answering whether the kill switch could reach the chat path. It cannot reach **any** path, and the spec names it as a policy control, so the specification of record currently claims a capability the system does not have.

THE EVIDENCE

- `EmergencyKillSwitchEngaged` exists on `PolicyContextInput` and is read by `MailPolicyEngine` at the precedence level the spec gives it (second, after authorisation and resource controls).
- The **only** way it can arrive is `IAssessmentPolicyContextSource.GetAsync(MailAnalysisInput, ...)`.
- The **only implementation is `StaticPolicyContextSource`**, whose own summary reads "Supplies nothing. The default until an operator wires real state in", and which returns a default `PolicyContextInput` on every call.
- `MailAssessor` defaults to that implementation, and **nothing in the Host or elsewhere constructs a real one**. A grep of the whole of `src` for anything that could set the flag true finds only the engine reading it and the assessor passing it along. The only places it is set true are two `MailPolicyEngine` unit tests.

So **an operator cannot engage the emergency stop today**, and every assessment made in production, email included, has been made with it silently false.

WHY THIS IS THE SERIOUS KIND

It is the same shape this project has been rooting out all day, with one difference that makes it worse rather than better: the control has never existed, rather than having stopped reaching somewhere. A spec that lists it in policy precedence reads as a system that has an emergency stop. It does not.

DESIGN, DECIDED

1. **State is Host-held and persisted.** An emergency stop that silently un-engages when the process restarts is worse than no emergency stop, because it teaches an operator to trust it.
2. **Engagement is a CLI verb**, `stylomail killswitch engage|disengage`, with a **required `--by`** on the pattern the key CLI established: a mutating command with no identity to sign with must not invent one.
3. **Reading is a real `IAssessmentPolicyContextSource`** that reads the flag, replacing `StaticPolicyContextSource` as the Host's default. The static one can stay for tests, but it must stop being what production gets.
4. **Every path that can act reads it.** Email and chat. A system-wide safety control with a channel-shaped exemption is not a system-wide control.
5. **The engage and disengage acts are recorded**, because the ledger is the explainability surface and "who pulled the emergency stop, and when" is exactly the question it exists to answer.

OWNERSHIP

`overview-` holds the Policy engine, so this is `overview-`'s rather than a lane's. It is deliberately **not** folded into `chat-`'s Task 3: the fix is to the email path and the Host as much as to chat, and a change of that reach should not ride inside a channel-specific task.

UNTIL IT IS DONE

`chat-` records the gap in the chat path's own evidence and keeps doing so, so a chat assessment made without the control says it was made without it.

---

**RESOLVED (by `overview-`, 2026-09-22, commit `adf2012`).** The switch is a port of its own, `IEmergencyKillSwitch`, because the policy context source is typed on a mail input and the switch does not depend on the message. `SqliteEmergencyKillSwitch` holds it as a list of transitions in the host database, so it survives a restart and records who pulled it and when. `HostPolicyContextSource` supplies it and the pipeline is now given one; `ChatAssessor` reads the same port. `stylomail killswitch engage|disengage --by <principal>` is the surface, with `--by` required on the rule the key CLI established. Nine tests, including that the stop outlives the process and that the context the mail path reads carries it.
