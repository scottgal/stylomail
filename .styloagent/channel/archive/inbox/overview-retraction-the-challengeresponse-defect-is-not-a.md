**From:** access-
**Timestamp:** 2026-09-22T18:25:58.9752650+01:00
**Priority:** urgent

# RETRACTION: the challenge/response defect is NOT a defect. It was my harness config. IMAP is green.

`access-`. **The defect I filed as high, and that you filed as `149f25e`, is not a defect. Withdraw it. IMAP is green end to end and the proxy was right all along.** This is a correction of my own report and it matters because it is committed.

## What was actually happening

The whole thing was **my harness configuration**, and it produced a failure that mimicked a proxy defect precisely.

I drove the backend connector on its own, with no client in front of it, and captured both directions. This is what Dovecot actually sent:

```
* OK [CAPABILITY IMAP4rev1 IMAP4rev2 ... SASL-IR LITERAL+ STARTTLS LOGINDISABLED] Dovecot ready.
* BAD [ALERT] cleartext authentication not allowed without SSL/TLS, but your client did it anyway.
S1 NO [PRIVACYREQUIRED] Cleartext authentication disallowed on non-secure (SSL/TLS) connections.
```

**No `AUTH=PLAIN` in the capability list, and `LOGINDISABLED`.** My drop-in config never took effect.

`DovecotServer` used `WithResourceMapping(dropIn, "/etc/dovecot/conf.d/99-harness.conf")`. It was **accepted without error and the file never arrived**, so Dovecot kept its defaults and refused cleartext. Switched to `WithBindMount`, which is the form I had verified by hand, and IMAP passes immediately.

The trap is the shape of it: a backend refusing a credential and a proxy failing to parse one both surface as "the IMAP server has unexpectedly disconnected", and I read the second from the first.

## The test that should have stopped me

I wrote the unit regression first, replaying the captured bytes, and **it passed**. I had a measurement contradicting my diagnosis and I explained it away as "socket versus pipe" instead of following it. That was the moment to stop, and I went past it. The bisect by driving the connector directly is what found the truth.

## What is real, and it is smaller

One genuine finding does survive, and it is a robustness gap rather than the failure:

**`ImapBackendConnector` treats an untagged status line as a protocol error.** Dovecot sent `* BAD [ALERT] ...` before its tagged `S1 NO`, and our loop only matches `+`, or `S1`-prefixed OK/NO/BAD. An untagged line falls through to `throw new AccessProxyProtocolException("The IMAP backend sent an unexpected authentication reply.")`.

RFC 3501 permits untagged responses at any time, so a server that emits any informational line during authentication makes the proxy report a protocol error instead of reading on to the tagged reply. It misclassifies a clean rejection as a malformed conversation. **Severity: low to medium, not high.** It did not cause the IMAP failure; the backend genuinely refused.

## State

| command | result |
|---|---|
| `dotnet build StyloMail.slnx` | 0 errors, 0 warnings |
| `STYLOMAIL_HARNESS=1 dotnet test .../StyloMail.Integration.Tests` | **5 passed, 0 failed, 0 skipped** |
| `dotnet test tests/StyloMail.AccessProxy.Tests` | **64 passed** |

`ImapThroughProxyTests` is `[HarnessFact]` again and passes. Two new integration tests drive the backend connector alone, which is what bisected this and is worth keeping: it is the only test pointing our hand-written IMAP client side at a server nobody here wrote.

**I did not change `src/`.** The authorised fix is not needed, because there was nothing to fix.

## What I need you to do

**Correct or withdraw `149f25e`.** The issue file states a defect that does not exist and names a required fix that would be wrong to implement: "handle the challenge/response form as well as the inline one" describes behaviour the proxy already has. I would rather retract it loudly than leave a plausible-looking workstream that someone picks up later.

**What survives for the record:** the earlier portability finding stands unchanged (no capability discovery, no `LOGIN` fallback, with the only-over-TLS caveat). And the harness still justified itself, twice: the GreenMail mechanism finding was real and measured, and this episode is a different kind of value, which is that it caught a wrong diagnosis by me before it became a wrong change in `src/`.
