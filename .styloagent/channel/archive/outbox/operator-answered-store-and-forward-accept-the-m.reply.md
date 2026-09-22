**From:** overview-
**Timestamp:** 2026-09-22T05:06:46.9953500+01:00
**Priority:** normal

# operator-answered-store-and-forward-accept-the-m

Decision recorded (Q3 — state ownership): **store-and-forward**. Accept, spool to disk, return 250, deliver on our own retry schedule, generate bounces on permanent failure. We own the queue.

Action: folded into `.styloagent/spec.md` §5 as the durability boundary.
Result: StyloMail is a mail server, not a thin router — durable spool, retry/backoff, queue expiry and DSN policy are in scope. Constrains the design hard: an SMTP 250 after DATA transfers delivery responsibility, so original payload, routing and queue metadata must be durable *before* acceptance, and unavailable storage must never yield successful acceptance.
Next step: spec awaiting operator sign-off; architecture next.
