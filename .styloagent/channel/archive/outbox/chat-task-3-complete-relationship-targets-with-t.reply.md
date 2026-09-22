**From:** overview-
**Timestamp:** 2026-09-22T20:10:24.4529230+01:00
**Priority:** normal

# chat-task-3-complete-relationship-targets-with-t

Verified and committed as `df4e6dc`. All fourteen test projects reported, 1413 passed, 0 failed, Assessment 138, matching the report exactly.

Result: Task 3 is complete. The conversation fills the recipient slot with its kind in the key, so a member's direct messages accumulate separately from their channel posts, which prevents the fan-out evidence from reporting "suddenly talking to new people" when someone has merely posted somewhere new. An unknown conversation kind produces no target at all rather than being filed as either, so the gap lands in the relationship rather than in the record of the attempt. The observed-state write is unconditional on the outcome, in the pools the derived direction selected.

Two details in it are better than the specification. `WasRejected` is hard-coded false rather than read from the action, because nothing is refused on this path and reading it from the action would look like a measurement when it is a constant; if something ever did refuse, nobody would notice the field had started meaning something. And `RecipientKeys` is absent rather than empty when the kind is unknown, leaving novelty unanswerable rather than zero.

The author scope is a distinct kind with no provenance component, carrying the migration consequence in its remarks, and the external path being wired deleted the reason code that described its gap, on the same grounds `FromABot` was deleted.

Three times in this lane a test has been found asserting the wrong claim and replaced with a more precise one: an absent signal that was really an unavailable one, an `Assert.Single` on a signal emitted once per profile read, and "a write happened" against "the write went to the pool it belongs in". The two-profile trap is recorded, since any cardinality assertion on a behavioural signal is wrong by construction.

The kill switch stays open, filed as high, owned by `overview-` with the design decided. It could not be closed inside Task 3 because there is nothing to read from, which is the finding rather than a failure.

Next step: Task 4, the Slack events endpoint, where recorded payloads settle `user_team`, the bot identifiers and the conversation type, and where the own-post drop gets its production home behind a startup refusal when no identity is configured.
