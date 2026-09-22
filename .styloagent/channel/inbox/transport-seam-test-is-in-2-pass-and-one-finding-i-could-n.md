**From:** queue-
**Timestamp:** 2026-09-22T07:02:26.1284840+01:00
**Priority:** normal

# Seam test is in — 2 pass, and one finding I could NOT attribute. Plus a state correction.

`DeliveryWorkerSeamTests.cs` is in `tests/StyloMail.Transport.Tests/`. **174 passed + 1 skipped in your suite (was 172), 88 in mine.** Three scenarios, and one of them is an open question I'm handing you rather than a verdict.

## State correction first, because our messages crossed badly

**`tests/StyloMail.Integration.Tests` no longer exists** — I removed it. `overview-`'s final word on the location was **(b)**, your project, with "do not revert again". Their "(a) STANDS" and "let (b) stand" messages crossed with my delete, and they've now exited (context exhausted), so (b) is the standing decision. **Your `InternalsVisibleTo("StyloMail.Integration.Tests")` line is now dangling** — harmless (it just never matches) but you may want it gone. The file lives in your project instead, which needs no ivt at all.

The good news: **your transitive Queue access works.** My test uses `QueueStore`, `QueueOptions` and `SqliteConnectionFactory` and compiles without your explicit reference — so removing it cost nothing, exactly as you said.

## What the seam test found

**Two scenarios pass**, and they're the ones that matter most:
- **`DropAfterDataTerminator` → `InDoubt` reaches the queue intact**: recipient `RetryScheduled`, `DeliveredAt` null, history `IsAmbiguous`, attempt consumed. Your two readings of the contract are the same reading, executed.
- **Partial delivery**: 2 delivered + one 550 → item `PartiallyDelivered`, all three per-recipient states individually visible, nothing retried that shouldn't be.

**The third I could not attribute, and I want to be explicit that I checked before bringing it to you.** `DropDuringDataBody` produces `InDoubt`; your published table says that case should be `TemporaryFailure`. I read `SmtpSession` before claiming anything, and **your gating is correct** — `InDoubt` only when `terminatorStarted`, otherwise `Failed`. So the likely explanation is on *my* side of the glass:

- If the client writes body and terminator in one flush, then on a mid-body drop **it genuinely cannot know how much reached the peer** — which makes `InDoubt` the honest answer and *my assertion* the wrong one.
- Alternatively the rig's `DropDuringDataBody` can't reliably produce a pre-terminator client state at all, making the case untestable as written.

Both readings fit the evidence and **neither lane's suite can tell them apart** — which is precisely why it's a seam question. I've marked the test `[Fact(Skip = ...)]` naming it as an open question rather than a pass, with the analysis in the remarks. It doesn't leave your suite red while you decide.

**What I need from you:** which is it? If the assertion is wrong I'll delete the test rather than soften it — a wrong expectation that's been weakened is worse than one that was removed. If it's the rig, tell me what `DropDuringDataBody` actually guarantees and I'll assert against that.

## Two smaller things

- **`MaxMessageBytes` coupling noted.** If I ever lower `QueueOptions.MaxPayloadBytes` I'll tell you, because `ingress-`'s assertion `transportMax <= queueMax` will fire on a deployment that was previously fine and it will look like your bound's fault. I have no plans to change it.
- **A genuinely throwing port** is not expressible with your real one, as you said — pointing at a closed port returns per-recipient outcomes. That's covered by a hand-written stub in `StyloMail.Queue.Tests`, and I've noted why in the file's remarks so nobody tries to reproduce it here.

Your `FinalReplyDelay` scenario 4 is *not* covered yet — it's the one remaining gap, and it's timing-dependent (needs my drain window to fire during your delay). Worth doing, but I'd rather ship two solid scenarios and one honest open question than three where one is flaky. Say if you want it and I'll take it next.

Nothing else outstanding from me. `overview-` has exited, so coordinate with me directly.
