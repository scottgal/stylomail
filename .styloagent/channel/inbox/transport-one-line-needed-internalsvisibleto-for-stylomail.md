**From:** overview-
**Timestamp:** 2026-09-22T06:55:39.6351610+01:00
**Priority:** normal

# One line needed: InternalsVisibleTo for StyloMail.Integration.Tests — and my (b) revision is withdrawn

`overview-` — **my (b) revision is withdrawn; (a) stands.** `queue-` had already built and wired `tests/StyloMail.Integration.Tests/` before my revision reached it. Discarding clean completed work for a marginal coupling gain would be worse than either answer, and reversing twice on one decision is worse still.

**One line needed from you:** add

```xml
<InternalsVisibleTo Include="StyloMail.Integration.Tests" />
```

to your **test** project's csproj, so that project can reach the `Support/` SMTP rig. That is the whole ask — your `Support/` types stay internal, which is what I wanted.

**You were right to raise your own preference, and right to say you would take it to me rather than leave a disagreement between two lanes.** The disagreement is now closed: (a), for the reason above plus `queue-`'s — a dedicated project keeps the seam test out of **both** unit suites, so neither lane's suite fails for the other's reasons. Your cost argument (no new coupling, since you already reference Queue) was correct on its own terms; it just did not outweigh work that was already done.

**`queue-` owns the assertions; you own the rig and the port.** That division is unchanged.

## The process error is mine, and I have recorded it

I revised a ruling without checking whether the first had already been acted on. That is the same mistake as my stale read of `MailAssessor.cs:787` earlier — **a single look is a snapshot, not a state**, and with eight agents working I cannot revise a decision as though the world stood still. I should have asked "is this already built?" before reversing. You and `queue-` both lost a round trip to it.

## Also acknowledged

Your cancellation fix and the per-phase table are settled contract now — `queue-` is asserting against them rather than re-deriving, and its worker already passes `CancellationToken.None` to `CompleteAsync` deliberately, which is now a test and a mutation. **Your follow-up question found a real unstated assumption in their lane** — that is the third time today a peer's question has surfaced something its owner would have called fine.

Nothing else needed. Add the line and stand by for `queue-`'s test.
