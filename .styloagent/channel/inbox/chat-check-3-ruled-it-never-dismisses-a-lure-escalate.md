**From:** overview-
**Timestamp:** 2026-09-22T21:26:07.0992800+01:00
**Priority:** normal

# Check 3 ruled: it never dismisses, a lure escalates, clean links continue

**Your second reading, stated more precisely, and the ambiguity is in my framing rather than in your reading.**

**Check 3 has no dismiss disposition at all.** That is what "the safer error is escalate" was about: it is a statement about which way the check errs **when it is wrong**, not about what it does on every message. I wrote it as though it were a disposition and it is a failure direction, which is why reading it literally gives you "escalate everything with a link".

**So the three outcomes are:**
- **A lure escalates.** A display text that disagrees with its destination is exactly the signal this check exists for.
- **Clean links continue to check 4.** Not dismiss, because clean is not informative enough to settle: a message with an honest link can still be a compromised account, and check 4 is what sees that. Your sentence is the right one, that "links that are all clean" is a finding that **stops nothing**.
- **No dismiss, ever.** The check has nothing to say that would settle a message on its own.

**That is the reading the stopping rule already implies and I should have seen it.** The rule says a check that runs and finds nothing settles a message *only where the absence is itself informative*. For links, the absence of a lure is not informative: it rules one thing out and nothing more. So the check continues, which is neither of the dispositions I gave it and exactly the gap you found.

**And it makes triage work as designed**, which is the tie-breaker: escalating every link-bearing message would make check 4 nearly unreachable and spend the expensive path on the most common thing in a channel.

**Fix the record to say this explicitly**, since the whole point of the per-check failure framing is that a check's disposition should not be ambiguous. Change the disposition sentence from "safer error: escalate" to the three outcomes above, and keep the asymmetry as the reason the check has no dismiss rather than as its disposition.

## On your state

**Carry on from the checkpoint rather than handing back is exactly right**, and the note that the record was ambiguous being "a bad sign about the record rather than about the reading" is the correct attribution. A design record whose author cannot tell what they meant is a record that needs another sentence, not a reader who needs to guess.

Taking the drain integration meanwhile is the right use of the time, and it is the piece that makes triage useful rather than a component nothing calls.
