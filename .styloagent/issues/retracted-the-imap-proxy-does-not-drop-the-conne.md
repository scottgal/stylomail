**From:** overview-
**Timestamp:** 2026-09-22T18:27:08.5311620+01:00
**Severity:** low
**Status:** open
**Source:** internal

# RETRACTED: the IMAP proxy does not drop the connection on challenge-response AUTHENTICATE

**Withdraws the high-severity issue filed earlier the same hour, which stated a defect that does not exist.** The proxy already handles the two-line challenge/response form of `AUTHENTICATE`, and no fix is required.

WHAT WAS ACTUALLY HAPPENING

The failure was `access-`'s harness configuration, and it mimicked a proxy defect precisely. `DovecotServer` used `WithResourceMapping(dropIn, "/etc/dovecot/conf.d/99-harness.conf")`. The mapping was accepted without error and **the file never arrived**, so Dovecot kept its defaults. Driving the backend connector alone, with no client in front of it, captured what Dovecot really sent:

```
* OK [CAPABILITY IMAP4rev1 IMAP4rev2 ... SASL-IR LITERAL+ STARTTLS LOGINDISABLED] Dovecot ready.
* BAD [ALERT] cleartext authentication not allowed without SSL/TLS, but your client did it anyway.
S1 NO [PRIVACYREQUIRED] Cleartext authentication disallowed on non-secure (SSL/TLS) connections.
```

No `AUTH=PLAIN` in the capabilities, and `LOGINDISABLED`: the backend genuinely refused the credential. Switched to `WithBindMount`, which had been verified by hand, and IMAP passes immediately. The harness is now **5 passed, 0 failed, 0 skipped**, with `ImapThroughProxyTests` back to `[HarnessFact]`.

WHY IT WAS BELIEVED, AND THE PART WORTH KEEPING

A backend refusing a credential and a proxy failing to parse one both surface as `ImapProtocolException: The IMAP server has unexpectedly disconnected`, and the second was read from the first.

**The decisive process failure is that the unit regression was written first, replaying the captured bytes, and it passed.** There was a measurement contradicting the diagnosis, and it was explained away as "socket versus pipe" rather than followed. Bisecting by driving the connector directly is what found the truth. **When a measurement contradicts a diagnosis, the measurement wins, and an explanation that preserves the diagnosis is the thing to distrust.**

THE INSTRUCTED FIX IS WITHDRAWN

"Handle the challenge/response form as well as the inline one" describes behaviour the proxy already has. Implementing it would have been wrong work against a fabricated defect, which is why this is retracted loudly rather than quietly dropped.

WHAT SURVIVES

The earlier AccessProxy finding stands unchanged: no capability discovery and no `LOGIN` fallback, with the only-over-TLS caveat. And a new and smaller robustness gap is filed separately, about untagged status lines.
