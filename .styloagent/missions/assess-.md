# `assess-` — composition root and semantic cache

## Your scope
Own `src/StyloMail.Assessment/` and `tests/StyloMail.Assessment.Tests/`. Create them.
Do **not** modify any other project. `mime-`, `adaptive-`, `queue-` and `host-` are actively
building their own components and **you must not edit their files**. If you need a change to their
API, `send_message` the owner directly (`mime-`, `adaptive-`, `queue-`, `host-`) and copy
`overview-` — do not patch around a missing API by reimplementing it.

**Your components are still in flight.** Expect to read their public interfaces as they land. Start
with what exists, and coordinate rather than block.

## The pipeline shape — this is fixed, do not redesign it
From `spec.md` §5, in this exact order:

1. Validate envelope, authorization, size and parsing limits; apply mandatory hard limits.
2. Parse an analysis copy and extract deterministic evidence. → `mime-`'s `IMimeMessageAnalyzer`
3. Read profile snapshots; reserve/update atomic observed-rate counters for this attempt. → `adaptive-`
4. Obtain semantic evidence from an exact cache hit, the provider, or an explicit *unavailable*
   state. → `jev-`'s `ISemanticMailClassifier`, wrapped by **your** cache decorator
5. Compare against profiles and recent campaign windows; compute drift and trend evidence. → `adaptive-`
6. Run versioned deterministic policy and persist the decision. → `MailPolicyEngine` (`StyloMail.Policy`)
7. For submissions: durably accept and schedule, or decline responsibility **before** acceptance.
   → `queue-`
8. Commit trusted learning **only** when an authorized outcome or explicitly permitted rule exists.

## What to build
1. **`MailAssessor : IMailAssessor`** — composes the above. It is the single entry point Host
   consumes. `host-` already takes `IMailAssessor` as an injected port and returns 503 when it is
   unregistered; registering yours is what makes the pipeline live.
2. **Semantic cache decorator** over `ISemanticMailClassifier` (source spec §8):
   - Exact keys include tenant, **resolved model version**, question schema version, preprocessing
     version, and a digest of the complete canonical classifier input. If relationship context went
     into the input, it is part of the key — the key is over the *whole* input.
   - **Single-flight**: concurrent identical requests must collapse to one provider call.
   - Configurable expiry, model/schema invalidation, bounded LRU/LFU eviction, sampled
     reclassification.
   - **Never memoise "allow this sender."** Cache assessments, not permissions. Every message still
     receives current authentication, URL, behavioural counter, profile and policy checks.
   - The persisted response must retain the evidence distribution, timestamp, coverage and provenance.
   - Near-duplicate matching supplies **campaign evidence only**; it must never be reused as an
     assessment. Semantically similar wording with a *changed bank account* must miss the reuse gate —
     link destinations, payment identifiers, sender context and attachment hashes are
     security-bearing and must not be smoothed away.

## Hard constraints
1. **`AssessmentContext.AssessmentOnly == true` means no delivery, no learning, no live traffic
   accounting.** Route those to the assessment path only — never into queue acceptance.
2. **`AssessmentContext.ShadowMode`** records the proposed action while still allowing forwarding.
   It is a mode, not an action.
3. **Use `AssessmentContext.TimeProvider` for everything.** No `DateTimeOffset.UtcNow` in logic.
   Replay depends on a fixed clock and isolated state.
4. **Persist a decision for every assessed message**, including allows, with the full version stamp
   (`policy`, resolved classifier model, question schema, preprocessing) and cache provenance.
5. **`PayloadReferences.RequireDurable`** guards the acceptance path. Assessment-only inputs carry
   `PayloadReferences.Ephemeral` and must never reach durable acceptance.
6. Missing semantic evidence stays `Unavailable` through composition — do not let an outage become
   an allow anywhere in the wiring.

## Tests
xUnit with a fixed clock and fakes for MIME/Jev/Adaptive/Queue. Cover: the pipeline runs in the
stated order; assessment-only creates no queue state and commits no learning; cache hit skips the
provider and a model-version change invalidates the entry; single-flight collapses concurrent
identical calls; a near-duplicate with a changed bank account does **not** reuse the cached
assessment; unavailable semantic evidence propagates as Unavailable and never as allow; shadow mode
records a proposed action without changing delivery.

## Build notes
- `dotnet` is NOT on PATH:
  `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`
- SDK 10.0.201, TargetFramework `net10.0`. **Several agents may edit `StyloMail.slnx`
  concurrently** — if `dotnet sln add` fails, retry once, and always verify with your own csproj.
- Do not run `git add` or `git commit`. **Never read, print, or reference `jevkey.pvt`.**

## Done when
Your tests are green and you have seen them run. Report to `overview-`: files created, test count,
which component APIs you depended on, any friction with an owner, and anything deliberately not done.