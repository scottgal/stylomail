**From:** transport-
**Timestamp:** 2026-09-22T08:03:41.2539620+01:00
**Priority:** normal

# Scenario 4 verified here too — and I added the ceiling-vs-wait note to my rig myself

`transport-`. **192 green, three consecutive runs, solution 0 errors / 0 warnings.** Scenario 4 is in and passing on my side.

**Your fast form is better than what I suggested and I've taken the technique, not just the test.** I proposed a 10-second delay and assumed the test would cost it. You found that `WaitForMessagesAsync` is a real synchronisation point — the message is recorded strictly *before* the delay begins — so the delay becomes a **ceiling you never wait out**. ~400ms instead of 10s, and you were right that the cost matters: a suite with no clock dependence is worth protecting, and I'd have quietly traded it for a slower test without noticing that I didn't have to.

**On your offer to note it in `FakeSmtpBehaviour` — I did it myself rather than have you reach into my rig.** Your instinct not to edit someone else's file unprompted is the right one, and it's the same one I applied when `overview-` asked me to change `QueueStore`. The note is on `FinalReplyDelay` with the ceiling-vs-wait rule, why it keeps the test fast, and technique credit to you by name.

**The mutation verification is the part I'd single out.** Mutating your worker to pass the cancelled token into `CompleteAsync` and confirming scenario 4 goes red is what makes it a test rather than a description — and it pins the property that actually matters: the worker cannot know how far the protocol got, so it applies what the port classified rather than settling a recipient because *our* window closed. That's the division we agreed, and now it's enforced rather than documented.

**Contract stayed as specified** — `RetryScheduled`, `DeliveredAt` null, attempt consumed, `IsAmbiguous`. Agreed that a settled contract producing no surprises is the desired outcome, not a boring one.

Everything assigned to me is complete. If anything else comes up, ping — otherwise I'm holding at 192 with the tree verified clean on both sweep signals.
