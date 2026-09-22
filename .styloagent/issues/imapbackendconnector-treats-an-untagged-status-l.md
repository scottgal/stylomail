**From:** overview-
**Timestamp:** 2026-09-22T18:27:10.3171950+01:00
**Severity:** low
**Status:** open
**Source:** internal

# ImapBackendConnector treats an untagged status line as a protocol error

Survived the retraction of the challenge/response issue and is a genuine robustness gap, not the cause of any observed failure.

THE GAP

During authentication, `ImapBackendConnector`'s reply loop matches `+`, or a line prefixed with the command tag carrying `OK`, `NO` or `BAD`. **An untagged line falls through** to `throw new AccessProxyProtocolException("The IMAP backend sent an unexpected authentication reply.")`.

Dovecot emits exactly such a line:

```
* OK [CAPABILITY ...] Dovecot ready.
* BAD [ALERT] cleartext authentication not allowed without SSL/TLS, but your client did it anyway.
S1 NO [PRIVACYREQUIRED] Cleartext authentication disallowed on non-secure (SSL/TLS) connections.
```

The untagged `* BAD [ALERT]` precedes the tagged `S1 NO`. The proxy therefore reports a protocol error rather than reading on to the tagged reply, **misclassifying a clean rejection as a malformed conversation**. RFC 3501 permits untagged responses at any time, so any server that emits an informational line during authentication can trigger it.

IMPACT

A refusal that should be reported as a refusal is reported as a protocol fault. The session fails closed either way, so this is a diagnosis problem rather than a security one, which is why it is medium at most and not high.

NO FIX PROPOSED, AND IT IS NOT URGENT

The correct shape is to skip untagged lines and read on to the tagged reply, bounded so a server emitting them forever cannot spin the loop. Anyone picking this up should note that the bound is the part worth thinking about: reading on indefinitely trades a misclassified error for a hang.
