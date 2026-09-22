**From:** chat-
**Timestamp:** 2026-09-22T18:55:42.1720970+01:00
**Priority:** normal

# chat- the builder move is done: one construction site for evidence stamping, Mime 91 unchanged

The builder move is done, its own change, frozen, separate from Task 3 as you asked.

## What moved

- **new** `src/StyloMail.Core/EvidenceBuilder.cs`: `EvidenceBuilder` plus a small `EvidenceAttributeLimits` bound record. The producer version is now a **constructor parameter** rather than a constant, which is what lets each channel stamp its own rules while sharing the stamping.
- **new** `src/StyloMail.Mime/Attr.cs`: `Attr` moved to its own file, unchanged. It is a one-line record constructor carrying no convention, so I left it in Mime rather than putting a type named `Attr` in Core's public surface.
- **deleted** `src/StyloMail.Mime/EvidenceBuilder.cs`.
- **modified** `src/StyloMail.Mime/MimeParseLimits.cs` (an `AttributeBudget` property mapping the two parser limits onto the bound) and `BoundedMimeMessageAnalyzer.cs` (the one construction site).
- **modified** `src/StyloMail.Chat/ChatEvidenceProducer.cs`: it now stamps through the shared builder, so **the second construction site is gone**, which was the point.

## The evidence

**`StyloMail.Mime.Tests` is 91, unchanged**, and Chat 47 and Assessment 124 are unchanged too.

**I diffed the moved bodies against the original before deleting it, and the rule earned its keep again.** The only differences are the two intended ones: `MimeSignals.SourceVersion` becoming the constructor parameter, and the three `limits.MaxAttributeEntries` / `MaxAttributeValueLength` references becoming `MaxEntries` / `MaxValueLength`. The capping loop and the attribute-keeping semantics are byte-identical.

**The character I was most at risk of corrupting: `Truncate` ends with a literal `…`.** Given that typing an escape sequence into an edit has already silently produced a NUL byte and a set of literal glyphs in this session, a non-ASCII character inside the very function being moved was the obvious place for the same class of bug. I checked it explicitly rather than trusting the diff's rendering: **`0x2026` in both files**, and `Truncate` came out IDENTICAL.

## One imprecision in my own test, caught by the move

My bounded-attributes test asserted a value length of at most the bound. The shared builder **appends** the marker rather than replacing a character, so a truncated value is one longer than the bound, and that assertion would have failed on exactly the case it exists to allow. It now asserts the real property: a value is either within the bound or visibly marked as cut.

## And the plan line

Updated `docs/chat-pipeline-design.md` as you invited, so Task 2 reads from `ChatAnalysisInput` and says why.

## Measured on the frozen tree

Build 0 warnings, 0 errors. Whole solution **1388 passed, 0 failed, 23 skipped**, identical to `db4a0e8` because a pure refactor adds no tests. Last source edit predates the artifacts, sweep signals clean, no em-dashes in anything I added.

Next is Task 3, the chat assessment path, **red-first**.
