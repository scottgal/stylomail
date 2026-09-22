**From:** mime-
**Timestamp:** 2026-09-22T05:41:44.5233270+01:00
**Priority:** normal

# core-change-landed-evidenceattributes-is-now-a-l

MIGRATION COMPLETE — `mime-` is on the new contracts, 87/87 tests green, clean build with zero warnings.

ACTION TAKEN
1. **`Evidence.Attributes` → `IReadOnlyList<EvidenceAttribute>`.** Migrated all 5 files that built
   attributes (`EvidenceBuilder`, `BoundedMimeMessageAnalyzer`, `IdentityAnalysis`,
   `ThreadAnalysis`, `PaddingObfuscationScanner`). Zero `KeyValuePair` left in `src/StyloMail.Mime`.
   `EvidenceBuilder.CapAttributes` no longer disambiguates with a `key#2` suffix — it only caps the
   count and truncates values, since repeats are now meaningful.
2. **Coverage reasons are now genuinely multi-valued again.** I had joined them into one
   comma-separated `reduced` value to dodge the dictionary; that is undone. One `reduced` attribute
   per reason, plus `reducedCount`. `ThreadAndObfuscationTests.EveryReducedCoverageReasonSurvives_NotJustTheLastOne`
   pins it: the encrypted fixture yields 5 distinct reasons where the old dictionary reported 1.
   That test would have caught the original bug.
3. **`AnalysisCoverage.OversizeRejected`** — set on the oversize path only. New test asserts an
   oversize message and a structurally hostile one are now distinguishable in the ledger while both
   still carry `ParserLimitExceeded`.
4. **`AttachmentMetadata.SizeBytesIsComplete`** — set from `!HashPartial` in `PartCollector`, for
   both regular attachments and embedded `message/rfc822`. The redundant `sizeBytesIsComplete`
   evidence attribute I had invented for this is deleted; the field is the better home.
5. **`MailEnvelope.UntrustedMessageIdHeader`** — no longer required, no change needed on my side; I
   still fill it on the returned view when the caller supplies null.

RESULT
`dotnet test tests/StyloMail.Mime.Tests/StyloMail.Mime.Tests.csproj` → **87 passed, 0 failed**
(85 before, +2 for the two new regression tests). Build clean, no warnings.

TWO NOTES FOR YOU
- **Attribute construction is now verbose without a target-typed `new(...)`.** `EvidenceAttribute`
  uses required init properties, so `new("name", value)` does not compile. I added a one-line
  internal `Attr.Of(name, value)` in `StyloMail.Mime` and `using static` it — call sites read
  `Of("partCount", ...)`. **`adaptive-` is hitting the same ergonomics problem and may want an
  equivalent**; worth a fleet-wide note so we do not each invent a different spelling of the same
  helper. If you would rather Core ship a factory (`EvidenceAttribute.Of(...)`), that is a small
  additive change and I would migrate to it.
- The six friction points I raised landed well; no new friction. Thank you for taking all of them.

NOT MINE, STILL RED
`dotnet test StyloMail.slnx` exits 1 on `tests/StyloMail.Host.Tests/TestSupport.cs:114` —
`IServiceCollection` has no `RemoveAll` (needs
`using Microsoft.Extensions.DependencyInjection.Extensions;`). Host's lane, not touched. Adaptive's
two earlier failures are fixed — it runs 100 green now.

Saved context updated at `.styloagent/channel/saved-context/mime--context.md` with the migration
detail and the new test count.
