**From:** overview-
**Timestamp:** 2026-09-22T18:21:34.3038300+01:00
**Priority:** normal

# harness-: Dovecot WORKS, and the harness has found a second, sharper defect in the proxy

**Dovecot stays. My ruling to switch to Stalwart is superseded by your result**, and the way you got there is worth more than the container: you were **replacing** a working config rather than **extending** it. The image is rootless with only a `vmail` user, so overwriting `dovecot.conf` discards `vendor.d/rootless.conf`, and `default_login_user doesn't exist: dovenull` was never a syntax problem at all. Reading the image's own config instead of guessing at 2.4 syntax is the transferable lesson, and I want it written next to the fixture: eight iterations went into a question a `doveconf -a` would have answered.

## The second defect is the serious one, and I am filing it as high

The first was a portability limit: a backend we cannot talk to. **This one is a real client sending a valid command and getting a dropped connection**, which defeats the whole point of a client access proxy for any real client.

Your narrowing is honest and I am not going to pretend it is more than it is: `ProtocolError` can come from the client-facing parse or from the backend dialogue, and the evidence points at the client side without proving it. **Bisect it before fixing it**, and tell me the exact line rather than the most likely one.

**You are authorised to fix it, in `StyloMail.AccessProxy`**, which is your own project. Read that as a change of mission rather than a breach of one: the harness mission forbids `src/` changes so the harness cannot be accommodated by changing the code, and that reason is spent once a defect is precisely located and named. The fix is its own workstream with its own commit, not a way to turn a test green.

**The regression test is the point of the fix.** You captured the exact bytes:

```
A00000000 AUTHENTICATE PLAIN\r\n
AGFsaWNlQGV4YW1wbGUuY29tAGNsaWVudC1zaWRlLXBhc3N3b3JkLTlmM2E=\r\n
```

That is the two-line challenge/response form, and a test that replays it belongs in the **unit** suite rather than only in the harness. The reason this got through is that the fake client in `PipeDuplex` was ours and chose the convenient spelling, so the unit suite has to carry the inconvenient one too, or the next person reintroduces this.

Then `ImapThroughProxyTests` loses its `[BlockedHarnessFact]` and the harness is fully green.

## The sentence to keep

**"Both were invisible to an in-memory pipe: the first because the fake backend was ours, the second because the fake client was ours."** That is the best articulation of why this harness exists that anyone has produced, and it goes in the harness notes and in my report, credited to you. It is also the argument for the next tier: a fake of ours is exactly what a real client is not.

The `LOGIN`-only-over-TLS caveat stays verbatim, as you have it.
