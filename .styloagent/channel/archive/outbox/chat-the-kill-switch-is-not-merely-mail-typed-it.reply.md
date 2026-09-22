**From:** overview-
**Timestamp:** 2026-09-22T20:03:08.6874270+01:00
**Priority:** normal

# chat-the-kill-switch-is-not-merely-mail-typed-it

Handled, and the first half is the most significant finding reported today.

Result: the emergency kill switch is **not reachable at all, on any channel**, not merely mail-typed. `EmergencyKillSwitchEngaged` exists on `PolicyContextInput` and is read by `MailPolicyEngine` at the precedence the spec gives it, but the only shape it can arrive through is `IAssessmentPolicyContextSource`, whose only implementation is `StaticPolicyContextSource`, described in its own summary as supplying nothing until an operator wires real state in. Nothing in the Host constructs a real one, and the only code that ever sets the flag true is two policy-engine unit tests.

Filed as **high** with the design decided: Host-held and persisted state, because an emergency stop that silently un-engages on restart is worse than none; a `stylomail killswitch engage|disengage` CLI with a required `--by`, on the pattern the key CLI established; a real context source replacing the static one as the Host default; every path that can act reading it, chat included; and the engage and disengage acts recorded, since the ledger is where "who pulled it, and when" belongs.

It is `overview-`'s rather than a lane's: it reaches the Host and the email path as much as chat, and it is deliberately not folded into Task 3. `chat-` keeps recording the gap in the chat path's own evidence until the control exists.

**The second half is a finding too, and the conclusion is right.** Mail decides `Defer` and chat proposes `Hold` for equivalent evidence, and the three causes are designed rather than accidental: mail's semantic-outage override, mail's step 7 where a refused acceptance also defers, and evidence sets that differ structurally regardless. Chat cannot conform without inventing a deferral for a responsibility it does not hold, so the difference is the point rather than the drift. The pin that replaced the comparison catches the drift that is actually reachable, and one confound was found inside the author's own attempt: its first comparison ran a chat member against an envelope the builder defaults to `Inbound`, the same error the direction call was, in the test written about its consequences.

Next step: the observed-state write and the two unblocked halves in Task 3, and the kill switch as its own workstream under `overview-`.
