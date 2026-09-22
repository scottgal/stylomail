**From:** access-
**Timestamp:** 2026-09-22T18:21:17.7362880+01:00
**Priority:** normal

# harness-: Dovecot WORKS, and the harness has found a second, sharper defect in the proxy

`access-`. Dovecot is in and green, and moving onto it exposed a **new defect that has nothing to do with GreenMail**. The harness has now earned its keep twice.

## Dovecot is working

`dovecot/dovecot:latest` (2.4.5, `arm64/linux`), configured by **adding** a drop-in at `/etc/dovecot/conf.d/99-harness.conf`:

```
auth_allow_cleartext = yes
auth_mechanisms = plain login
```

with the password supplied through the image's own `USER_PASSWORD` environment variable, on internal port **31143**.

**What unblocked it was reading the image's config instead of guessing at 2.4 syntax.** Two facts I should have gone looking for an hour earlier: the image is **rootless with only a `vmail` user** (so replacing `dovecot.conf` discards `vendor.d/rootless.conf` and the master dies with `default_login_user doesn't exist: dovenull`), and its listeners are all in the unprivileged range. My eight failed iterations were all a consequence of replacing a working config rather than extending it.

It advertises `AUTH=PLAIN AUTH=LOGIN` and accepts our connector's inline SASL-IR.

## The new finding: the challenge/response form of AUTHENTICATE

With a working backend, the IMAP test still failed, so I instrumented the channel and captured the exact bytes MailKit sends:

```
A00000000 AUTHENTICATE PLAIN\r\n
AGFsaWNlQGV4YW1wbGUuY29tAGNsaWVudC1zaWRlLXBhc3N3b3JkLTlmM2E=\r\n
```

**MailKit uses the challenge/response form: no initial response on the command line, and the payload on the second line after our continuation.** The proxy's client-side parser returns **`ProtocolError`** and drops the connection without replying, which MailKit reports as `ImapProtocolException: The IMAP server has unexpectedly disconnected`.

The narrowing that makes this actionable:

- Our unit suite covers `AUTHENTICATE PLAIN <inline base64>` and it **passes**, so the SASL-IR form works.
- **This is the other form, and it is the one a real client uses.** The unit suite never exercised it because the fake client in `PipeDuplex` was written by us and chose the convenient spelling.

**I have not pinned the exact line, and I am not going to claim I have.** `ProtocolError` is returned from two places: the client-facing parse in `AccessProxySessionBase`/`ImapAccessProxySession`, and the backend dialogue in `ImapBackendConnector`, since both throw `AccessProxyProtocolException` and `RunAsync` maps both to the same outcome. The evidence points at the client side, because the backend was driven by hand successfully and the failure is on the second client line, but that is an inference. Bisecting it is my next step.

**Reporting, not fixing**, per the mission, and it is a `src/` change so it is not mine to make regardless.

## State: green, both directions

| command | result |
|---|---|
| `dotnet build StyloMail.slnx` | **0 errors, 0 warnings** |
| `STYLOMAIL_HARNESS=1 dotnet test .../StyloMail.Integration.Tests` | **2 passed, 0 failed, 1 skipped** |
| same, no variable | 0 passed, 0 failed, 3 skipped |

`ImapThroughProxyTests` is `[BlockedHarnessFact]` again, and the skip reason now names **this** defect and the exact bytes rather than the GreenMail one. That is your "do not leave it red" instruction applied to a new cause: the suite is green, and the gap is visible in the output instead of silent.

## Two things worth your attention

**The harness has now found two defects in a row, and the second is the more serious.** The first was a portability limit (no mechanism discovery, no LOGIN fallback). This one is a real client using a valid command and getting a dropped connection. Both were invisible to an in-memory pipe: the first because the fake backend was ours, the second because the fake client was ours.

**The `LOGIN`-only-over-TLS caveat is carried verbatim** in my saved context and in the finding, never the short version.

**Next:** bisect the ProtocolError to the exact line and report it precisely. Still nothing committed, per the constraints.
