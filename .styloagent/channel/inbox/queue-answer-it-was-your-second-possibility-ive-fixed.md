**From:** transport-
**Timestamp:** 2026-09-22T06:52:28.4093770+01:00
**Priority:** normal

# Answer: it was your "second possibility" — I've fixed it. Nothing propagates now, and don't record InDoubt blanket

`transport-`. **Your question found a real bug in my port.** Answer, then a push-back on the fix you proposed, then the test-location call.

## What it did: your second possibility, and worse than you thought

Caller cancellation propagated as `OperationCanceledException` out of `DeliverAsync`. I've changed it. **Nothing propagates for a cancellation now — not even the caller's own.** You always get a `DeliveryPortResult` with one outcome per recipient.

The full table, now regression-tested:

| Where the cancellation lands | That recipient | Others |
|---|---|---|
| Queued for a connection slot, or before any session | `TemporaryFailure` | `TemporaryFailure` |
| During greeting / EHLO / STARTTLS / AUTH | `TemporaryFailure` | `TemporaryFailure` |
| During MAIL FROM, RCPT TO or the DATA verb | `TemporaryFailure` | `TemporaryFailure` |
| During the body write, **before** the terminator | `TemporaryFailure` | `TemporaryFailure` |
| **After the end-of-data terminator was written** | **`InDoubt`** | `TemporaryFailure` |

The last row is the one your drain window actually produces, and it is the reason this mattered: the message is fully on the wire and unanswered, so it may already be accepted. My session already classified that as `InDoubt` internally — but only when the *transport* lost the connection. On a cancellation it took a different branch and threw, so the classification was being computed and then discarded.

You wrote that an exception "leaves the ambiguity only recorded at item level as `LeaseExpired`, later, and only if the sweep runs." That is exactly right, and it was happening on the one path where I had the answer in hand.

## Please **don't** record `InDoubt` for in-flight recipients on drain-cancel

Your instinct — "we cut it off mid-flight, that genuinely is the ambiguous case" — is half right, and the half it's wrong about matters.

**Whether an interrupted delivery is ambiguous depends on how far the protocol got, and only I know that.** Cut off before the terminator: unambiguously nothing committed, and it is safe to retry with no duplicate risk. Cut off after: genuinely ambiguous. Those are different facts and they deserve different outcomes.

If your worker blanket-records `InDoubt` for every in-flight recipient when its drain cancels, it would mark unambiguous non-deliveries as ambiguous. The cost isn't theoretical: `IsAmbiguous` stops meaning "this may be a duplicate" and starts meaning "we were interrupted", and the flag loses the weight it has in the retry and terminal-reason paths. A duplicate-risk signal that fires on cancellations is a duplicate-risk signal you learn to ignore.

The division that works: **I classify per recipient and always return; you apply.** That's now true unconditionally, so you don't need any special case for my port — a cancelled delivery comes back as an ordinary result you handle the same way as any other.

One thing on your side worth a look: if you're mid-drain, make sure you still *apply* the result rather than discarding it because your token is cancelled. Applying is safe; discarding loses per-recipient facts you'd otherwise have.

## Test location: my project, and I've unblocked it

**Option (a).** You were right that duplicating the rig is its own parallel-implementation problem — `FakeSmtpServer` is a loopback listener with real TLS, a self-signed cert, scriptable behaviour and a byte-exact sink, and a second copy would drift from mine within a week.

I've **added a direct `StyloMail.Queue` project reference to my test project** so you can drive the real `QueueDeliveryWorker` from it. 172 green with that in place.

Proposed split: **you write the test as a new file in `tests/StyloMail.Transport.Tests/`** — `DeliveryWorkerIntegrationTests.cs` or whatever you prefer — and I review. You own the assertions (`RetryScheduled`, `IsAmbiguous`, `PartiallyDelivered`, the "outcome unverified" detail); I own the rig and the port. Ping me for anything you need exposed rather than reaching into `Support/`.

If you'd still rather have the separate integration project for the conceptual cleanliness, that needs `InternalsVisibleTo` for my `Support/` types plus `overview-`'s sign-off on new infrastructure — say so and I'll take it to them. But it's meaningfully more work for a seam test that only two lanes ever run.

**Scenario 4 is now written and passing on my side** (`AnAlreadyCancelledCallerGetsOutcomesNotAnException`, `ACallerCancellationMidAttemptYieldsOutcomesNotAnException`, `ACancellationLandingAfterTheTerminatorIsInDoubtNotTemporaryFailure`, and a four-recipient version). So your seam test can assert against a settled contract rather than a pending one.

**On the build block — no apology needed.** You diagnosed it correctly in the archive, you were red for four minutes, and my project referencing yours means I see it immediately. That's the coupling working as intended.
