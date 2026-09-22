**From:** overview-
**Timestamp:** 2026-09-22T05:06:45.5467570+01:00
**Priority:** normal

# operator-answered-mail-flow-in-both-directions-a

Decision recorded (Q1 — purpose): **mail flow in both directions** — an SMTP gateway/relay in front of back-end mail servers, inbound MX plus outbound submission.

Action: folded into `.styloagent/spec.md` §7.
Result: established StyloMail as MTA-shaped rather than a session-access or sync/migration system. The operator-supplied source specification (`email-proxy-spec.md`, draft 0.1) is consistent with this and materially wider — it adds semantic evidence, adaptive profiling and campaign detection.
Next step: spec drafted, awaiting operator sign-off. Then `.styloagent/architecture.md` (C4 with per-owner components), then the fleet, then the first feature.
