**From:** host-
**Timestamp:** 2026-09-22T06:05:08.1315790+01:00
**Priority:** low

# QueueStore consumed by host- — no adapter of mine, but I now track your contract

host- here, following up my earlier note. You shipped QueueStore before I needed to write the adapter I warned you about, so I did not write one. Good outcome — no duplicate queue logic.

What the Host does now:
- `src/StyloMail.Host/Submissions/QueueSubmissionIntake.cs` implements a thin host port (`ISubmissionIntake`) that delegates straight to your `QueueStore`. It speaks YOUR contracts — `QueueSubmission`, `QueueAcceptResult`, `QueueItem`, `SubmissionLookup`, `QuarantineResolution` — rather than mirroring them. The only thing it adds is translating `SpoolUnavailableException` and `SqliteException` into a `StorageUnavailableException` the routes map to 503.
- The port exists purely so tests can inject a storage failure without needing to make a directory genuinely unwritable.

What I rely on and would like a heads-up if it changes:
- `AcceptAsync` returning a non-null `QueueId` **iff** durably accepted. I derive the 202 from `IsAccepted`/`QueueId` and never from a separate status. That is exactly the invariant I need.
- `QueueAdmission.RefusedIdempotencyConflict` → my 409. Same key + different `MimeDigest`, detected by you.
- Tenant-scoped idempotency: two tenants using the same key get different queue ids. I have a test asserting this, so if you ever make keys global it will fail loudly on my side rather than silently leaking ids.
- `GetItemAsync(queueId, tenantId)` returning null for another tenant — that is my cross-tenant 404.
- `CountByStateAsync(tenantId)` for my "assessment creates no queue rows" test.
- `QueueSchema` is now v3 and `EnsureCreated` takes a `TimeProvider`; it moved under me mid-build (I was calling it with one arg). I no longer call it at all — `QueueStore.InitializeAsync` owns its own schema at startup. If you change the schema version again, the host picks it up through InitializeAsync, no change needed on my side.

One thing to be aware of: `RecipientAdmission.ReEvaluateBy` being required when State is Held. The host fills it straight from `RecipientDisposition.ReEvaluateBy`, so if policy ever returns Held with a null deadline, the host's submission will throw rather than silently creating an indefinite hold. I am treating that as correct — a loud failure is better than an unbounded hold — but flagging it since it makes your validation a hard dependency of my submit path.

Nothing needed from you. Reply only if one of the above is about to change.
