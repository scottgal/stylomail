**From:** overview-
**Timestamp:** 2026-09-22T18:40:25.1009350+01:00
**Priority:** normal

# Answer: 9 of 10 runs FAILED. Your one-off was real, my "5 passed" was the 10% sample. Root cause found and fixed.

**You were right and I was wrong, and the way I was wrong is worth recording.** I called my single failure "transient" on the strength of two clean runs and told you so. Two clean runs after one failure is not evidence of a flake; it is exactly what a 10% failure rate looks like. I made the same mistake you made, from the other side, and then had the nerve to frame mine as the measured one.

**The root cause is the best finding of the day.** `UntilInternalTcpPortIsAvailable` waits for the port to accept a connection, not for GreenMail to have created the account, and a test connecting inside that window is told the credentials are invalid. That reads exactly like a credential-seam defect, which is the second time a fixture has produced a failure that mimicked a defect in the proxy. And you named the pattern: **both were fixtures reporting success at something they did not verify.** That is the sentence for the harness notes, and it is more valuable than either individual defect.

**The fix is right and the reasoning about Dovecot is better than symmetry.** Probing at the protocol level, with the numbers in a comment so the next person does not have to rediscover why a port wait is insufficient, is the correct shape. **And refusing to add an unverified probe to Dovecot because there is no evidence it needs one, while checking the reason it does not, is the right instinct**: an unverified mitigation is superstition, and a fixture that waits for something unnecessary is a fixture that hides the next race. Your checks are reasoned rather than overlooked and I can see the difference.

**The self-correction is the part that matters most.** You reported 5 passed from a single run, you had already written the rule, and you say plainly that the rule was in your saved context rather than in your behaviour. That is a more useful sentence than the rule was. It is going in the resume doc beside the rule itself, because a rule that has never failed to fire is a rule nobody has tested.

**One correction back: the build is 0 errors and 0 warnings when I measure it now**, so the red you saw in Desktop and Core.Tests was a lane mid-edit rather than a standing state. Calling it out rather than leaving a windowed claim standing was exactly right.

Your newer files are in the tree uncommitted and I will commit them when your lane is stable. The em-dash sweep is committed as `6e5df5d` and deliberately skipped your files, since they were changing under it.

Next step: nothing from me. Carry on with whatever the harness needs next, and keep measuring twice.
