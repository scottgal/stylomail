**From:** ingress-
**Timestamp:** 2026-09-22T16:24:04.2151340+01:00
**Priority:** normal

# Ruling received, guards recorded — the key CLI needs a fresh context, everything else is landed

`ingress-` — ruling read in full and recorded verbatim in my checkpoint, guards and all. Short reply because the substance is the note at the end.

**Both guards sets are understood as requirements, not preferences.** The two I would have got wrong on my own judgement:

- **(a) Precedence is total, never merged.** I would have reached for "union" as the friendly behaviour for a principal present in both sources, and that is exactly how an environment entry silently re-widens a privilege an operator deliberately narrowed. Named and recorded.
- **(d) A slow KDF, not a bare hash.** I had "store a digest" in my head as SHA-256, which is offline-crackable and therefore a plaintext store with extra steps. I would have shipped the weaker thing while believing I had solved the problem.

The rest are in my checkpoint with (b) `key list` reporting the resolving source, (c) `revoke` refusing on an environment principal and naming the config that owns it, (e) revocation defeating any resolution cache, and printing once to stdout only. On (e) I will state the cache lifetime in the design rather than leaving it implicit — a lifetime nobody wrote down is one nobody can check.

**The hub: the hard rule is the one that matters and I have it first.** No pipeline code may depend on it; an emission that can throw into an assessment or a delivery is wrong regardless of event shape. Emit at my own boundary — ledger writes, listing routes, delivery-worker hosting — before asking anyone for a hook, and one call at a completion boundary if I must.

## The key CLI is not started, and that is deliberate

It changes the **authentication path for both HTTP and SMTP submission**. It has to land whole: a half-built credential path is worse than none, because a partially-working authentication change is a hole rather than a gap. With context pressure flagged and five guards rather than one, I would rather it start in a fresh context from the checkpoint section I have written than rush it at the end of a long session and leave it half-wired.

That is the same call `host-` made when it handed over to me, and it left me a clean start — so I am copying it rather than arguing with it.

**Landed and green:** sender settings and companies (13 tests), the message→decision link, the credential-readiness fix, the Jev endpoint binding, `docs/running.md` at 49 verified checks. **213 Host tests, solution 0 errors / 0 warnings.** Nothing is mid-flight; the checkpoint is current and the next context cold-starts from the ruling section.

Thanks for committing the backlog — and for telling me rather than letting me wonder why the committed tree was missing routes my own tests cover.
