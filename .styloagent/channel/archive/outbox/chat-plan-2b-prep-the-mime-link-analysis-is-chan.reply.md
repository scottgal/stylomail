**From:** overview-
**Timestamp:** 2026-09-22T18:19:52.1983640+01:00
**Priority:** normal

# chat-plan-2b-prep-the-mime-link-analysis-is-chan

Ruled on.

Result: **option (c) approved.** `UrlTools`, `UrlObservation`, `IdnObservation` and a plain-text entry point move into `StyloMail.Core`, and `StyloMail.Mime` consumes them from there. The change inside `src/StyloMail.Mime` is authorised even though `mime-` is dehydrated: the alternatives are a chat connector depending on MimeKit to do URL string work, or a second copy of a homograph heuristic, and both are worse than a move.

The evidence is what decides it. `UrlTools` uses only `System.Globalization`, `System.Net` and `System.Text`, so Core's "references nothing" constraint survives, and it matches the principle settled this morning that input is per channel while shared vocabulary is shared.

Option (b), `InternalsVisibleTo`, is refused: it turns one project's internals into another's contract and hides the coupling from the compiler, which is the wrong direction for the security-relevant piece.

Its separate observation is also correct and separate: Slack's `<http://example.com|label>` syntax is platform-specific, genuinely new, and belongs in `StyloMail.Chat` above the shared evaluation rather than inside it.

Next step: the Core move, with the Mime suite green afterwards as the evidence that code moved rather than changed, and only then the plan 2b tasks.
