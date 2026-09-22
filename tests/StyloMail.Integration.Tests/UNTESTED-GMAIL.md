# What this harness does not cover: Gmail

The plan requires the untested behaviour to be written down rather than implied away. This is that
note. It records what the harness proves, what it cannot prove, and which of those gaps matter to the
product rather than only to the test suite.

## What the harness covers

RFC IMAP against two real servers, as of the tier one plan:

| Backend | Client | What it proves |
|---|---|---|
| Dovecot 2.4.5 | MailKit | A real client reaches a real server through the proxy, both hand-written halves on trial |
| GreenMail 2.1.14 | MailKit | A real client logs into a container backend, and POP3 reaches it by the `USER`/`PASS` pair |

Two backends rather than one, because they disagree in a way that turned out to matter: Dovecot
advertises `AUTH=PLAIN` and GreenMail does not, which is why the IMAP leg runs against Dovecot and the
POP3 leg runs against GreenMail.

## What it does not cover, and cannot

**Gmail is a superset of IMAP, not an implementation of it.** Its IMAP adds three extensions that no
container image implements:

- `X-GM-LABELS`: the Gmail label set on a message, which is not the same as an IMAP folder.
- `X-GM-MSGID`: Gmail's per-message identifier, stable across folders.
- `X-GM-THRID`: Gmail's conversation identifier.

They are advertised through the `X-GM-EXT-1` capability. Because they are Google's rather than the
RFC's, no image implements them, so **no test in this repository exercises them end to end**. That is
a coverage gap and not a defect. It is stated here so it is not silently assumed away by a green suite.

## Why this gap is narrower than it looks, and where it is not narrow at all

**Narrower, on the wire.** After authentication the proxy is a byte pump. It parses nothing, so a
`FETCH` carrying `X-GM-LABELS` is relayed verbatim in both directions and there is no code of ours for
a Gmail-specific response to trip. Tier one's evidence for byte preservation is in
`tests/StyloMail.AccessProxy.Tests` (awkward payloads, NULs, bare LF, high bytes) and the relay has no
parser that could behave differently for an extension it does not know.

**Not narrower, at session establishment.** The proxy terminates the client session and advertises
**its own** capability list, not the backend's:

```
* OK [CAPABILITY IMAP4rev1 AUTH=PLAIN AUTH=LOGIN] StyloMail ready
```

`X-GM-EXT-1` is not in it. A client that checks capabilities before deciding whether to use Gmail
labels will conclude the server does not support them and will not try. **That is a product
consequence rather than a test gap**, and it is the same limitation that makes the proxy useful: the
capability list belongs to the terminating proxy, so the backend's extensions are invisible until
after authentication, if a client re-issues `CAPABILITY` then.

That behaviour is untested because it cannot be tested here. It is also the thing most likely to be
reported as a bug by a real user with a Gmail account and a labels-aware client.

## What would close it, in order of cost

1. **A manual check against a real Gmail account**, which the harness cannot do because the brief
   forbids real accounts and the OAuth path is plan three. This is the only thing that proves the
   Gmail extensions work through the proxy, and it is an operator decision rather than a test.
2. **A recorded-session replay.** Capture a real `X-GM-EXT-1` exchange once, then replay the bytes
   through the relay in a unit test. This proves the byte pump does not corrupt them, which is the
   part most likely to break, without needing Google at test time. Cheap once the capture exists.
3. **A capability passthrough test** that asserts what the proxy advertises pre-authentication and
   what it advertises after, so the current behaviour is at least pinned and a change to it is
   deliberate.

## Related gaps, recorded here rather than separately

- **SMTP submission and the upstream MTA handoff.** `StyloMail.Transport` has the same hand-written
  problem and gets the same treatment in plan two.
- **Cloudflare Email Routing and the OAuth token endpoint.** Both HTTP, both need `WireMock.Net`,
  neither is a mail protocol. Plan three.
- **XOAUTH2 against a real provider.** The Dovecot and GreenMail images are pinned at versions that
  support XOAUTH2, but the credential seam test is not written. It belongs with plan three, and
  GreenMail advertising `AUTH=XOAUTH2` is not the same as it accepting a token we can mint.
