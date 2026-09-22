# mime- — saved context

Checkpoint, 2026-09-22. Scope, current state, and the things a fresh me would otherwise have to
re-derive. Historical narrative (how each finding was reached) lives in the channel archive, not
here.

## Identity + scope
- Prefix: `mime-`, depth 1, parent `overview-`.
- Owns exactly two trees: **`src/StyloMail.Mime/`** and **`tests/StyloMail.Mime.Tests/`**. Nothing
  else.
- Do not edit `src/StyloMail.Core/`, `src/StyloMail.Persistence/`, `src/StyloMail.Jev/`,
  `.styloagent/spec.md`, or `email-proxy-spec.md`. Contract friction goes to `overview-` via
  `send_message`.
- Mission brief: `.styloagent/missions/mime-.md`. Specification of record: `email-proxy-spec.md` §6
  (deterministic evidence) and §11 (parser failure modes).

## Repo state + commands
- Root `/Users/scottgalloway/RiderProjects/stylomail/`, branch `main`, **no commits** (everything is
  untracked; the mission forbids `git add` / `git commit`).
- Solution `StyloMail.slnx` (.NET 10 format). SDK 10.0.201, `net10.0`.
- **Every shell needs:**
  `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`
- Last verified: **91/91 tests green**, clean build, zero warnings, **`dotnet build StyloMail.slnx`
  succeeds**.

## Deliverable — COMPLETE and green
- `src/StyloMail.Mime/` — 20 files, ~4.4k lines. One NuGet dependency: **MimeKit 4.18.0** (cached
  locally, restores offline; parser only, no I/O).
- `tests/StyloMail.Mime.Tests/` — 11 test files, 9 `.eml` fixtures, **91 tests**.
- Mutation set: `.styloagent/tools/mutations/mime.py`, **20 entries, 20/20 CLAIMED** over 5
  consecutive full sweeps of the shared harness.

## Blast radius — who depends on this project
Referenced by **`src/StyloMail.Assessment`** and **`src/StyloMail.Host`** (plus their test projects).
Changing the public surface breaks them.

**Fleet rule:** build *your own* project to stay unblocked, but **`dotnet build StyloMail.slnx`
before declaring done**. My project being green is not the claim "nothing I did broke anyone" — a
shared-project break is invisible from inside your own lane.

## Public API (8 types — a contract)
`IMimeMessageAnalyzer` · `BoundedMimeMessageAnalyzer` · `MimeAnalysisRequest` · `MimeAnalysisResult` ·
`MimeParseDisposition` (+ `MimeParseRejection`) · `MimeParseLimits` · `MimeSignals`.

```csharp
new BoundedMimeMessageAnalyzer().Analyze(MimeAnalysisRequest) → MimeAnalysisResult
```
Sync, no I/O, never throws for hostile input. `MimeAnalysisResult.Message` is **null** unless
`Disposition == Parsed`.

## Hard rules baked in (all test-enforced)
1. **No network, structurally.** No socket code path; a test asserts the assembly references no
   networking assemblies and names none in its compiled surface.
2. **Original bytes never mutated** — asserted over every fixture.
3. **Limits enforced before parsing**: size, part count, **nesting depth**, header count, header
   bytes, header line length. Depth is measured in the preflight scan; leaving it to the parser
   would silently truncate a nesting bomb and analyse the readable layer as if it were whole.
4. **Rejection ≠ partial analysis.** A rejected message returns `Message = null`, an explicit
   `MimeParseRejection`, and a coverage record. Never a fragment dressed as a whole message.
5. **Reduced coverage is always an explicit signal**, never an absence.
6. **Message-ID is untrusted** — carried as `MailEnvelope.UntrustedMessageIdHeader` only, never a key.
7. **Thread headers are claims** — internal consistency only, never resolved.
8. **A message cannot authenticate itself** — in-message `Authentication-Results` headers are
   ignored entirely; only `AuthenticationContext` from a trusted verifier is consumed.
9. **Thread-safe by construction** — zero instance fields, all shared state `static readonly`. Two
   reflection tripwires plus a concurrency-equivalence test enforce it. See "Thread safety" below.

## Signal catalogue
All `EvidenceOrigin.Deterministic`, `SourceVersion = "stylomail-mime/1"`, `Confidence` always null,
ids prefixed `deterministic.`:

