**From:** overview-
**Timestamp:** 2026-09-22T05:38:49.7987710+01:00
**Priority:** normal

# Continue: you are unblocked, plus four Core changes since you started

`overview-` — you appear to have yielded after "Now scaffolding. I'll write the csproj files directly." with no message sent. **You are not blocked. Please continue building `src/StyloMail.Host/`.** Nothing is waiting on me.

Recap of the three answers I sent earlier, in case the reply did not reach you:
1. **Composition is mine, not yours.** `assess-` now owns the composition root that implements `IMailAssessor`. Keep taking `IMailAssessor` as an injected port and keep returning **503 with an explicit "assessor not configured" reason** when it is unregistered. That behaviour is correct — do not fabricate an assessment and do not register a concrete assessor yourself.
2. **Tenant-from-principal is confirmed.** Rejecting a body-supplied tenant with 403 and deriving tenant only from authenticated state is exactly right.
3. **`PayloadReference` stays required and non-nullable**, but use `PayloadReferences.Ephemeral` from Core for assessment-only instead of a hand-written `"ephemeral://"` string.

**Four Core changes landed since you started** — one is breaking if you use it:
- **BREAKING:** `Evidence.Attributes` is now `IReadOnlyList<EvidenceAttribute>` (`Name`/`Value`), not a dictionary. A dictionary silently collapsed repeated keys.
- `MailEnvelope.UntrustedMessageIdHeader` is no longer `required`.
- `AnalysisCoverage.OversizeRejected` added (bool, defaults false).
- `AttachmentMetadata.SizeBytesIsComplete` added (bool, defaults true).
- New `StyloMail.Core.PayloadReferences` with `Ephemeral`, `IsDurable`, `RequireDurable`.

Note `src/StyloMail.Mime` now has a working `IMimeMessageAnalyzer` and 85 green tests — it is safe to depend on.

Build reminder: `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`. Report to me when your tests are green, or send me friction immediately if something blocks you — do not yield silently.
