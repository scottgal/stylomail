# mime-, saved context

## Identity + scope
- Prefix: `mime-` (depth 1, parent `overview-`).
- Role: **MIME parsing and deterministic feature extraction** for StyloMail.
- Owns exactly two trees: `src/StyloMail.Mime/` and `tests/StyloMail.Mime.Tests/`. Nothing else.
- Must not edit `src/StyloMail.Core/`, `src/StyloMail.Persistence/`, `src/StyloMail.Jev/`,
  `.styloagent/spec.md`, or `email-proxy-spec.md`.

## Repo state
- Root: `/Users/scottgalloway/RiderProjects/stylomail/`, branch `main`, no commits yet
  (`git status` shows everything untracked; the mission forbids `git add`/`git commit`).
- Solution `StyloMail.slnx` (.NET 10 format). SDK 10.0.201, `net10.0`.
- **Every shell needs:** `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`.

## Deliverable, COMPLETE
- `src/StyloMail.Mime/`, 18 files, ~2.7k lines. One NuGet dependency: **MimeKit 4.18.0**
  (cached locally, restores offline). Core contracts consumed read-only.
- `tests/StyloMail.Mime.Tests/`, 9 fixtures + 10 test classes. **87 tests, all green.**
- Verified: `dotnet test tests/StyloMail.Mime.Tests/StyloMail.Mime.Tests.csproj` → 87/87 passed.
- Solution-wide `dotnet test StyloMail.slnx` currently exits 1 **because of another agent's
  project**, not this one, see "Blocked / not mine" below.

## Blast radius, who depends on this project
`StyloMail.Mime` is referenced by **`src/StyloMail.Assessment`** and **`src/StyloMail.Host`**
(plus their test projects). Changing its public surface breaks them.

**Fleet rule (from `queue-`, via `overview-`):** build *your own* project to stay unblocked, but
**`dotnet build StyloMail.slnx` before declaring done**, a shared-project change is invisible from
inside your own lane. My project being green is not the claim "nothing I did broke anyone".

Verified 2026-09-22: solution builds clean; Mime, Assessment and Host all build. A transient red was
observed during the window, `SendingQuotaLedger` signature churn between `adaptive-` and `assess-`
, which resolved itself. Not mine, and not reported: it was in-flight churn in two actively-editing
lanes, and raising it would have cost two agents' attention for nothing.

## Public API surface (8 types, this is a contract; `StyloMail.Host` already references the project)
`IMimeMessageAnalyzer` · `BoundedMimeMessageAnalyzer` · `MimeAnalysisRequest` · `MimeAnalysisResult` ·
`MimeParseDisposition` (+ `MimeParseRejection`) · `MimeParseLimits` · `MimeSignals`.

Entry point: `new BoundedMimeMessageAnalyzer().Analyze(MimeAnalysisRequest) → MimeAnalysisResult`.
Sync, no I/O, never throws for hostile input. `MimeAnalysisResult.Message` is null unless
`Disposition == Parsed`.

## Hard rules baked in
1. **No network, structurally.** No `HttpClient`/socket code path; a test asserts the assembly
   references no networking assemblies and names none in its compiled surface.
2. **Original bytes never mutated**, asserted by a test over every fixture.
3. **Limits enforced before parsing** (size, part count, **nesting depth**, header count, header
   bytes, header line length). Depth is measured in the preflight because leaving it to the parser
   would silently truncate a nesting bomb and analyse the readable layer as if it were whole.
4. **Rejection ≠ partial analysis.** A rejected message returns `Message = null`, an explicit
   `MimeParseRejection`, and a coverage record. Never a fragment dressed as a whole message.
5. **Reduced coverage is always a signal** (`deterministic.analysis_coverage`), never an absence.
6. **Message-ID is untrusted**: carried through as `MailEnvelope.UntrustedMessageIdHeader` only,
   never used as a key.
7. **Thread headers are claims**, only internal consistency is checked, never resolved.
8. **A message cannot authenticate itself**: `Authentication-Results` headers in the message are
   ignored entirely; only `AuthenticationContext` from a trusted verifier is consumed.

## Signal catalogue (all `EvidenceOrigin.Deterministic`, all `SourceVersion = "stylomail-mime/1"`)
`envelope_header_identity` · `display_name_address_mismatch` · `reply_to_divergence` ·
`trusted_authentication_failure` · `authentication_provenance` · `link_display_mismatch` ·
`link_idn` · `link_idn_homograph` · `link_host_profile` · `attachment_type_mismatch` ·
`attachment_hash` (one per attachment) · `attachment_unavailable` · `recipient_count` ·
`message_structure` · `template_fingerprint` · `message_size` · `html_text_disagreement` ·
`padding_obfuscation` · `thread_header_consistency` · `quoted_history` · `content_encrypted` ·
`analysis_coverage` · `html_markup_observation`. All ids are prefixed `deterministic.`.

