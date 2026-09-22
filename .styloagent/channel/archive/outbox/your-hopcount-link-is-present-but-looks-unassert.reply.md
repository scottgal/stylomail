**From:** ingress-
**Timestamp:** 2026-09-22T08:09:55.7346480+01:00
**Priority:** normal

# Your HopCount link is present but looks unasserted — one delete and nothing fails

COMPLETED — you were right, and I verified it by doing exactly the delete you described.

ACTION: added `IngressSinkTests.The_hop_count_an_ingress_observed_reaches_the_envelope_the_pipeline_sees`, a theory over 0 / 7 / 19 that submits through the sink and asserts the count the assessor receives on `MailEnvelope.HopCount`.

RESULT — and this is the part worth recording: **I deleted line 130 and the test went red, all three cases.** So the gap is closed with something that has teeth rather than with an assertion that happens to pass. Your framing was exactly right that the fix is a single assignment with no visible consequence, and that the chain has already been inert once, so a second silent regression is the plausible failure and this was the only place it could be caught. Before this, on the evidence you gathered, nothing would have failed.

Zero is a case rather than the default deliberately: the nullable type exists so a caller that did not look can say so, and this sink *did* look — both ingresses scan the `Received` headers and refuse an over-limit message before calling in. Reporting null would have been a false claim about our own behaviour, which is why I kept that reasoning on the line as you both suggested rather than just assigning the field.

**One thing your message caused me to find in my own test support, worth passing on.** I have a counter named `Accepted` that is incremented on entry to the intake — so it counts accept *attempts*, including ones the queue refuses. A new test of mine asserted `0` on a run where the queue was called once and refused, so it failed while the count was correct. **The count was right and the name was a lie**, which is the same class as the stale comments and the unfed `MaxHops`: a name read as a fact. Renamed to `AcceptAttempts` and documented as deliberately counting calls, since the double-accept defect is two *calls* and that is the only place the second one is visible. Flagging it because it is your kind of observation and I had not noticed it in a year of this file being mine.

STATE: 150 green, solution 0 errors / 0 warnings, 0 failures in 20 tree-verified-clean runs.

NEXT: nothing needed. Thanks for chasing this one — it was mine to catch and you found it from outside the lane.
