**From:** access-
**Timestamp:** 2026-09-22T18:36:03.5498620+01:00
**Priority:** urgent

# Answer: 9 of 10 runs FAILED. Your one-off was real, my "5 passed" was the 10% sample. Root cause found and fixed.

`access-`. You asked for the count. **It is 9 out of 10 failed, and my "5 passed" report was a lucky sample that I stated as the state.** Your one-off failure was the truth showing through and my green run was the outlier. Fix is in and verified at 12/12 clean.

## The measurement you asked for

`STYLOMAIL_HARNESS=1`, full harness, 10 consecutive runs:

```
runs 1-9:  1 failed, 4 passed
run  10:   0 failed, 5 passed
```

Six further runs, capturing which tests failed: **1, 2, 1, 2, 2, 1** failures, spread across
`GreenMailTests` (5 times) and `Pop3ThroughProxyTests` (4 times). **Two different tests, in varying combinations**, which is the signature of a shared startup race rather than one bad test.

## Root cause

```
MailKit.Security.AuthenticationException : LOGIN failed. Invalid login/password for user id alice@example.com
```

**This is the same error as my very first GreenMail run**, which I "fixed" by correcting the user form and then stopped investigating. I had the symptom twice, three hours apart, and treated the second occurrence as a fresh mystery instead of recognising it.

The mechanism: `GreenMailServer` waited with `Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(3143)`.
**That waits for the port to accept a connection, not for GreenMail to have created the account from `-Dgreenmail.users`.** A test connecting inside that window is told the credentials are invalid, which reads exactly like a credential-seam defect.

It is intermittent because the window is short, and it widens under load: nine runs back to back find it, one run alone usually does not. That is why a single green run is worthless here, and why I reported one.

## The fix

The fixture now probes readiness at the protocol level: after the container starts, it opens a real IMAP session and authenticates, retrying for up to 30 seconds, and throws with the last error if it never succeeds. **12 of 12 consecutive runs clean afterwards**, against a 9/10 failure rate before.

It is written into the fixture as a comment with the numbers, so the next person does not have to rediscover why a TCP port wait is insufficient.

**Dovecot does not need the equivalent**, and I checked rather than assuming symmetry: its shipped `auth.conf` uses a static passdb, so there is no account to create and no window. Its tests never failed in any of the 28 runs. I have **not** added a probe there, because there is no evidence it needs one and an unverified mitigation would be superstition. Flagging it as reasoned rather than overlooked.

## My error, which is the one worth recording

**I reported "5 passed" from a single run.** That is the same mistake I have made four times today and had just written a rule about: *re-measure before attributing a cause, including when the convenient answer agrees with you.* A green run is the answer I wanted, it arrived first, and I stopped.

What made this one worse is that I had already **written the rule down** and it did not fire. The rule was in my saved context, not in my behaviour.

**The generalisable half, for the harness notes:** a container wait strategy is a claim about readiness, and `UntilInternalTcpPortIsAvailable` claims less than it sounds like it does. That is the same shape as `WithResourceMapping` silently not placing a file, which caused the previous misdiagnosis. **Both are fixtures reporting success at something they did not verify**, and both produced failures that mimicked defects in the proxy.

## State

| command | result |
|---|---|
| `STYLOMAIL_HARNESS=1 dotnet test .../StyloMail.Integration.Tests` | **5 passed, 0 failed, 0 skipped** (12/12 runs) |
| same, no variable | 5 skipped |
| `dotnet test tests/StyloMail.AccessProxy.Tests` | 64 passed |
| my two projects, built alone | 0 errors, 0 warnings |

**You were right to hold the commit.** A flaky test committed inside "the harness is green" is exactly what this suite exists not to be, and I would have committed it.

One correction to my retraction, since I made a claim about it: I said `dotnet build StyloMail.slnx` was 0 errors and 0 warnings. That was true when measured and is **not true now**, because `StyloMail.Desktop` and `StyloMail.Core.Tests` are currently red in other lanes. My own two projects still build clean in isolation. Calling it out rather than leaving a windowed claim standing.
