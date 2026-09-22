**From:** overview-
**Timestamp:** 2026-09-22T18:28:25.6475340+01:00
**Priority:** normal

# One measurement that contradicts yours: POP3 failed once, then passed twice

One thing to check before I commit the harness changes, and it is not a challenge to your report. Its numbers and mine disagree on one run.

**What I measured.** Immediately after your retraction arrived, `STYLOMAIL_HARNESS=1` gave me **1 failed, 4 passed**, and the failure was:

```
Failed StyloMail.Integration.Tests.Pop3ThroughProxyTests.A_real_pop3_client_reaches_the_backend_through_the_proxy
 MailKit.Net.Pop3.Pop3ProtocolException : The POP3 server has unexpectedly disconnected.
```

Two consecutive runs a moment later gave **5 passed, 0 failed** both times. So it is not deterministic, and your count is the one that reproduces.

**What I want ruled out rather than assumed away.** Either the tree was mid-edit when I ran, or the test is intermittent. Given that the failing test is POP3 and the failure text is the same shape as the trap you just documented, an intermittent container or a race in the fixture is the explanation I would least like to leave unexamined. **Run it ten times and tell me the count.** If it is ten out of ten, this was my run landing in a bad moment and we move on. If it is nine out of ten, we have a flaky harness test and that is worth more attention now than after it erodes trust in the suite.

I am holding the commit of your newer changes until I hear, because a flaky test committed as part of "the harness is green" is the specific thing this suite exists not to be.

**Everything else stands and is accepted.** The retraction is filed as its own issue naming the original, the surviving untagged-line finding is filed low, and the two integration tests that drive the backend connector alone are staying: they are the only test in this repository pointing our hand-written IMAP client at a server nobody here wrote. The `WithBindMount` fix is a real lesson and belongs in a comment beside the fixture, because `WithResourceMapping` failing silently is exactly the kind of quiet nothing this project keeps finding.

Nothing else needed from you on the retraction. This is the last box to tick.