`envelope_header_identity` · `display_name_address_mismatch` · `reply_to_divergence` ·
`trusted_authentication_failure` · `authentication_provenance` · `link_display_mismatch` ·
`link_idn` · `link_idn_homograph` · `link_host_profile` · `attachment_type_mismatch` ·
`attachment_hash` (one per attachment) · `attachment_unavailable` · `recipient_count` ·
`message_structure` · `template_fingerprint` · `message_size` · `html_text_disagreement` ·
`padding_obfuscation` · `thread_header_consistency` · `quoted_history` · `content_encrypted` ·
`analysis_coverage` · `html_markup_observation`.

## Deliberately NOT done
Novelty is a baseline comparison and cannot be computed from one message, so it is not fabricated:
- **URL host novelty** → emits `link_host_profile` with a stable `hostSetDigest` of the distinct
  host set. Comparing against a recipient's history is the adaptive engine's job.
- **New correspondence relationships / Reply-To novelty** → emits the local `reply_to_divergence`
  fact plus the envelope identity keys.
- **Template similarity** → emits `template_fingerprint` (simhash + skeleton digest); the
  cross-message Hamming comparison belongs to whoever holds the other messages.
- **DKIM/SPF verification** → would need the original connection context and DNS. Only
  trusted-verifier results from `AuthenticationContext` are consumed.
- **DKIM domain alignment** (does a trusted `dkim=pass` match the From domain?) — not implemented;
  needs an agreed Authentication-Results detail convention. Still unowned; worth raising.
- **HTML is scanned, not DOM-parsed** — bounded regexes, all `RegexOptions.NonBacktracking`.
- **Attached `message/rfc822` is hashed, not descended into** — bounds work per message.

## Core contracts — current shape
`Evidence.Attributes` is **`IReadOnlyList<EvidenceAttribute>?`** (was a dictionary; repeated names
are meaningful). Coverage reasons are **one `reduced` attribute per reason** — read with
`AttributesNamed("reduced")`, not `Attribute("reduced")` (that returns only the first). Regression
guard: `EveryReducedCoverageReasonSurvives_NotJustTheLastOne`.

Also: `UntrustedMessageIdHeader` no longer required on input · `AnalysisCoverage.OversizeRejected`
(oversize path only, so "too big" is distinguishable from "structurally hostile") ·
`AttachmentMetadata.SizeBytesIsComplete` (set from `!HashPartial`) · attribute construction uses
`Attr.Of(name, value)` via `using static StyloMail.Mime.Attr;` because `EvidenceAttribute` uses
required init properties (no target-typed `new(...)`).

**OPEN:** Core ships no `EvidenceAttribute.Of(...)` factory, so each lane invents its own shorthand.
Raised with `overview-`; `adaptive-` was hitting the same thing. Awaiting a fleet-wide decision.

## Verification — mutation testing
Shared harness: **`python3 .styloagent/tools/mutate.py mime`** (my per-lane harness is retired). My
set is `.styloagent/tools/mutations/mime.py` — **do not edit `queue.py`**.

**Sweeps now run in an isolated copy of the tree** (2026-07-22, from my copy-tree precedent), so it
is safe to run one while other lanes — or I — are working. Two consequences: a sweep no longer
leaves the real tree's build output warm for a manual run, and `obj`/`bin` are deliberately excluded
from the copy so a stale binary cannot ride across and defeat the mtime guard. Verified empirically:
real-tree hash identical before and after a full sweep, 20/20 CLAIMED.

**Current: 20 mutations, 20/20 CLAIMED, no gaps** across 5 consecutive sweeps; post-sweep green.

Each mutation names the test whose *name* claims the behaviour, which is what makes `CLAIMED` a
stronger statement than "something went red". **When adding a mutation, always name the claiming
test.**

Three toothless tests were found and fixed this way — worth knowing the shapes, because they recur:
- **Two code paths, one outcome.** `BytesThatAreNotAMessage` stayed green when our validation was
  removed, because MimeKit happened to throw instead. Fixed by asserting `Rejection.Reason`.
- **One guard redundantly covered by another.** `AMessageWithOnlyOneRepresentation` was green because
  a *token-count floor* excluded the plain-only fixture, leaving the both-parts gate untested. Fixed
  by adding an HTML-only fixture.
- **Two enforcement layers, identical reason strings.** Part count is enforced in both the preflight
  and the tree walk, both reporting `Reason = "part-count"`, so the test could never prove "refused
  *before* the expensive parse". Fixed by suffixing the walk breach with `-after-parse`.

**The generalisation behind all three — now a named section in the harness header.** `queue-`
observed the same defect at three levels: test assertions (a "clamped to 200" claim tested with 5
items), verdict extraction (the theory regex), and the harness's own restore check (comparing a file
against its already-mutated self). Each produced **confidence instead of a signal**.

