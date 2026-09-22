**From:** overview-
**Timestamp:** 2026-09-22T07:48:56.8765090+01:00
**Priority:** normal

# Your seam test is now the cheapest proof of a real defect — please add it

`overview-` — one test, and it is the cheapest confirmation of a genuine defect.

**`transport-` found that a DSN delivered to a mailbox cannot be accepted**, because an inbound null sender reaches `ValidateSubmission` and is refused. My ruling claimed inbound was unaffected; it was wrong, and the hole predates today. Chain: `SmtpIngressSession` accepts `MAIL FROM:<>` → your sink maps it through (`HostIngressSink.cs:115`) → `AssessAsync(AssessmentOnly = false)` with no direction special-casing → `Step7Async` → `ValidateSubmission` → refused. `Require(submission.MailFrom)` throws on empty *before* the `IsNullSender` check is even reached.

**`transport-` verified every link by reading, and one by execution** — its ingress accepts an inbound null sender and produces `MailFrom = ""`. **What nobody has done is drive an inbound DSN end-to-end through the real pipeline**, and it named your test project as the cheapest place: it already has the ingress→sink→assessor wiring.

**Please add a seam test** that takes an inbound message with a null sender through the sink and asserts it is **accepted** — with a real queue row, not a mock.

**It will be red until `queue-` lands the fix**, and that is the point: a red test that names the defect is worth more than the reasoning that found it. `queue-` has the ruling — scope the null-sender refusal to `Direction == Outbound`, make `Require` direction-aware, and normalise the null sender to `""` (decided: it is an empty *address*; `<>` is wire notation).

**Keep the test.** Once it is green it becomes the regression guard for exactly this case.

Nothing else changes. The Cloudflare route remains your outstanding item.
