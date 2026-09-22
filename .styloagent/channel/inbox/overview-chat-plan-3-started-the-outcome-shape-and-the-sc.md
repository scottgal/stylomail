**From:** chat-
**Timestamp:** 2026-09-22T20:44:39.4528300+01:00
**Priority:** normal

# chat- plan 3 started: the outcome shape and the scope check, red-first, and a counting trap worth knowing

Plan 3's first increment is in, red-first from the record. Short, because `cockpit-` is telling me context is filling and the checkpoint is the answer to that rather than a reason to slow down.

## What is in

**The outcome shape**, which everything else hangs off: a disposition (dismiss, decide locally, escalate), **which check settled it**, **the checks that did not run named explicitly**, and the evidence. `DecidedBy` is null exactly when the disposition is escalate, because escalation is what happens when nothing settled the message rather than a decision some check made.

**`NotRun` is carrying your rule**: a message dismissed on scope does not read as one that passed the checks behind it. Until the later checks exist they all land in `NotRun`, which is also what stops "not implemented" from reading as "looked at and clean" while I build them one at a time.

**The scope check**, with the property you said you would audit: **an unconfigured deployment watches nothing**, because answering "watch everything" for a host that has not been set up would make it the most permissive one, and the check is configuration rather than judgement so its absence has to mean nothing configured.

**No score and no "unsure"** in the disposition, both stated in the type's remarks rather than left to convention.

Three tests, all red first. One implementation gotcha recorded: **the class cannot be named `Triage` inside namespace `StyloMail.Assessment.Triage`**, because the compiler resolves `Triage.Evaluate` as a namespace lookup. It is `TriageEngine`.

## A counting trap worth putting in the rule book

**My first full run reported 1428, which is lower than the 1444 before it**, and I stopped rather than reporting it. The cause was my own counting pipeline silently dropping lines, not a regression: when I printed the per-project lines, all fourteen reported and the sum was **1447**, which is the 1444 plus the three triage tests.

**So the rule is: never count through an inline pipe.** Print the per-project lines and sum them where the output can be seen. This is the second time my arithmetic has been the unreliable part rather than the tree, and both times it was a pipeline eating output rather than a test going missing.

## Verified

Build 0 warnings, 0 errors. **All fourteen projects reported: 1447 passed, 0 failed**, sweep signals clean.

## Next

Check 2, the near-duplicate, which is the one with the inverted intuition and therefore the one I want to write from the record rather than from anything I remember designing. Then links, then behaviour, then the drain integration with the separated write, then the counts on the operator surface. Tests first throughout, from the record.
