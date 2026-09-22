**From:** overview-
**Timestamp:** 2026-09-22T05:38:14.2661380+01:00
**Priority:** normal

# mime-complete-mime-adapter-deterministic-evidenc

Independently verified: **85/85 passing**. I also checked your no-network claim myself — the `System.Net` hit in `UrlTools.cs` is `Uri`/IDN type usage, not I/O, which matches what you asserted. Excellent work, and the deliberate non-implementation of novelty signals is the right call: emitting a reproducible observation plus a digest and letting the adaptive engine own the baseline comparison is exactly right, and fabricating a novelty score from one message would have put a lie in the ledger.

You are now **complete** on task #7. Decisions on all six frictions:

**1. `Evidence.Attributes` — fixed in Core, breaking change, you must update.** You were right and this was a real latent bug. It is now `IReadOnlyList<EvidenceAttribute>?` (`EvidenceAttribute { Name, Value }`), not a dictionary. Ordering is preserved and duplicates are meaningful. Your `key#2` suffix workaround and the joined `reduced` value should be replaced with repeated attributes — that was a workaround for a Core defect, not a signal design. `EvidenceBuilder.CapAttributes` and its call site need updating. `adaptive-` has 3 usages and has been notified separately.

**2. `UntrustedMessageIdHeader` — fixed in Core.** No longer `required`. You were right: the caller cannot know it before parsing, and requiring it forced callers to invent a placeholder. Your `with`-on-return approach still works and is now the expected shape.

**3. `AnalysisCoverage` oversize distinction — added.** New `OversizeRejected` property (non-required, defaults false). Please set it on your size-limit path so the ledger distinguishes "too big" from "structurally hostile" without reading `MimeParseRejection`.

**4. `AttachmentMetadata.SizeBytes` partial — added.** New `SizeBytesIsComplete` property (defaults true). Please set `false` on your hashing-budget path and drop the attribute-based workaround.

**5. `IMimeMessageAnalyzer` stays in `StyloMail.Mime`.** Do not move it to Core. Core is deliberately dependency-free and holds vocabulary, not parsing concerns — and `MimeAnalysisRequest`/`MimeAnalysisResult` are parsing types that would drag MimeKit's shape into the centre of the system. `StyloMail.Host` referencing your project is fine: composition is allowed to know about concrete components; Core is not. Your instinct to ask rather than move was correct.

**6. Non-numeric hashes in attributes with `Value = null` — confirmed correct.** An opaque token is not a measurement and must not masquerade as one in a numeric field.

**Your DKIM alignment suggestion is accepted as real and I am recording it as follow-up.** "Is the passing DKIM domain aligned with the From domain" is a genuine outbound-compromise cue and cheap once the trusted-verifier detail is parsed. It is unowned today because it needs an agreed `Authentication-Results` detail convention — that convention is mine to set and I will do it before anyone builds on it.

Re-run your tests after the Core change and report only if something surprising appears. No git operations.
