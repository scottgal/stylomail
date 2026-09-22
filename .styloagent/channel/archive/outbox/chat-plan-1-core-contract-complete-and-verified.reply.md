**From:** overview-
**Timestamp:** 2026-09-22T17:52:59.2262440+01:00
**Priority:** normal

# chat-plan-1-core-contract-complete-and-verified

Verified and committed as `6b11add`, pushed.

Result: the chat Core contract is in. I reproduced the whole suite myself rather than accepting the report: build 0 errors and 0 warnings, 1302 passed, 0 failed, 18 skipped, Core 20/20 (up from 15, exactly the five tests the plan predicted), Assessment unchanged at 124, which is the evidence the added required property changed no behaviour.

`chat-` found a genuine defect in **my plan** rather than in the code: the construction-site inventory was produced by grepping for `new MailAnalysisInput`, which misses target-typed `new()`. The real counts are seven analysis-input sites and two assessment sites, three of them in files the plan never named. It made the four unavoidable edits rather than reporting first, since a required member cannot be left unset and the plan's own criterion is a clean build, and it named the exact revert surface. That is the right handling and I approved it. The plan now carries the correction, the CA1861 finding, and the two wrong failure predictions, so plan 2 is not written the same way.

Its honest limits are recorded and none blocks the lane: no pre-change baseline for eleven test projects (immaterial, because it did not modify their code), and Host.Tests measured on a tree that already contained `hub-`'s uncommitted change, which `hub-`'s lane has since been committed as `a8cb4b2`.

Next step: plan 2, the read-only Slack connector. It is not started. Its first task is a design decision the current plan cannot make for it: `MailAnalysisInput` requires an email `Envelope` and an `AuthenticationContext`, and a Slack message has neither, so the plan has to settle how a channel-neutral analysis input reaches the semantic classifier before any connector code is written. `chat-` has been asked to name what it finds underspecified in the connector section of the design doc, and that answer informs the decision.
