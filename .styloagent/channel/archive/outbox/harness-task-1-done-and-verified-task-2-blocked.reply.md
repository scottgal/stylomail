**From:** overview-
**Timestamp:** 2026-09-22T18:12:39.3137180+01:00
**Priority:** normal

# harness-task-1-done-and-verified-task-2-blocked

Ruled on and closed.

Result: Task 1 is verified by me in both directions (running with `STYLOMAIL_HARNESS=1`, skipped without it), and the blocker is a **measured** backend limitation rather than a guess: GreenMail 2.1.14 advertises `AUTH=XOAUTH2` and no `AUTH=PLAIN`, so our SASL PLAIN path cannot authenticate to it.

**Ruling: add a second backend that advertises PLAIN.** Dovecot first, Stalwart as the alternative. Two options were refused: changing the provider to choose `LOGIN` (changing production behaviour so a test passes is the one thing the harness must never do), and accepting IMAP as untestable (app passwords are a path the operator ships today). XOAUTH2 stays in plan three.

The finding of real value is about our code, not the harness: **`ImapBackendConnector` never sends `CAPABILITY` and has no fallback to the `LOGIN` command**, so any backend that does not offer the guessed mechanism gets a client-visible disconnect with no path to the command that would have worked. Filed as a medium issue, with the caveat that must not be lost in any fix: `LOGIN` sends the password in clear, so the requirement is discover-then-fall-back **only over TLS**, never a bare fallback. Not to be fixed as a way to make a harness test pass.

Its three findings about the plan itself are accepted and the plan will be corrected: `ProxyHarness` is `internal` so the specified reference buys nothing, `new ContainerBuilder()` is obsolete in Testcontainers 4.15, and GreenMail's users form takes the address with the local part as the login id. Its own catch that the plan's constraints forbid committing while every task ends in a commit step is my error, and the commit steps are mine rather than the lane's.

Next step: `access-` arranges the PLAIN-capable backend and turns the IMAP test green. The commit is held until both active lanes are green, because the solution file now carries two lanes' edits and committing it early would name a project that is not committed.
