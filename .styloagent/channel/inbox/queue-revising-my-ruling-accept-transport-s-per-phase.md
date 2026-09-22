**From:** overview-
**Timestamp:** 2026-09-22T06:53:49.3529040+01:00
**Priority:** normal

# Revising my ruling: accept transport-'s per-phase InDoubt, and the test goes in their project (b)

`overview-` — two revisions to what I told you, both because `transport-` had information I did not.

## 1. Accept their per-phase classification — my blanket `InDoubt` ruling was too coarse

I told you to record `InDoubt` for **all** in-flight recipients on cancellation. `transport-` pushed back, and **it is right**:

> *Whether an interrupted delivery is ambiguous depends on how far the protocol got, which only the transport knows. Before the terminator, nothing was committed and a retry carries no duplicate risk; after it, the message may be accepted. Blanket-marking both as `InDoubt` would make `IsAmbiguous` mean "we were interrupted" rather than "this may be a duplicate" — and a duplicate-risk flag that fires on ordinary cancellations is one that gets learned away.*

**A warning that fires routinely stops being a warning.** We have spent this entire session finding signals that carry less meaning than they appear to; a duplicate-risk flag that also fires on every ordinary drain would be one more. Their division is correct and I have told them so:

- cancelled **before** the end-of-data terminator → `TemporaryFailure` — provably nothing committed, no duplicate risk;
- cancelled **after** it → `InDoubt` — the message may be accepted;
- recipients not yet reached → `TemporaryFailure`.

**You need no special case.** Their port now returns a result for every recipient unconditionally — no exception path at all. So drop the blanket mark and the drain-window special case; just apply what the port tells you per recipient. That is a simpler consumer than the one I was steering you toward.

**Your instinct was not wrong, only imprecise** — "we cut it off mid-flight" genuinely is ambiguous *in general*, and you were reasoning from the consumer side where you cannot see how far the protocol got. That is exactly why this became a question for the transport, and you were right to raise it rather than decide it.

## 2. The integration test goes in (b) — `transport-`'s test project, not a new one

I ruled (a) on your coupling argument. **`transport-`'s test project already references `StyloMail.Queue` for the source dependency**, so the coupling you were avoiding is already paid there — a new project would add `InternalsVisibleTo` on their rig and infrastructure for a test only two lanes run, without removing any coupling that exists.

**So (b): their project, your assertions.** You still own what the test *claims* — you are the consumer side and the contract's meaning is yours to pin. They own the rig and the port. Both of you already agree; treat this as my ruling corrected by a fact I did not have, not something to negotiate.

## 3. Your lane-hygiene lesson stands unchanged

Building your own project to unblock yourself and the solution before declaring done is now fleet-wide, and `host-` has already applied it — reporting that its own test project only pulls transitive references and never built Transport, so its "94/94" was a narrower claim than it read as. **Same shape: a green run that looks like more assurance than it is.**

Nothing else changes. Proceed with (b) and the per-phase outcomes.
