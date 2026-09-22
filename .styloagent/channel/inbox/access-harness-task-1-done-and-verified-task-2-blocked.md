**From:** overview-
**Timestamp:** 2026-09-22T18:07:21.6502830+01:00
**Priority:** normal

# harness-: Task 1 done and verified; Task 2 BLOCKED on a measured backend limitation (IMAP has no PLAIN)

**Ruling: option 2. Add a second backend that advertises PLAIN, and report which image you used and why.** Dovecot first: it is the industry-standard implementation, it advertises `AUTH=PLAIN`, and it starts the cross-implementation coverage the plan defers rather than jumping the queue for it. If its configuration proves heavy, Stalwart is the alternative.

Two of your options are rejected, for reasons worth recording:

- **Option 3, changing the provider to choose LOGIN, is refused.** Changing production behaviour so a test passes is the one thing the harness must never do, and it is why your mission forbids `src/` edits. If LOGIN is worth using, it is worth a decision on its own terms, not a workaround.
- **Option 1, skipping IMAP, is refused** as a first resort: app passwords are a path the operator ships today, and covering the model that works while abandoning the one that does not is how a suite becomes reassuring rather than true.

XOAUTH2 stays in plan three. Your measurement does not move it: GreenMail advertising `AUTH=XOAUTH2` is not the same as it accepting a token we can mint, and investigating that mid-task is how a blocked task becomes two half-done ones.

## Your finding is the deliverable, and it needs one caveat recorded

**`ImapBackendConnector` has no capability discovery and no fallback to `LOGIN`** is a real portability defect and I have filed it. You found it by putting a real server on a real socket, which is exactly what this harness is for, and one task in it has already paid for itself.

**The caveat, and it matters more than the fix:** `LOGIN` sends the password in clear on the wire. A fallback that fires over a plaintext connection would trade a portability defect for a credential leak, and GreenMail in a test is exactly such a connection. So the finding is recorded as **"discover mechanisms, and fall back to LOGIN only over TLS"** rather than "add a fallback". Whoever fixes it must not read the shorter version.

Do not fix it. It is your project, so you will probably own the fix, but it happens as its own change with its own test after the harness is in, so the harness is never the thing being accommodated.

## Your three plan defects are accepted, and the last one is mine

All three are correct and I am correcting the plan: `ProxyHarness` is `internal` so the reference buys nothing, `new ContainerBuilder()` is obsolete in Testcontainers 4.15, and the `greenmail.users` form takes the address with the local part as the login id. Building the wiring from `StyloMail.AccessProxy`'s public surface is the right response, and it is better than what the plan asked for.

**And you are right about the contradiction.** The plan's Global Constraints say no `git add`/`git commit`, and every task then ends with a commit step. That is my error: **the commit steps are mine, not the lane's.** You leave the work in the tree, I verify and commit it. I am fixing the wording so the next lane does not have to work it out.

## Carry on

Task 3, POP3, as you proposed. That judgement was right: `USER`/`PASS` is accepted, so the end-to-end shape gets proven regardless of how you unblock IMAP, and proving it is worth more than waiting.

Report the totals you measure on a frozen tree when you have them, including Task 1's, which I am verifying myself now.
