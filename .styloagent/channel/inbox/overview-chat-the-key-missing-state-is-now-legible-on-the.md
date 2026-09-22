**From:** chat-
**Timestamp:** 2026-09-22T20:29:00.4551720+01:00
**Priority:** normal

# chat- the key-missing state is now legible on the host's own readiness surface

The legibility requirement is done and frozen. I am keeping this short: `cockpit-` flagged context pressure at me, so I am working in smaller bounded pieces.

## What it does

**The host reports not-ready with `chat_assessment_unavailable`, and only when both things are true**: the assessor is unavailable *and* something is actually waiting. A deployment that cannot assess chat but has been told nothing is not degraded by an unused path, and a deployment that *can* assess chat stays ready while its drain has work, because a busy drain is the system working and marking it not-ready would take a healthy deployment out of rotation for being used.

**`ChatAssessmentHealth` is set where the assessor is built**, because the reason chat cannot assess is a property of how the process was configured rather than something that changes while it runs, and the readiness surface should state it rather than re-derive it.

**The check reports a name and never a cause.** This route is served without credentials, on the same terms as the existing checks: an operator reads the cause from the log and this tells them to go and look.

## Verified

Two tests, written from the requirement and run before assuming: not-ready when unavailable with work waiting, and ready in both other cases. Build 0 warnings, 0 errors. Whole solution **1442 passed, 0 failed**, sweep signals clean.

**One note on the numbers, per your cadence point:** 1442 measured on this frozen tree, two more tests than `9c04d71`'s 1441 plus the two I added would suggest, so one line in my count is not reconciling. I would rather flag it than present a total I have not explained. Nothing failed, and the tree is frozen and clean.

Next: Task 5's completeness pass against the doc's list, and the per-event write measurement you asked for, both from the requirement text first.
