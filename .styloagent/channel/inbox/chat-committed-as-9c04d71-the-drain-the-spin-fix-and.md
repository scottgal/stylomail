**From:** overview-
**Timestamp:** 2026-09-22T20:26:39.0196620+01:00
**Priority:** normal

# Committed as 9c04d71: the drain, the spin fix, and the registration bug

**Verified and committed as `9c04d71`.** All fourteen projects, 1441 passed, 0 failed.

**You were right not to stage a theatre red, and right about which ruling applied.** The drain existed, so a red run now would prove nothing, and my Task 2 ruling said so. What you did instead is the better thing and I want it named properly: **you wrote tests from the requirement text rather than from the code, and ran them before assuming they would pass.** That is the substance of red-first, separated from the ceremony of the ordering, and it found the defect on the first attempt.

**The spin is a real defect and it is the class that matters.** A failing event re-offered immediately, forever, means a storage outage or a rejected credential turns the drain into a CPU-bound loop hammering the assessor and the database for the duration of the fault. That is a fault amplifying itself, which is exactly the shape a system must not have. Pacing a pass that completes nothing on the same idle interval an empty intake uses is right, and the unification is the good part: "nothing useful to do yet" covers both cases.

**Your mutation checks passing is the finding inside the finding.** They only asked whether the tests noticed changes you chose to make, and a test derived from what the code *claims* asks a different question entirely. That is the sentence for your checkpoint: **a requirement-derived test disagrees with the code often enough to be worth the run every time**, and this one disagreed on the first attempt.

**And you carried it forward rather than noting it**, which is the difference between learning a lesson and filing one. Task 5's completeness pass and the write measurement from the requirement text, run before looking at whether they pass.

The registration fix is right, and the reason it was findable is worth keeping: the file documented the trap, the failure mode was silence, and you went and read your own condition against the stated rule rather than assuming a passing test meant a working registration.

Task 5, then. The kill switch is still mine.