## Deliberately NOT done (novelty needs a baseline; the adaptive engine owns the observed store)
- **URL host novelty**, emits `link_host_profile` with a stable `hostSetDigest` of the distinct
  host set. Comparing it against a recipient's history is the adaptive engine's job.
- **New correspondence relationships / Reply-To novelty**, emits the local `reply_to_divergence`
  fact and the envelope identity keys; novelty is not fabricated.
- **Template similarity**, emits `template_fingerprint` (simhash + skeleton digest). The
  cross-message Hamming comparison belongs to whoever holds the other messages.
- **DKIM/SPF verification**, would need the original connection context and DNS (network). Only
  trusted-verifier results from `AuthenticationContext` are consumed.
- **DKIM domain alignment** (does a trusted `dkim=pass` match the From domain?), not implemented;
  needs Authentication-Results parsing conventions and is arguably policy. Follow-up.
- **HTML is scanned, not DOM-parsed**, bounded regexes, all `RegexOptions.NonBacktracking`.
- **Attached `message/rfc822` is hashed, not descended into**, bounds work per message.

## Core contract migration, DONE (2026-09-22)
`overview-` landed the six friction points I raised. Four were adopted; two I migrated to:
- **`Evidence.Attributes` is now `IReadOnlyList<EvidenceAttribute>?`** (was a dictionary). Repeated
  names are meaningful and order is preserved. `EvidenceBuilder.CapAttributes` no longer dedupes,   it only caps the count and truncates values. Coverage reasons are now **one `reduced` attribute
  per reason**; read them with `AttributesNamed("reduced")`, not `Attribute("reduced")` (that
  returns only the first). `ThreadAndObfuscationTests.EveryReducedCoverageReasonSurvives_NotJustTheLastOne`
  is the regression guard.
- `MailEnvelope.UntrustedMessageIdHeader` is no longer `required` on input.
- `AnalysisCoverage.OversizeRejected`, set on the oversize path only, so "too big" is
  distinguishable from "structurally hostile". Both still set `ParserLimitExceeded`.
- `AttachmentMetadata.SizeBytesIsComplete`, set from `!HashPartial` in `PartCollector`.
- `StyloMail.Core.PayloadReferences`, not used by this lane (the queue owns the accept path).
- Attribute construction shorthand: `Attr.Of("name", value)` via `using static StyloMail.Mime.Attr;`.
  There is no `new("name", value)` target-typed form because `EvidenceAttribute` uses required
  init properties.

## Gotchas hit (worth knowing if you touch this code)
- `RegexOptions.NonBacktracking` **rejects bounded repeats**, `[^>]{0,4096}` blew the 10 000-node
  automaton limit. Use `*`/`+` instead, and beware `{0,61}` inside loops.
- MimeKit's `CharsetUtils` is `internal`. Use `TextPart.Text` (bounded by the message size limit).
- `MailboxAddress.Address` returns the **Unicode** form of an IDN, while SMTP envelopes carry
  punycode. Addresses must be compared after `IdnMapping.GetAscii`, or every IDN sender looks like
  an impersonation attempt. Handled in `UrlTools.NormalizeAddress`.
- `Evidence.Attributes` **is a list now**, do not reintroduce a map lookup. `Attribute(name)`
  returns the first match; use `AttributesNamed(name)` when a signal can repeat.
- Bare-URL extraction must run over the **plain-text parts only**, running it over HTML-derived
  text re-reports every anchor under a second label.
- HTML/text disagreement is only comparable when **both** a real plain part and a real HTML part
  exist; otherwise the comparison is the HTML against itself.
- Hidden-styled elements are **cut out** of the visible HTML before text extraction, not merely
  collected alongside it. Leaving them in hands the classifier the invisible instructions.

## Mutation-testing pass (2026-09-22), my safety-critical tests DO have teeth
Ran in response to `overview-`'s "a test that cannot fail is not a test" advisory. Harness at
`/tmp/mime-mutation/run.sh` (copy-tree, mutate, run-filtered-test, restore; treats any build
failure as an invalid mutation). Eight mutations, all now caught:

| Mutation | Result |
| --- | --- |
| preflight nesting-depth check disabled | RED |
| hidden elements no longer elided from visible text | RED |
| IDN normalisation removed from address comparison | RED |
| coverage reasons collapsed to the last (Core regression) | RED |
| message-size ceiling disabled | RED |
| adapter gains a compiling networking dependency | RED |
| rejected message reported as `Parsed` | RED |
| preflight part-count gate removed | **GREEN, see below** |

The green one was informative. Part count is enforced twice (preflight refusal, then the
`PartCollector` walk), and both paths reported the **same** `Reason = "part-count"`, so the test
could only prove "rejected", never "rejected *before* the expensive parse", which is the actual
DoS protection. Fixed by suffixing the walk-breach reason with `-after-parse`; the test now asserts
the bare reason and goes red when the early gate is removed. Re-run after the fix: RED.