> **Ask what input makes a guard fail. If you cannot name one, it is decoration.**

Apply that to any new assertion, guard or check — it is faster than mutating and catches the same
class. Note it also implies a coverage trap: *a claim about a ceiling cannot be tested below the
ceiling.*

**Cheapest way to find these: list every test name containing a specific claim
(`IsNotApplicable`, `BeforeParsing`, `IsMarkedTruncated`, `IsUnavailable`) and ask "what else could
produce this same outcome?" — before mutating.**

## Thread safety
`BoundedMimeMessageAnalyzer` has **zero instance fields**; all shared state is `static readonly`,
populated at construction and only read afterwards. `StyloMail.Host` shares one instance across
threads, so this is a guarantee, not an observation:
- `TheAnalyzerHoldsNoMutableState_SoOneInstanceMayBeShared` — instance-field tripwire.
- `NoTypeInTheAdapterHoldsMutableStaticState` — catches the more likely future mistake, a **static**
  cache (memoised regex, reused buffer), which an instance-field tripwire would miss.
- `ConcurrentUseProducesExactlyTheSameEvidenceAsSequentialUse` — 256 analyses over 8 threads compared
  byte-for-byte against sequential.

Mutation-verified (`R14`, `R15`, `R20`). If instance state is ever genuinely needed, the tripwire
will stop it silently shipping — make the deliberate choice, do not delete the test.

## Gotchas
- **`grep` in the agent shell is a zsh function wrapping `ugrep` with `-I --ignore-files`.** `-I`
  silently skips files it considers binary, and `--ignore-files` skips anything matching ignore
  rules. **Use `/usr/bin/grep` (or python) for any search whose *absence* you are going to rely on.**
  This bit me: a raw NUL byte in `LinkExtractor.cs` made the whole file invisible to grep and
  silently corrupted my own residue checks. Fixed by escaping it
  (`const string KeySeparator = "\u0000";`); repo-wide source scan is clean.
- `RegexOptions.NonBacktracking` **rejects bounded repeats** — `[^>]{0,4096}` blew the 10 000-node
  automaton limit. Use `*`/`+`, and beware `{0,61}` inside loops.
- MimeKit's `CharsetUtils` is `internal`. Use `TextPart.Text` (bounded by the message size limit).
- `MailboxAddress.Address` returns the **Unicode** form of an IDN while SMTP envelopes carry
  punycode. Compare after `IdnMapping.GetAscii` or every IDN sender looks like an impersonation
  attempt — handled in `UrlTools.NormalizeAddress`.
- Bare-URL extraction must run over the **plain-text parts only**; over HTML-derived text it
  re-reports every anchor under a second label.
- HTML/text disagreement is only comparable when **both** a real plain part and a real HTML part
  exist, or the comparison is the HTML against itself.
- Hidden-styled elements are **cut out** of the visible HTML before text extraction, not merely
  collected alongside it.

## Bus + fleet
- **`reply_to_thread` archives a thread but does NOT deliver it.** Use `send_message` when a peer is
  waiting on you; `reply_to_thread` only to close a thread whose recipient will see it anyway. Two
  of my replies were stranded this way, and two of `overview-`'s to me — including two independent
  verifications of my work that I never saw. **Treat silence as non-delivery, not as agreement.**
- `inbox/` is a **shared** directory; only `mime-*` and `all-*` files are surfaced to me. Files
  prefixed with another lane's prefix are not mine — do not act on them.
- Four mutation-harness traps, all guarded in the shared harness: build failure ≠ passing mutation
  (analyzers are errors here, so a mutation must be *used* to compile) · verify the restore by
  content · missing anchor/no-op ⇒ INVALID, never a verdict · stale binary (mtime) — the harness
  refuses to run if source is older than the build output, and runs a mandatory post-sweep green run.
- Build the solution, not just your project (see Blast radius).

## Blocked / not mine
Nothing blocked. `dotnet build StyloMail.slnx` currently **succeeds**. Earlier reds belonged to other
lanes (`StyloMail.Adaptive.Tests` / `StyloMail.Assessment` on a `SendingQuotaLedger` signature
change; `StyloMail.Host.Tests` on a missing `using`) — all resolved by their owners. Do not patch
another agent's lane.

## Hard rules for a fresh me
- Do not run `git add` or `git commit`.
- **Never read, print, or reference `jevkey.pvt`.** It holds an API key.
- `.gitignore` allows `*.eml` only under `tests/**/fixtures/**`.
- Do not edit `StyloMail.Core/`; raise friction with `overview-` via `send_message`.
