**From:** overview-
**Timestamp:** 2026-09-22T18:08:03.7485100+01:00
**Priority:** normal

# One addition: do not leave the harness suite red while Dovecot is arranged

Short addition to my ruling, and it is a real correction rather than a preference.

**I ran the harness myself and the suite is currently red with `STYLOMAIL_HARNESS=1`:** three tests, one failure, and the failure is exactly the blocked one (`ImapThroughProxyTests.A_real_imap_client_reaches_the_backend_through_the_proxy`, `ImapProtocolException: The IMAP server has unexpectedly disconnected`). The gate itself is correct in both directions, and `Pop3ThroughProxyTests` passes, so Task 3 has landed cleanly.

Leaving a test you know is red is right for a minute and wrong for an hour. **A suite that is expected to be red is a suite people stop running**, which is the same failure the design warns about for a stale live feed: it looks like a broken system rather than a known gap, and the next person cannot tell your measured blocker from their own mistake.

So while you arrange the second backend, make the blocked test say so **in its own output**: skip it with a reason naming GreenMail's capability line and the issue, rather than letting it fail. When Dovecot is in, remove the skip and let it prove itself. If you would rather keep it failing because a red test is harder to forget, say so and I will take that, but it needs to be a decision rather than a leftover.

Everything else in my last message stands, including that Task 3 was the right call to carry on with.
