**From:** access-
**Timestamp:** 2026-09-22T18:06:57.7150050+01:00
**Priority:** normal

# harness-: Task 1 done and verified; Task 2 BLOCKED on a measured backend limitation (IMAP has no PLAIN)

`access-` holding `harness-`. **Task 1 is complete and verified in both directions. Task 2 is blocked, and the blocker is a measurement, not a guess. I need a decision from you.**

## Task 1: done

`tests/StyloMail.Integration.Tests/` created, added to `StyloMail.slnx`, Testcontainers and MailKit referenced.

- **WITH `STYLOMAIL_HARNESS=1`:** 1 passed (a real MailKit client logs into GreenMail and opens INBOX).
- **WITHOUT:** 1 skipped, 0 failed.

Packages `dotnet add` resolved: **Testcontainers 4.15.0**, **MailKit 4.18.0**.

## Task 2: blocked. IMAP auth on GreenMail has no PLAIN, measured three ways

Our app-password provider uses **SASL PLAIN** for IMAP. GreenMail 2.1.14 advertises, over both plaintext and implicit TLS:

```
* CAPABILITY IMAP4rev1 LITERAL+ UIDPLUS SORT IDLE MOVE SASL-IR AUTH=XOAUTH2 QUOTA
```

No `AUTH=PLAIN`, no `AUTH=LOGIN`. Direct probes against the container:

| probe | result |
|---|---|
| `LOGIN alice@example.com <pw>` | `a1 OK LOGIN completed.` |
| `AUTHENTICATE PLAIN <b64>` (challenge form) | `a1 NO AUTHENTICATE failed. Unsupported authentication mechanism 'PLAIN'` |
| `AUTHENTICATE PLAIN <b64>` (what our connector sends) | `a1 BAD Expected end-of-line, found more character(s)` |

The full test reproduces exactly that: client authenticates to the proxy fine, proxy sends `AUTHENTICATE PLAIN <b64>`, GreenMail rejects, our connector fails closed, and MailKit reports `ImapProtocolException: The IMAP server has unexpectedly disconnected` at `AuthenticateAsync`.

**Our connector behaved correctly.** The backend refused and it failed closed, which is spec §9.5 risk 4 working as designed. This is not a proxy defect in the flow.

## The reportable finding that falls out of it

GreenMail advertises `SASL-IR` and then rejects the inline initial response. But the finding that matters is about **our** code:

**`ImapBackendConnector` has no mechanism discovery and no fallback.** It never sends `CAPABILITY` to learn what the backend offers, and if `AUTHENTICATE <mechanism>` is unsupported it does not fall back to the `LOGIN` command, which the backend does support and which would have worked here. The mechanism is chosen entirely by the credential provider, above the seam.

Production Gmail advertises `AUTH=PLAIN`, so this is not a live Gmail problem. It is a **portability** limit: any backend that does not offer PLAIN gets a client-visible disconnect on every session, with no path to the `LOGIN` command that would have succeeded. Reporting it, not fixing it, per the mission.

## What I need decided

To exercise IMAP through the proxy I need a backend our PLAIN path can actually authenticate to. Options as I see them:

1. **Accept the app-password IMAP path as untestable here** and cover IMAP via the OAuth/XOAUTH2 path. GreenMail does advertise XOAUTH2, but the plan explicitly defers XOAUTH2 to plan three, and GreenMail needs token configuration I have not investigated.
2. **Let me add a second backend image** that advertises PLAIN (Dovecot or Stalwart). The plan defers a cross-implementation matrix, so this is a scope call for you.
3. **Change the harness fixture only** to make the app-password provider choose `LOGIN` for IMAP. That would need a `src/` change, which my lane forbids, so I am not doing it unasked.

**My recommendation: option 2**, because it tests the path the operator actually ships today (app passwords) and the second image also starts the cross-implementation coverage the plan says is worth having.

## Meanwhile

**Task 3 (POP3) should not be blocked**: the app-password provider uses the `USER`/`PASS` pair for POP3, and GreenMail accepts those. I am proceeding with it while this decision is open, so the end-to-end shape gets proven either way.

## Three defects in the plan itself, for the record

1. **The reuse mechanism does not work.** The plan references `StyloMail.AccessProxy.Tests` "to reuse `ProxyHarness`". `ProxyHarness` is `internal` and that project grants no `InternalsVisibleTo`, so the reference resolves nothing (`CS0122`). I built the equivalent wiring in the new project from `StyloMail.AccessProxy`'s public surface instead, so the reference is currently unnecessary rather than relied upon.
2. **The plan's `GreenMailServer` does not compile.** `Host` as an instance property trips `CA1822`, and analyzers are errors. Also `new ContainerBuilder()` is obsolete in Testcontainers 4.15 (`CS0618`). Both fixed in-project.
3. **The plan's GreenMail user form is wrong.** `-Dgreenmail.users=alice:pwd@example.com` makes the login id the **local part**: `alice` authenticates, `alice@example.com` does not, and the plan's `Login` constant is the full address. Fixed to `-Dgreenmail.users=<address>:<password>` and verified.

I have not committed anything, per the constraints. Note the plan's Global Constraints forbid `git add`/`git commit` while each task ends with a commit step; I followed the constraints.
