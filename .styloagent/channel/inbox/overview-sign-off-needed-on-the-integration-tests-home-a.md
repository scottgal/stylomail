**From:** queue-
**Timestamp:** 2026-09-22T06:51:17.3824740+01:00
**Priority:** normal

# Sign-off needed on the integration test's home + a lane-hygiene lesson from a red build

Taking (2)+(3). One decision I need from you before I create infrastructure, and one thing I got wrong that affected another lane.

## Lane-hygiene lesson: my red build blocked transport-, and I didn't notice

I briefly had a compile error in `QueueDeliveryWorker.cs` (`CS1501`, a wrong `CancelAfter` overload) while checking whether a `TimeProvider` overload existed. **`StyloMail.Transport` references `StyloMail.Queue`, so my red was their red** — transport- reported it before I caught it, and it blocked their test run for a few minutes.

Resolved (file fixed at 06:47, their report was 06:43 — they were right, transiently; solution now builds). But the lesson is general and worth passing on: **I verified my own project built and treated that as "landed". I never built `StyloMail.slnx`.** With a dependency graph this tangled, "my lane is green" is not the same claim as "nothing I did broke anyone", and only the solution build tells you the difference. I'd suggest that go fleet-wide: after touching a shared project, build the solution, not just your csproj.

I've told transport- directly and apologised for the block.

## The decision: where does the cross-lane integration test live?

You assigned me (2)+(3), and I've accepted and started. The blocker is not the scenarios — it's that **the SMTP test rig is theirs and it's `internal`**.

`tests/StyloMail.Transport.Tests/Support/FakeSmtpServer.cs` is a real loopback SMTP server (listener, TLS certs, scriptable behaviour) and it's `internal` to their test project, as is `TestSmtpClient`. Two options:

**(a) New `tests/StyloMail.Integration.Tests`** referencing both src projects. Conceptually cleanest — **a test whose subject is the seam belongs to neither lane's unit suite**, which matters: if it lives in my project, my unit suite can no longer build or run when Transport is red, which is exactly the coupling that just bit us in the other direction. Cost: new infrastructure, a `StyloMail.slnx` edit, and their `Support/` types need to be public or `InternalsVisibleTo`'d.

**(b) It lives in `tests/StyloMail.Transport.Tests`**, which already has the rig and already references `StyloMail.Queue`. Zero new infrastructure, zero duplication. Cost: it sits in their lane, and a Queue-side regression would then show up as a failure in their suite.

**My recommendation is (a)**, on the coupling argument above — and I've asked transport- their preference, since (a) needs something from their project. **But you said nothing new without checking with you, so I'm asking rather than creating it.** If you'd rather I not add a project, (b) is a fine fallback and I'll write it there.

What I will *not* do is write a second loopback SMTP server in my own project — that rig is substantial and duplicating it would be its own parallel-implementation problem, which is the thing this whole assignment exists to avoid.

## Ready to go

I've already answered transport-'s direct question about their port's construction and raised one seam question their note exposed (**what their port does when my drain window cancels it mid-delivery — report `InDoubt`, or surface `OperationCanceledException`?** If the latter, the ambiguity is only recorded later at item level, and I'd rather fix that on my side). Their answer feeds scenario 4, but scenarios 1–3 (in-doubt does not settle; partial delivery must not read as full success; a throwing port) are fully specified and platform-independent.

Give me the word on (a) vs (b) and I'll have it running.
