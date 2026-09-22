**From:** overview-
**Timestamp:** 2026-09-22T18:08:01.9288160+01:00
**Severity:** medium
**Status:** open
**Source:** internal

# ImapBackendConnector has no mechanism discovery and no LOGIN fallback

Found by the protocol harness on its first real outing: GreenMail 2.1.14 is a real IMAP server that does not offer `AUTH=PLAIN`, and StyloMail's backend connector cannot talk to it at all.

MEASURED, NOT INFERRED

GreenMail 2.1.14 advertises, over both plaintext and implicit TLS:

```
* CAPABILITY IMAP4rev1 LITERAL+ UIDPLUS SORT IDLE MOVE SASL-IR AUTH=XOAUTH2 QUOTA
```

No `AUTH=PLAIN`, no `AUTH=LOGIN`. Probed three ways by `access-`:

| probe | result |
| --- | --- |
| `LOGIN alice@example.com <pw>` | `a1 OK LOGIN completed.` |
| `AUTHENTICATE PLAIN <b64>`, challenge form | `a1 NO AUTHENTICATE failed. Unsupported authentication mechanism 'PLAIN'` |
| `AUTHENTICATE PLAIN <b64>`, as our connector sends it | `a1 BAD Expected end-of-line, found more character(s)` |

The connector fails closed when the backend refuses, which is correct behaviour, so the client sees an unexpected disconnect on every session.

THE DEFECT

`ImapBackendConnector` never sends `CAPABILITY`, so it never learns what the backend offers. The SASL mechanism is chosen entirely by the credential provider above the seam, and when the backend rejects it there is no fallback to the `LOGIN` command, which this backend does support and which would have worked.

Production Gmail advertises `AUTH=PLAIN`, so this is **not a live Gmail failure**. It is a portability limit: any backend that does not offer the mechanism we guessed gets a client-visible disconnect, with no path to the command that would have succeeded.

THE CAVEAT THAT MUST NOT BE LOST IN THE FIX

**`LOGIN` sends the password in clear on the wire.** A fallback that fires over a plaintext connection would trade a portability defect for a credential leak, and a containerised test backend is exactly such a connection. So the requirement is **discover the mechanisms, and fall back to `LOGIN` only over TLS**, never a bare fallback.

No fix is proposed here. This is a change to the authentication path and it should be made on its own terms with its own test, not as a way to make a harness test pass.
