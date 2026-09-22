**From:** chat-
**Timestamp:** 2026-09-22T18:30:30.6948690+01:00
**Priority:** normal

# chat- item 2 done: the URL and IDN analysis is in Core, Mime consumes it, suite green as the evidence it moved

Item 2 is done: the Core move, frozen, uncommitted. Stating the transient red as you asked: the tree was red for the few minutes between `UrlTools` leaving `StyloMail.Mime` and its callers being repointed, and it is green and frozen now.

## What moved

- **new** `src/StyloMail.Core/UrlTools.cs`: `UrlTools`, `UrlObservation`, `IdnObservation`, moved verbatim and made `public`.
- **new** `src/StyloMail.Core/LinkAnalysis.cs`: `LinkFinding` plus the extraction logic, and the plain-text entry point you asked for, `FromPlainText(text, maxLinks)`.
- **modified** `src/StyloMail.Mime/LinkExtractor.cs`: now a thin adapter that hands `HtmlAnalysis.Links` and the plain body to `LinkAnalysis.Extract`.
- **deleted** `src/StyloMail.Mime/UrlTools.cs`.
- **new** `tests/StyloMail.Core.Tests/LinkAnalysisTests.cs`: 11 tests.

I kept an adapter in Mime rather than calling Core from the analyzer directly, so the knowledge that "an HTML part carries anchors, a body carries bare URLs" stays in the project that owns HTML parsing.

## The evidence it moved rather than changed

**`StyloMail.Mime.Tests` is 91 passed, 0 failed, identical to before the move.** I also diffed the moved text against the original rather than trusting myself: the `ScriptOf` ranges, the four `[GeneratedRegex]` declarations and the whole `Compare` method (including `TrimWww`) are byte-identical between `Mime/UrlTools.cs` and `Core/UrlTools.cs` apart from the namespace and the three `internal` to `public` changes, and the regex block and `Compare` are byte-identical between `Mime/LinkExtractor.cs` and `Core/LinkAnalysis.cs`.

**That diff caught two real defects I had just introduced**, which is why I am reporting the method as much as the result:
1. I transcribed the `ScriptOf` script ranges from hex escapes into literal glyphs. The file's own comment says the table is written as escapes precisely so it can be diffed without depending on a font, and a single mistyped codepoint would have silently narrowed homograph detection.
2. I wrote `KeySeparator` as a raw NUL byte rather than the escape text, which is invisible in the source and would have made that line unreviewable. The comment above it even explains why the separator is NUL rather than a space.

Both are now the original escape forms, and I scanned both new files for control characters: none.

**One test expectation I got wrong, against the code.** I had asserted that a bare URL in plain text "claims nothing". It does claim something: the URL is its own label and that label names a host, so `LabelMakesHostClaim` is true and the label belongs in the denominator. What it cannot do is disagree with itself. I fixed the test rather than the code, and the Mime suite being green is what told me the code was the unchanged side.

## Measured on the frozen tree

`dotnet build StyloMail.slnx`: 0 warnings, 0 errors.
`dotnet test StyloMail.slnx`: **1362 passed, 0 failed, 23 skipped.**

Core went 20 to 31, exactly my eleven. **Mime is 91, unchanged**, which is the evidence for the move. Host 303 and Chat 28 are unchanged. AccessProxy, Desktop and Integration moved (61 to 64, 182 to 197, 3 to 5 skipped) because `access-` and `desktop-` are adding tests concurrently, not because of me.

## What this gives 2b

`LinkAnalysis.FromPlainText(text, maxLinks)` is the entry point for a channel with no markup. Slack's `<http://example.com|label>` is the case that does not use it: that carries a display text separate from its target, so the chat reader parses the markup and passes both through `LinkAnalysis.Extract`, which is the same `Compare` the HTML anchors go through. That parsing is genuinely new and stays in `StyloMail.Chat`, as you ruled.

I have not folded `DecisionResponse` into this change, since you placed it in 2b Task 1 alongside `Channel`.

Next: starting plan 2b, Tasks 1 through 5, tests first, and I will keep 2 and 3 apart from each other. One thing worth flagging before I begin: there are now two link types in Core, `LinkObservation` (the transport shape on the analysis input) and `LinkFinding` (the analysis result). I will not merge them without asking, but you should know they exist side by side in case the contract review wants that reconciled.
