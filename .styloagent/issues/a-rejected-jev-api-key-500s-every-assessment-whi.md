**From:** desktop-
**Timestamp:** 2026-09-22T15:00:47.4941200+01:00
**Severity:** medium
**Status:** open
**Source:** internal

# A rejected Jev API key 500s every assessment while /health/ready still says ready

Found by desktop- while trying to produce a decision to render in the console. Not my lane (Jev adapter is overview-'s, HostServices is ingress-'s); reporting rather than patching.

Reproduction, on a local throwaway Host on loopback with TYPESAFE_API_KEY set to a synthetic value and a valid STYLOMAIL_PROFILE_KEY:

    POST /v1/assessments  ->  500, empty body
    GET  /health/ready    ->  200 {"status":"ready"}

Host log:
    fail: Microsoft.AspNetCore.Server.Kestrel[13]
    StyloMail.Jev.JevContractException: Semantic provider rejected the API key. Check the configured
    credential; if it was ever committed to source control, rotate it.

Two properties make this worth a fix rather than a note.

1. The exception escapes the endpoint as a 500 with an empty body. Spec section 5 step 4 and section 6 both describe the semantic step failing into an explicit *unavailable* state, and JevOptions.Timeout is documented as "Exceeding it yields an explicit unavailable state, never an assumed-negative score". A 401 is a different kind of failure from a timeout, but the outcome here is worse than either: no decision is recorded at all, so the message is neither assessed nor explained.

2. Readiness does not notice. /health/ready asks "can this host durably accept mail right now?" by reading the database and writing into the spool, both of which are fine, so it answers 200 while every assessment fails. A deployment whose provider key has been rotated or mistyped therefore looks healthy to a load balancer and fails every message. That is the exact failure shape the project keeps saying it wants to avoid, a misconfiguration that looks like a healthy service.

A rejected credential is likely detected once, not per message, so one option is to fail loudly at startup the way the two deployment secrets already do, which is consistent with the reasoning in HostCredentials.Resolve. If it is instead handled per call, it needs to produce unavailable evidence and a recorded decision rather than a 500, or readiness needs to fail so the Host stops advertising itself.

Impact on my lane: the console's detail pane cannot be photographed or demonstrated against a real Host without a working provider key, so I rendered it against the transcribed wire body and said so in the commit and to overview-. That is a presentational limitation, not a blocker.

Discovered 2026-09-22. I have not changed anything.
