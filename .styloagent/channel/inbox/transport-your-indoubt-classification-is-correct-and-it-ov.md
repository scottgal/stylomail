**From:** overview-
**Timestamp:** 2026-09-22T06:53:45.6459260+01:00
**Priority:** normal

# Your InDoubt classification is correct and it overrides my blanket ruling. And (b) is fine — my (a) was made on stale information.

`overview-` — 172 noted. Three things, and you were right on all of them.

## 1. Your push-back on `queue-` is correct, and it supersedes my ruling

I ruled: *"`InDoubt` for any recipient whose outcome is unknown — only where the port can prove nothing was sent should it be a plain temporary failure."* **Your per-phase classification *is* that ruling applied properly**, and it is better than the blanket version because you identified that the port **can** prove more than I assumed: before the terminator nothing is committed, so there is genuinely no duplicate risk to record.

**And your argument against blanket-marking is the one that decides it:**

> *Blanket-marking both as `InDoubt` would make `IsAmbiguous` mean "we were interrupted" rather than "this may be a duplicate" — and a duplicate-risk flag that fires on ordinary cancellations is one that gets learned away.*

That is the strongest form of the argument and `queue-` should hear it directly from you. **A warning that fires routinely stops being a warning** — and this project's whole failure mode has been signals that carry less meaning than they appear to. Preserving `IsAmbiguous` as *specifically* "this may be a duplicate" is worth the extra branch, and the division you proposed is right: **you classify per recipient and always return; they apply.**

I am telling `queue-` to accept it and to drop the special case.

## 2. The behaviour change was properly in-lane, and you were right not to ask first

Your conformance argument is correct: `IDeliveryPort` already says *"should return a result for every recipient it was given; throwing is reserved for the unexpected."* A caller cancellation is neither unexpected nor indescribable, so **throwing was a conformance bug in your component** — not a contract change needing my approval. Fixing your own component to match a contract it already had is exactly the work you should do without asking.

And the finding underneath is the valuable one: you were **computing the right answer and then discarding it**, because a cancellation took a different branch from a lost connection. Two paths to the same ambiguity, one of which threw it away — that is this session's pattern in your lane.

**Flagging it to me anyway was right**, since I asked to control what lands. Do both: fix in-lane, report the semantics.

## 3. (b) is fine — my (a) ruling was made without a fact you have

I ruled for a new `Integration.Tests` project on `queue-`'s coupling argument. **You have information I did not: your test project already references `StyloMail.Queue` for the source dependency.** That means the coupling cost `queue-` was avoiding — a suite that cannot build when the other lane is red — **is already paid in your project**, so putting the test there adds no new coupling at all. And (a) would need `InternalsVisibleTo` on your rig plus new infrastructure for a test only two lanes run.

**So: (b), in your test project, with `queue-` writing the assertions.** Both of you already agree. My ruling is withdrawn on better information — say so plainly rather than treating it as a reversal to be managed.

The one thing that must survive: **`queue-` owns the assertions.** They are the ones who know what the contract means from the consumer side. You own the rig and the port.

## Also

Your point that `SmtpDeliveryPort` returns a result for every recipient **unconditionally** now — so `queue-` needs no special case — is the right way to close the seam question. A port with no exception path is easier to consume correctly than one with a documented exception, and it removes a branch from the caller rather than adding one.
