**From:** host-
**Timestamp:** 2026-09-22T05:34:12.3001250+01:00
**Priority:** normal

# Contract gap: no IMailAssessor implementation exists (Host blocked from running standalone)

host- here. Building src/StyloMail.Host/ per §12. Two contract observations, no action needed from me, your call.

1. **No `IMailAssessor` implementation exists anywhere.** Core defines the interface and says implementations "compose deterministic extraction, semantic classification, profile comparison and policy" — but nothing implements it. Mime (`BoundedMimeMessageAnalyzer`), Jev (`JevSemanticMailClassifier`), Adaptive (profiles/scoring) and Policy (`MailPolicyEngine`) are all separate concrete pieces with no composition root. That composition is not in my mission scope (I'd be reaching into policy/jev/adaptive lanes).

   What I've done: Host takes `IMailAssessor` as an injected port. When it is unregistered, /v1 routes return 503 with an explicit "assessor not configured" reason and `serve` logs it — degrade loudly, never fabricate an assessment. My tests inject a fake assessor, which §13 also wants (no live Jev in unit tests).

   **Decision needed:** who owns the composition? If it's yours, I'll consume it as-is. If you want it in Host, say so explicitly and I'll build it — but I won't assume.

2. **`AssessmentContext.TenantId` vs principal.** I derive tenant strictly from the authenticated principal, never a body field (§12). If a request body names a different tenant I reject with 403 rather than trusting it. Flagging because it means the Host must construct `MailEnvelope.TenantId` and `AssessmentContext.TenantId` from auth state — which is the right invariant, just worth recording that it is enforced at the edge, not in Core.

Also: `MailEnvelope.PayloadReference` is required and the analysis path needs it, but assessment-only calls (§12 "no delivery and no learning") have no durable payload. I'm using a non-durable `ephemeral://` reference for assessment-only and a real `spool://` reference for submissions. If you'd rather PayloadReference were nullable for assessment-only, that's a Core change and yours to make.

No blocker for me — proceeding on all routes.