Two harness lessons, both worth repeating for anyone doing this:
- **A build failure is not a passing mutation.** My first harness only looked for `error CS` and
  silently scored a `CA1823` build failure as "no teeth". In this repo analyzers are errors, so any
  mutation must be *used* (a method, not an unused field) to compile.
- **Always confirm the restore.** Diff the tree after the run; a half-restored source is worse than
  no mutation testing at all.

### Round 2, hunting the pattern in my own lane (found 2 more)
After `overview-` generalised the finding ("if two code paths can produce the same observable
outcome and the test claims one specifically, make them distinguishable"), I re-ran the hunt over
every test whose *name* claims a specific path. Two more had no teeth:

| Claim in the test name | Why it stayed green | Fix |
| --- | --- | --- |
| `BytesThatAreNotAMessage_AreRejectedAsMalformed` | "Malformed" is produced by **two** mechanisms: our header-block validation, and MimeKit throwing on headers we let through. A mutation removing our validation left the test green, MimeKit happened to throw instead. | Assert `Rejection.Reason` per input (`no-header-body-separator` / `no-header-fields`), pinning the mechanism we control. |
| `AMessageWithOnlyOneRepresentation_IsNotApplicable` | Two guards produce NotApplicable: the token-count floor and the both-parts gate. The plain-only fixture is caught by the token floor, so the parts gate was untested. | Added the HTML-only fixture (`link-display-mismatch.eml`), where both sides have plenty of tokens and only the parts gate applies. |

Both verified RED under mutation after the fix. **Total: 13 mutations, 13 caught.**
The lesson generalises one step further than the advisory states: the second failure was not two
*code paths* sharing an outcome, but one gate being redundantly covered by another gate, a test
that passes because a *different* guard happens to catch the same input.

### Round 3, thread safety (sparked by the SQLite amendment)
The SQLite advisory's real point, *when things genuinely run in parallel they contend for shared
state, and the failure looks like a bug in the code under test*, points at a gap here even though
this lane has no database. `StyloMail.Host` will hold **one** analyzer instance and call it from
whatever thread a message arrives on, and nothing documented or tested that it is safe to share.

Verified the design is stateless: `BoundedMimeMessageAnalyzer` has **zero instance fields**, and all
shared data is `static readonly`, populated at construction and only read afterwards. Then made that
a guarantee rather than an observation:
- `TheAnalyzerHoldsNoMutableState_SoOneInstanceMayBeShared`, reflection tripwire on instance fields.
- `ConcurrentUseProducesExactlyTheSameEvidenceAsSequentialUse`, 256 analyses over 8 threads, each
  compared byte-for-byte against the sequential render.

Both mutation-verified: adding an instance field → RED; making the evidence list a shared static
scratch → RED. **Total across three rounds: 15 mutations, 15 caught.** 90/90 tests green.

If instance state is ever genuinely needed here, that first test is the thing that will stop it
silently shipping, do not delete it, make the deliberate choice instead.

### Round 5, the generalisation was one word too narrow
`overview-` restated my round-3 work as "an object the host shares across threads should carry no
**instance** state". That is narrower than the real risk here. The likeliest future mistake in a
*parser* is not an instance field, it is a **static** cache: a memoised regex, a reused decode
buffer, a lazily built lookup table. Shared by every thread in the process, and my instance-field
tripwire would not have caught it.

Added `NoTypeInTheAdapterHoldsMutableStaticState`: reflection over every non-compiler-generated type
in the assembly, asserting no non-readonly static fields. (Compiler-generated types excluded, Roslyn emits non-readonly statics for its own lambda caches.)

Mutation-verified: adding a `static Dictionary` host memo → RED. **Five rounds: 20 mutations, 20
caught.** 91/91 green.

**The precise statement for this codebase:** *every piece of shared state in the adapter is
`static readonly`, populated at construction and only read afterwards*, that is the guarantee, and
both tripwires exist to keep it true.

### Round 4, systematic specific-claim sweep + a tooling hazard found the hard way
Applied the advisory's heuristic properly (list every test name with a specific claim, ask what
else could produce that outcome) rather than ad hoc. Four more mutations, all caught:
number-normalisation removed, label-claim gate disabled, generic-type gate disabled, host
comparison forced to mismatch. **Four rounds: 19 mutations, 19 caught** (3 were toothless and were
fixed first). 90/90 green.

**THE TOOLING HAZARD, read this before trusting any grep in this repo.**
`grep` in the agent shell is a zsh *function* wrapping `ugrep` with `-I --ignore-files`. `-I` skips
files it considers binary, **silently and with no warning**. While auditing, a search for
`LabelMakesHostClaim` returned only the *use* and missed the *declaration*, because
`src/StyloMail.Mime/LinkExtractor.cs` contained **one raw NUL byte**, making the whole file binary
to grep.

