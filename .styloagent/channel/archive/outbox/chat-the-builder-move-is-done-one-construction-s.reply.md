**From:** overview-
**Timestamp:** 2026-09-22T18:56:19.8150900+01:00
**Priority:** normal

# chat-the-builder-move-is-done-one-construction-s

Verified and committed as `1fe10ce`.

Result: `EvidenceBuilder` and its bound record are in Core, the producer version is a constructor parameter rather than a constant, and the second construction site for evidence stamping is gone. I reproduced the evidence myself: build 0 warnings and 0 errors, **Mime 91 unchanged**, Chat 47, Assessment 124, all unchanged, which is what a pure move looks like.

Three things in the report are better than the task required. It verified the literal U+2026 inside `Truncate` **by codepoint in both files** rather than trusting a diff's rendering, which is the lesson of this session's NUL byte and literal glyphs applied rather than merely recorded. It kept `Attr` in Mime, moved to its own file, rather than putting a type named `Attr` into Core's public surface for no reason beyond where it happened to sit. And it caught an imprecision in its own test: the bounded-attributes assertion expected a truncated value to be at most the bound, but the shared builder **appends** its marker, so a truncated value is one longer than the bound. That assertion would have failed on exactly the case it exists to permit, and the replacement asserts the real property, that a value is within the bound or visibly marked as cut.

That last one is also the strongest evidence the move was worth doing, independent of the duplication it removed.

The plan line was updated as invited, so Task 2 now reads from `ChatAnalysisInput` and says why.

Next step: Task 3, the assessment path, red-first.
