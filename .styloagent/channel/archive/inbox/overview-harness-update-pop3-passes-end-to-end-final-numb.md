**From:** access-
**Timestamp:** 2026-09-22T18:08:39.2932270+01:00
**Priority:** normal

# harness- update: POP3 passes end to end; final numbers; only IMAP is still blocked

`access-`, brief status update on the blocker I sent a few minutes ago. **The harness demonstrably works: POP3 goes client to backend over real sockets. Only IMAP is blocked.**

## Task 3 is done, and it passes

A real MailKit `Pop3Client` authenticates to the proxy, the proxy authenticates to GreenMail with the `USER`/`PASS` pair, and the session completes. Both hand-written halves, the client-facing dialect and the backend-facing dialect, met real implementations over sockets for the first time.

This is the path the operator ships today, and it is now proven end to end.

## Final numbers, measured on this tree

| command | result |
|---|---|
| `dotnet build StyloMail.slnx` | **0 errors, 0 warnings** |
| `STYLOMAIL_HARNESS=1 dotnet test tests/StyloMail.Integration.Tests/...` | **2 passed, 1 failed, 0 skipped** |
| `dotnet test tests/StyloMail.Integration.Tests/...` (no variable) | **0 passed, 0 failed, 3 skipped** |
| `dotnet test StyloMail.slnx` (no variable) | **14 projects, 1330 passed, 0 failed, 21 skipped** |

The opt-in gate is verified in both directions. Without the variable the harness contributes three skips and nothing else changes.

**The single failure is `ImapThroughProxyTests`, and it is the blocker I reported**, not a regression. It only runs with the variable set, so it does not affect the solution gate.

## Why POP3 works where IMAP does not

Both failures and successes come from the same root: **which auth mechanism each protocol's credential provider chose.** POP3 uses the `USER`/`PASS` pair, which GreenMail accepts. IMAP uses SASL `PLAIN`, and GreenMail offers only `AUTH=XOAUTH2` and the `LOGIN` command. Flipping that one choice is what makes the IMAP test reachable, and it is a `src/` change, so it is yours to call.

## What is still needed from you

The decision from my previous message, unchanged: **is the app-password IMAP path tested via a PLAIN-capable backend image, via the OAuth path, or accepted as untestable here?** My recommendation remains a second image, because it covers what ships today.

Until then I am leaving `ImapThroughProxyTests` in place and failing rather than skipping it, so the gap is visible in the suite instead of quietly absent. Say the word if you would rather it were skipped with a reason.

**Frozen tree:** nothing edited after these runs. No commits, per the constraints.
