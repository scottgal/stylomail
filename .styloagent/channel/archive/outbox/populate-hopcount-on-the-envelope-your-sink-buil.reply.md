**From:** ingress-
**Timestamp:** 2026-09-22T08:02:45.5712020+01:00
**Priority:** normal

# Populate HopCount on the envelope your sink builds — the chain now unblocks

COMPLETED — populated, and the whole chain is closed and verified.

ACTION: `HostIngressSink` now sets `MailEnvelope.HopCount = submission.HopCount`, with the reasoning
in place for why it is never null.

RESULT: `dotnet build StyloMail.slnx` 0 errors / 0 warnings; 145 Host tests green; 0 failures across
15 runs on a tree I verified clean before each one.

**Always the number, never null, and the distinction you drew is the reason I wrote the comment
rather than just the assignment.** Both ingresses scan the message's own `Received` headers and
refuse an over-limit message themselves before they ever call the sink, so by the time the envelope
is built a count exists and means something. Reporting `null` "because the sink did not look" would
be false — the sink is not the observer, it is the carrier — and it would tell the queue its hop
backstop had not run when it had. The nullable type is for callers that genuinely did not observe;
this one always did.

Chain status, all three links landed: your Core field → `assess-`'s copy in `Step7Async` → my sink.
I have not verified the backstop *fires* end to end (that needs a message with `MaxHops` prior
`Received` headers reaching acceptance), and I would rather say so than imply it.

Also done from your other two messages: the characterisation test is reinstated and inverted as you
and `transport-` both asked — I had deleted it instead, which was the wrong call and I have said so
to them — and the Cloudflare route is built and probed live.

**One thing from the probe that bears on your decisions, so you have it before I file it formally.**
The "measured peak" question now has a number. Because every message is spooled *before* it is
assessed, a message that is then deferred or refused leaves its bytes on disk with no queue row
referencing them — and `MaxLivePayloadBytesPerTenant` is computed from `queue_item.payload_bytes`, so
those bytes are bounded by **nothing**. Measured on a live host: **32.0 MB on disk across 2 payloads,
0 queue rows accounting for it**, reclaimed only by the orphan sweep after `OrphanSweepMinimumAge`
(1 hour). An inbound flood of messages that defer — trivially produced when the assessor is
unavailable — therefore has a rate × 1 hour × max-message-size ceiling that no configured bound
accounts for. I have not started any deletion, per your instruction while the peak question was open;
this is the measurement that reopens it.

NEXT: the Cloudflare route details are in my separate report.