Cause: an earlier edit of mine wrote a literal NUL into the link dedup key
(`string.Concat(label, "\0", href)`) instead of an escaped one. Functionally harmless, but it
silently broke verification tooling, including my own residue checks, which is exactly the trap the
advisory warns about.

Fixed by naming the separator (`const string KeySeparator = "\u0000";`) so the source is plain
ASCII and the value is still collision-proof. Repo-wide scan for NUL bytes in source: **clean**
(I was the only offender, now fixed).

**Rules for a fresh me:**
- Use `/usr/bin/grep` (or python) for any search whose *absence* you are going to rely on.
- Re-verify restores against a clean rebuild, not just a grep: `dotnet clean` then full test run.
- Grep with `--ignore-files` also silently skips anything matching ignore rules, build artefacts,
  and `*.eml` outside fixtures.

### Trap 4 (stale binary), checked, did NOT affect any round; full re-run confirms 20/20
`queue-` found that `shutil.copy2` preserves mtime, so a restored source can be *older* than the
binary built from the mutation, MSBuild skips the rebuild, and the next run silently tests the
**mutated binary**, a false "caught", which is worse than a miss.

**Verdict for this lane: not affected, and now proven rather than reasoned.** My harness restores
with `rm -rf` + plain `cp -R` (no `-p`), which assigns a *fresh* mtime. Controlled experiment:
after restore, source mtime 1790052876 vs dll 1790052875, source newer, so MSBuild rebuilt every
time. `copy2`/`cp -p` is the trigger and I used neither.

Hardened anyway, and re-ran the whole sweep with all four traps guarded:
- any build failure (incl. analyzer diagnostics) => INVALID, never a verdict;
- missing anchor or a no-op replacement => INVALID, never a false "NO TEETH";
- `touch` every restored file so mtime beats the last build;
- mandatory post-sweep green run.

**Result: CAUGHT=20, TOOTHLESS=0, INVALID=0.** Post-sweep `dotnet clean` + full suite: 91/91 green.
Residue markers: none. NUL bytes: none.

**My per-lane harness is RETIRED** (`/tmp/mime-mutation` deleted). The shared
`.styloagent/tools/mutate.py` is canonical. Use:

```
python3 .styloagent/tools/mutate.py mime          # full lane sweep
python3 .styloagent/tools/mutate.py mime R7       # selected mutations
```

My mutation set is at **`.styloagent/tools/mutations/mime.py`**, 19 entries, each naming the test
that *claims* the behaviour. Do not edit `queue.py`; `queue-` owns the harness.

**Harness bug FIXED (2026-09-22), verdicts are now trustworthy.** `queue-` moved the name
extraction to TRX parsing with a count/name cross-check that reports INCONCLUSIVE on disagreement,
and added multi-edit support. `R15` is restored, so every mutation in `mime.py` has a reproducible
entry, none rest on a recorded run.

**Current result: 20/20 CLAIMED across 5 consecutive full sweeps, no gaps.** Residue clean, no stray
`.bak`, 91/91 green from a clean rebuild, solution builds.

The superseded bug (kept for the record): Parameterised `[Theory]` failures are invisible to
the console name extraction: the `[FAIL]` line contains the parameter list (with spaces), so
`(\S+\.\S+)\s+\[FAIL\]` cannot match, `failed` comes back empty, and a correctly-caught mutation
is reported as **ELSEWHERE**, with a garbage catcher name like `eml")`. Affects `R10` and `R13`,
reproducibly. Verified fix: parse TRX (`--logger "trx;LogFileName=..."`,
`testName.split('(')[0].split('.')[-1]`). Until it lands, expect 17/19 CLAIMED, not a defect in the
tests.

`R15` (the shared-static-scratch mutation behind the concurrency-equivalence test) needs two edits
and cannot be expressed in the one-old/new-pair format; multi-edit support requested. Its teeth rest
on a recorded run, noted in the module docstring so it is not silently lost.

## Blocked / not mine
- `dotnet test StyloMail.slnx` exits 1 on **`tests/StyloMail.Host.Tests/TestSupport.cs:114`**,   `IServiceCollection` has no `RemoveAll` (missing
  `using Microsoft.Extensions.DependencyInjection.Extensions;`). Host's lane. Adaptive's earlier two
  failures are fixed (100 passing now). Do not patch another agent's lane.

## Hard rules for a fresh me
- Do not run `git add` or `git commit`.
- **Never read, print, or reference `jevkey.pvt`.** It holds an API key.
- `.gitignore` allows `*.eml` only under `tests/**/fixtures/**`.
- Contract friction goes to `overview-` via `send_message`; do not edit `StyloMail.Core/`.
