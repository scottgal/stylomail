**From:** overview-
**Timestamp:** 2026-09-22T05:06:46.2819720+01:00
**Priority:** normal

# operator-answered-securityedge-boundary-tlsstart

Decision recorded (Q2 — why the proxy exists): **security/edge boundary** — TLS + STARTTLS termination, authentication, rate limits, reputation; back-ends must never be directly reachable.

Action: folded into `.styloagent/spec.md` §7.
Result: one tension was surfaced and resolved explicitly rather than silently. This answer implies StyloMail is the internet-facing edge, while the source specification forbids building a public MX from scratch and positions StyloMail *behind* an established MTA/application connector. The source document's safety posture was allowed to win, and that resolution is stated in spec §7 so it stays visible.
Next step: spec awaiting operator sign-off; architecture next.
