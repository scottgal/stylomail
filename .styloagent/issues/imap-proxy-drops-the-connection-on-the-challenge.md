**From:** overview-
**Timestamp:** 2026-09-22T18:21:36.5668280+01:00
**Severity:** high
**Status:** resolved
**Source:** internal

# IMAP proxy drops the connection on the challenge/response form of AUTHENTICATE

Found by the protocol harness with a real client: MailKit's IMAP client authenticates using the two-line challenge/response form of `AUTHENTICATE`, and StyloMail's client-facing IMAP parser returns `ProtocolError` and drops the connection without replying.

CAPTURED BYTES

MailKit sends exactly this, instrumented on the channel by `access-`:

```
A00000000 AUTHENTICATE PLAIN\r\n
AGFsaWNlQGV4YW1wbGUuY29tAGNsaWVudC1zaWRlLXBhc3N3b3JkLTlmM2E=\r\n
```

That is: no initial response on the command line, then the base64 payload on the second line after the server's continuation. The client sees `ImapProtocolException: The IMAP server has unexpectedly disconnected`.

WHY THE SUITE MISSED IT

The unit suite covers `AUTHENTICATE PLAIN <inline base64>`, the SASL-IR form, and it passes. The fake client in `PipeDuplex` was written by us and chose the convenient spelling. **A real client uses the other form**, so the covered path and the used path were different, and nothing in an in-memory suite could tell.

Not yet bisected to the line. `ProtocolError` is returned from two places, the client-facing parse in `AccessProxySessionBase`/`ImapAccessProxySession` and the backend dialogue in `ImapBackendConnector`, because both throw `AccessProxyProtocolException` and `RunAsync` maps both to the same outcome. The evidence points at the client side, since the backend was driven by hand successfully and the failure is on the second client line, but that is an inference rather than a measurement.

IMPACT

A real IMAP client cannot authenticate through the proxy, which defeats the client access proxy's purpose for any client that is not our own test double. AccessProxy is not hosted yet, so this is latent rather than a live outage.

REQUIRED FIX

Handle the challenge/response form as well as the inline one, with a **unit** test that replays the captured byte sequence, because the unit suite is where this class of gap has to be closed rather than only in the harness.

RELATED

A separate and less severe AccessProxy defect, no capability discovery and no `LOGIN` fallback, is filed separately. Both were invisible to an in-memory pipe, the first because the fake backend was ours and the second because the fake client was ours.

---

**RESOLVED (verified by `overview-`, 2026-09-22).** Withdrawn. The defect did not exist: the harness's Dovecot drop-in never arrived because WithResourceMapping accepted the mapping silently, so the backend refused the credential and the refusal was read as a proxy parse failure. See retracted-the-imap-proxy-does-not-drop-the-conne.md.
