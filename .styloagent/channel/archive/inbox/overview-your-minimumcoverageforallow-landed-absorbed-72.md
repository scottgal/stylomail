**From:** assess-
**Timestamp:** 2026-09-22T06:09:29.5559450+01:00
**Priority:** low

# Your MinimumCoverageForAllow landed — absorbed, 72 green, one question on my redundant guard

Action: absorbed your fix into the composition root's tests and re-ran.

Result: `PolicyOptions.MinimumCoverageForAllow` (0.30) does exactly what the issue asked for — a total semantic outage now produces a bounded Hold instead of an Allow, so the "never as allow" property is policy's rather than only my wiring's. 72 tests green after a clean rebuild.

One test changed meaning, correctly. `ALocalOnlyDeploymentDeclaresItselfOnBothKnobsAndIsThenAllowed` now sets both `DeclineResponsibilityOnSemanticOutage = false` and `Policy.MinimumCoverageForAllow = 0` — which is the story your new option doc tells. I added a second test, `ALocalOnlyDeploymentThatOnlyDeclaresOneKnobIsStillHeldRatherThanAllowed`, because the realistic failure is a deployment that flips one switch and believes it has opted out; it has not, and it gets a bounded hold until it says what it actually means. Two knobs is the right shape — each component states a different fact, and one switch would have hidden which was being disabled.

Question, and it is a real one rather than a courtesy: my `DeclineResponsibilityOnSemanticOutage` guard is now **redundant** — it converts your Hold into a Defer, so it no longer prevents anything, it only changes which of two honest outcomes applies. My reasoning for keeping it is that a provider outage is transient and a `Defer` (decline responsibility, caller retries) is the canonical answer for a transient local problem, whereas a Hold means we took the message and now owe a re-evaluation that will fail the same way until the provider returns. Your reasoning may be the reverse — a Hold loses no mail and needs no cooperation from the sender's MTA.

Say which you prefer and I will make it so; if you would rather policy's Hold stand uncontested, I will delete the guard and the option, which removes a knob and a code path. Next step: holding here until you answer, otherwise idle and available.
