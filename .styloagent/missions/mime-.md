# `mime-`, MIME adapter and deterministic evidence

## Your scope
Own `src/StyloMail.Mime/` and `tests/StyloMail.Mime.Tests/`. Nothing else.
Do **not** edit `src/StyloMail.Core/`, `src/StyloMail.Persistence/`, `src/StyloMail.Jev/`,
`.styloagent/spec.md`, or `email-proxy-spec.md`. Other agents own those. If you believe a Core
contract must change, `send_message` to `overview-` rather than editing it.

## Context you must read first
- `.styloagent/spec.md`, the project spec (§6 semantic dimensions, §4 constraints).
- `email-proxy-spec.md` (repo root), §6 "Deterministic and semantic evidence" is your detailed
  brief; §11 covers parser failure modes. This is the specification of record for mechanics.
- `src/StyloMail.Core/`, the contracts to implement against: `MailAnalysisInput`, `AnalysisCoverage`,
  `LinkObservation`, `AttachmentMetadata`, `Evidence`, `EvidenceOrigin`, `EvidenceAvailability`.

## What to build
A bounded MIME parser turning raw MIME bytes into a `MailAnalysisInput` plus `Evidence` records with
`Origin = EvidenceOrigin.Deterministic`.

Deterministic features required (source spec §6):
- envelope/header identity mismatches; display-name vs address mismatch
- Reply-To novelty
- trusted authentication results (only from trusted verifiers, see `AuthenticationContext`)
- actual link destinations vs visible labels; IDN/punycode representation; URL host novelty
- attachment extension/type mismatch; attachment hashes
- message and recipient counts; template similarity
- message size; HTML/text disagreement; padding/obfuscation indicators
- new correspondence relationships; thread-header consistency

## Hard constraints, safety requirements, not style preferences
1. **No network calls of any kind.** Do not fetch links, load remote images, execute attachments, or
   resolve redirects.
2. **Preserve original MIME bytes.** Your output is an *analysis view*; never mutate the original.
   Rewriting signed content invalidates DKIM.
3. **Enforce parser limits** (size, nesting, part count, header count). On exceeding them set
   `AnalysisCoverage.ParserLimitExceeded` and return an explicit unsupported/oversize disposition,    never analyse an arbitrary fragment as though it were a complete message.
4. **Reduced coverage is explicit, never silent.** Encrypted/password-protected content, unparseable
   parts and truncation must be recorded in `AnalysisCoverage`.
5. **Header `Message-ID` is untrusted**, see `MailEnvelope.UntrustedMessageIdHeader`. Never an
   idempotency key.
6. **Thread headers are claims, not proof** of a relationship. Record disagreement; do not resolve it
   in the sender's favour.

## Tests
xUnit. Fixture `.eml` files under `tests/StyloMail.Mime.Tests/fixtures/`. Cover at minimum:
display-name/address mismatch, display-vs-actual link mismatch, IDN homograph, extension/content-type
mismatch, HTML/text disagreement, oversize/limit-exceeded, encrypted part, and a plain benign message
producing no signals. Tests must not touch the network.

## Build notes (important)
- `dotnet` is NOT on PATH. Every shell needs:
  `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`
- SDK 10.0.201; TargetFramework `net10.0`. Solution file is `StyloMail.slnx` (.NET 10 format).
  Add your projects with `dotnet sln StyloMail.slnx add <path>`.
- Add a `ProjectReference` to `src/StyloMail.Core/StyloMail.Core.csproj`.
- `.gitignore` already covers `bin/`, `obj/` and `*.eml` outside `tests/**/fixtures/**`, fixtures are
  deliberately allowed. Do not run `git add` or `git commit`.
- **Never read, print, or reference `jevkey.pvt`.** It holds an API key.

## Done when
`dotnet test StyloMail.slnx` passes with your tests included, and you have run it and seen it green.
Then report to `overview-` via `send_message`: files created, test count, contract friction, and
anything you deliberately did not do.