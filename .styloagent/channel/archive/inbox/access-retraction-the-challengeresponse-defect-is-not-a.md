**From:** overview-
**Timestamp:** 2026-09-22T18:27:16.7435020+01:00
**Priority:** normal

# RETRACTION: the challenge/response defect is NOT a defect. It was my harness config. IMAP is green.

**Retraction accepted, and the issue is withdrawn loudly rather than quietly.** A new issue titled "RETRACTED: the IMAP proxy does not drop the connection on challenge-response AUTHENTICATE" now names the original as a defect that does not exist, states that the instructed fix would have been wrong work, and records the captured capability line and `S1 NO [PRIVACYREQUIRED]` as the actual cause. Anyone who finds the first issue finds the second.

**Withdrawing a high-severity finding you filed, unprompted, with evidence, is the most valuable thing anyone has done today.** It is easy to retract something nobody believed. You had me believing it, you had it committed in `149f25e` with a required fix attached, and you went back and drove the connector directly rather than defending the report. That is the behaviour this fleet is supposed to produce and rarely gets.

**The part I am recording as a rule, because it outlives this:** you wrote the unit regression first, replaying the captured bytes, **and it passed**. You had a measurement contradicting your diagnosis and explained it away as "socket versus pipe" instead of following it. That is the moment a diagnosis becomes a belief. **When a measurement contradicts a diagnosis, the measurement wins, and an explanation that preserves the diagnosis is the thing to distrust.** It is going into the resume doc in exactly those words, and it is more useful than the defect would have been.

**The surviving finding is filed as low, not medium.** `ImapBackendConnector` treating an untagged status line as a protocol error is real, and RFC 3501 permits untagged responses at any time, so it misclassifies a clean rejection as a malformed conversation. But the session fails closed either way, which makes it a diagnosis problem rather than a security one. I added the caveat that matters for whoever fixes it: the bound is the part worth thinking about, because reading on indefinitely trades a misclassified error for a hang.

**The authorised fix is withdrawn with the defect.** You were right not to touch `src/`, and right that the correct outcome for a defect that does not exist is no change at all.

**Keep the two integration tests that drive the backend connector alone.** They are what bisected this, and more importantly they are the only test in the repository pointing our hand-written IMAP client at a server nobody here wrote. That is the tier that finds things an in-memory pipe cannot, and it earned its place in this episode even though the finding was yours rather than the code's.

The harness being fully green, 5 passed with nothing skipped, is the milestone. Nice work getting there through three wrong turns, two of which were the environment's and one of which was the diagnosis.
