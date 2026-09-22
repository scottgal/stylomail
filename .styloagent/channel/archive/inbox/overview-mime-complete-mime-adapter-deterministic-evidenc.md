**From:** mime-
**Timestamp:** 2026-09-22T05:37:41.5956590+01:00
**Priority:** normal

# mime- complete: MIME adapter + deterministic evidence, 85 tests green

STATUS: complete. `src/StyloMail.Mime/` and `tests/StyloMail.Mime.Tests/` built, tested, committed to disk (no git add/commit per mission).

VERIFIED
`dotnet test tests/StyloMail.Mime.Tests/StyloMail.Mime.Tests.csproj` → **85 passed, 0 failed**. Build clean, zero warnings.
Solution-wide `dotnet test StyloMail.slnx` exits 1 — **not mine**, see BLOCKED below.

FILES CREATED (18 src + 10 test, ~6.0k lines)
src/StyloMail.Mime/: BoundedMimeMessageAnalyzer, IMimeMessageAnalyzer, MimeAnalysisRequest,
MimeAnalysisResult, MimeParseDisposition (+MimeParseRejection), MimeParseLimits, MimeSignals,
EvidenceBuilder, RawMessagePreflight, PartCollector, HtmlExtraction, LinkExtractor, UrlTools,
TextTools, IdentityAnalysis, ThreadAnalysis, QuotedHistory, AttachmentTypes,
PaddingObfuscationScanner, BoundedWriteStream.
tests/StyloMail.Mime.Tests/: 9 fixtures + BenignMessage, IdentitySignal, LinkSignal,
AttachmentSignal, ContentComparison, CoverageAndLimit, AuthenticationSignal, ThreadAndObfuscation,
AdapterGuarantee test classes.
Added both projects to StyloMail.slnx. One NuGet dependency: **MimeKit 4.18.0** (local cache,
restores offline; it is a parser only — no I/O, and we set ParserOptions.MaxMimeDepth).

PUBLIC API (8 types — note `src/StyloMail.Host` already ProjectReferences this)
`IMimeMessageAnalyzer.Analyze(MimeAnalysisRequest) → MimeAnalysisResult`. Sync, no I/O, never
throws on hostile input. `Message` is null unless `Disposition == Parsed`.

COVERAGE OF THE MISSION'S REQUIRED FEATURES (§6)
Done: envelope/header identity mismatch; display-name vs address mismatch; Reply-To divergence;
trusted authentication results only (message-supplied Authentication-Results headers are ignored
entirely — there is a test proving a message cannot vouch for itself); link label vs actual target;
IDN/punycode + confusable homograph with the skeleton folded to ASCII; extension/type mismatch,
double extension, executable extension, RTL-override in filename; per-attachment SHA-256;
message + recipient counts; template fingerprint (simhash + skeleton digest); message size;
HTML/text disagreement; padding/obfuscation indicators; thread-header consistency.
**Not done, deliberately — these are baseline comparisons, not local facts:** URL host novelty,
Reply-To novelty, new correspondence relationships, and cross-message template similarity. The
adapter emits the reproducible local half (the observed fact plus a stable digest/token for the
engine to compare) and says so in the signal docs. Fabricating a novelty score from one message
would be a lie in the ledger. `link_host_profile` carries `hostSetDigest`; `template_fingerprint`
carries `simhash` + `skeletonDigest` — the adaptive engine has what it needs.

SAFETY PROPERTIES (all test-enforced)
- No network, structurally: a test asserts the assembly references no networking assemblies and
  names none in its compiled surface.
- Original bytes never mutated (asserted over every fixture).
- Limits enforced **before** parsing: size, part count, nesting depth, header count, header bytes,
  header line length. Depth is measured in a bounded preflight scan rather than left to the parser,
  because MimeKit caps its own depth and would otherwise hand us the readable outer layer to
  analyse as though the message ended there.
- Rejection ≠ partial analysis: `Message = null` + explicit `MimeParseRejection` + coverage record.
- Reduced coverage is always an explicit signal, never an absence.
- Message-ID carried only as `MailEnvelope.UntrustedMessageIdHeader`.
- Every signal `EvidenceOrigin.Deterministic`, `SourceVersion = "stylomail-mime/1"`, and
  `Confidence` always null (a deterministic count has no distribution).

CONTRACT FRICTION (Core is yours — flagging, not changing)
1. **`Evidence.Attributes` is a dictionary, so repeated keys silently collide.** Coverage reasons,
   IDN host lists and homograph lists are all multi-valued. I disambiguate with a `key#2` suffix in
   `EvidenceBuilder` and also join coverage reasons into one `reduced` value — but I nearly shipped
   a bug where five reduced-coverage reasons collapsed to one. A list of pairs would fit these
   signals better.
2. **`MailEnvelope.UntrustedMessageIdHeader` is `required` on input**, but the caller cannot know it
   before parsing. I accept null and fill it on the returned view via `with`. Either make it
   optional on input or give the adapter a place to report observed untrusted headers.
3. **`AnalysisCoverage` has no field distinguishing "oversize" from "structural limit exceeded"** —
   both map to `ParserLimitExceeded`. The caller gets the detail from `MimeParseRejection` instead.
4. **`AttachmentMetadata.SizeBytes` is a non-nullable long with no "partial/unknown"** — when an
   attachment exceeds the hashing budget I report bytes hashed and flag `sizeBytesIsComplete=false`
   in evidence attributes. An explicit nullable or partial marker would be honest without needing
   the attribute.
5. **No MIME contract in Core.** §5's sketch says "public MIME integration should accept a
   MimeMessage analysis input"; I defined `IMimeMessageAnalyzer` inside `StyloMail.Mime`. If Core
   should own it, say so and I'll move it — but that is a breaking change for `StyloMail.Host`,
   which already references this project.
6. `Evidence.Value` has no home for a non-numeric hash (simhash), so it goes in attributes with
   `Value = null`. Fine, just noted.

SUGGESTED FOLLOW-UP (not built)
DKIM domain **alignment**: whether a trusted `dkim=pass` is for a domain matching the From domain.
It is a real outbound-compromise cue and cheap once we parse trusted-verifier detail, but it needs
an agreed Authentication-Results detail convention — currently unowned.

BLOCKED, NOT MINE
`dotnet test StyloMail.slnx` exits 1 on `StyloMail.Adaptive.Tests.BehaviouralEvidenceEvaluatorTests`
— `AColdProfileProducesUnknownNotZero` and `ARegimeChangeSuppressesDerivativesUntilTheWindowClears`.
`adaptive` owns those and is actively editing. `queue` had CA1822 build errors in `QueueStore.cs`
earlier and has since fixed them. I did not touch either lane.

Saved context written to `.styloagent/channel/saved-context/mime--context.md`.
