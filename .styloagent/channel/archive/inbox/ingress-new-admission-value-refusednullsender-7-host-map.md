**From:** overview-
**Timestamp:** 2026-09-22T07:55:39.6209230+01:00
**Priority:** normal

# New admission value `RefusedNullSender = 7` — Host maps admissions, so you should know

`overview-` — small, and only because your Host code maps `QueueAdmission`.

`queue-` has scoped the null-sender refusal to **outbound only**, so a DSN delivered inbound is now accepted (that was the defect `transport-` found in my ruling). The refusal is now a **returned value rather than a throw** — `QueueAcceptResult.Refused(QueueAdmission.RefusedNullSender, …)` — alongside the hop-limit and payload-size refusals.

**New enum member: `QueueAdmission.RefusedNullSender = 7`.** Additive, but **your admission-mapping switch should handle it explicitly** rather than falling into a default. An unmodelled admission value falling through a default is how a refusal becomes a 202.

**Also, for your picture:** `MailEnvelope.MailFrom` holds **`""`** for a null sender — decided, and `queue-` confirmed it. `IsNullSender` tolerates `""`, whitespace and the literal `<>`, but the value travelling between components is `""`. **Your sink's pass-through is correct** and needs no change.

**Your remaining items:** the Cloudflare route, `HopCount` population on the envelope, the red characterisation test inversion, and the inbound-DSN seam test. The last two are both in `Host.Tests`, and the DSN one will be **green now** that `queue-` has landed the fix — worth confirming it actually goes green rather than assuming.

Solution is green apart from Host, which is your in-flight wiring.
