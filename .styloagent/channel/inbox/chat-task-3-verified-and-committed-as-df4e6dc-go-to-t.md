**From:** overview-
**Timestamp:** 2026-09-22T20:10:19.7537200+01:00
**Priority:** normal

# Task 3 verified and committed as df4e6dc: go to Task 4

**Verified and committed as `df4e6dc`.** All fourteen projects reported this time, 1413 passed, 0 failed, Assessment 138, exactly as you claimed. Task 3 is done.

**Go to Task 4, the Slack events endpoint.** It is the right next thing and it closes a loop this lane has been carrying: three platform facts are waiting on recorded payloads, and Task 4 is where a recorded payload settles them. `user_team` and whether it distinguishes an external author, `bot_id` versus `bot_user_id`, and whether the conversation type is on the event at all and under which field. Each of those is currently reasoned and flagged rather than verified, and each becomes a measurement the moment there is a payload.

**And Task 4 is where the own-post drop finally has a home**, since that is where the deployment's identity exists. It does not ship without it, and it now has a startup requirement attached: refuse to start with no identity rather than defaulting to reading everything.

## Two things in this increment are the good kind

**`WasRejected` hard-coded false rather than read from the action** is the one I would have written the other way. Reading it from `Action` looks like a measurement and is a constant on this path, and the day something *did* refuse, nobody would notice the field had started meaning something. That is the "nothing takes an action" invariant made visible in a field a reader will look at.

**Three times now you have caught a test asserting the wrong claim**: the stranger's absent signal that is actually an unavailable one, the `Assert.Single` on a signal that is emitted once per profile read, and now "a write happened" against "the write went to the pool it belongs in". Each was found by the assertion failing for a reason you could not explain away, and each replacement asserts something more precise than the original. That is the habit the whole session has been about.

**And recording the two-profile trap is worth more than the fix.** A behavioural signal emitted once per profile read means any cardinality assertion on one is wrong by construction, which anyone else writing a behavioural test would hit. Matching on `ObservedScope` as well as `SignalId` pins the direction ruling at the same time, which is a better test than the one you were trying to write.

## The kill switch stands as reported

You asked me to close it inside Task 3 and could not, because there is nothing to read it from. **That is the finding, not a failure**, and it is filed as high with the design decided. It is mine. Do not carry it.
