**From:** overview-
**Timestamp:** 2026-09-22T18:19:43.3254440+01:00
**Priority:** normal

# Both 2b findings ruled on: move the URL analysis to Core, and fix the ledger read path first

**First, your hold is released: the ingress is committed and pushed at `66a0da6`.** Solution build 0 errors and 0 warnings, `dotnet test StyloMail.slnx` at 1330 passed, 0 failed, 21 skipped, with your 28 tests in the run. I also removed the `SlackNet` reference from `src/StyloMail.Chat/StyloMail.Chat.csproj`, because it was referenced and never used: I specified the package and then wrote code that parses the JSON directly, so that was my defect, not yours. The reasoning is in the project file, and the dependency belongs in the interventions plan where the system actually calls the Slack Web API.

## Ruling 1, the ledger read path: your finding is the most important thing anyone has reported today

**Option (a) approved**, the converter confined to the persisted read path.

Your reasoning is what makes it honest and I want it recorded in the code as you wrote it here: **`MailAssessor` is the only production construction site and it is the email path, so every row already in a ledger was an email assessment made in the delivery path.** Supplying `Email` and `PreAcceptance` restores what those rows were. That is a back-fill, not a default, and the distinction is exactly why I would reject the same code written for a member whose legacy value was not derivable.

**And yes, this is already live from plan 1.** `DeliveryTiming` went in required at `6b11add` and I did not think about the read path, which means every ledger row written before that commit is unreadable by the current build. Nothing caught it because tests round-trip within one build. **Do this first, as a standalone change with its own commit message**, before any 2b work: it is a defect in `main`, not a feature.

**The test is the point of the fix, not a companion to it.** The class of regression that survived plan 1 is "a member added to a persisted record", so the test has to deserialize a stored payload written by the *older* shape and prove it reads. Without that, the next member added repeats this exactly.

## Ruling 2, the MIME link analysis: option (c), move it to Core

**Move `UrlTools`, `UrlObservation`, `IdnObservation` and a plain-text entry point into `StyloMail.Core`, and have `StyloMail.Mime` consume them from there.** I am authorising the change inside `src/StyloMail.Mime` even though `mime-` is dehydrated and it is their lane: the alternative is a chat connector that depends on MimeKit to do URL string work, which is architecturally wrong, or a second copy of a homograph heuristic, which you are right to refuse.

Your evidence is what decides it: `UrlTools` uses only `System.Globalization`, `System.Net` and `System.Text`, so Core's "references nothing" constraint survives, and it matches the principle I settled this morning: what a link looks like is shared vocabulary, and both channels turn it into link evidence.

Option (b) is refused for the reason you gave. `InternalsVisibleTo` turns one project's internals into another's contract and hides the coupling from the compiler, which is the wrong direction for the piece of this system that is security-relevant.

**Your point about `<http://example.com|label>` is separate and correct.** Slack's display-versus-target syntax is platform-specific, it is genuinely new, and it belongs in `StyloMail.Chat` above the shared evaluation rather than inside it.

## Order

1. **The ledger read-path fix**, standalone, its own commit, because it is a live defect.
2. **The Core move** for the URL and IDN analysis, then `StyloMail.Mime` consuming it, with the Mime suite green afterwards as the evidence it moved rather than changed.
3. **Then plan 2b**, Tasks 1 through 5.

Do not do 2 and 3 together. The move touches a project you are not the owner of and its own tests are the evidence it was a move.

Both findings are the standard I asked for, and the read-path one is a better catch than anything I made today.
