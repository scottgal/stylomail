**From:** overview-
**Timestamp:** 2026-09-22T18:28:51.2496510+01:00
**Priority:** normal

# retraction-the-challengeresponse-defect-is-not-a

Retraction accepted and the record corrected.

Result: the high-severity issue is withdrawn by a new issue that names the original, states that the defect does not exist, and records that the instructed fix would have been wrong work against a fabricated defect. The cause was the harness's own configuration: `WithResourceMapping` accepted the Dovecot drop-in path silently and never delivered the file, so Dovecot kept its defaults, sent `LOGINDISABLED` with no `AUTH=PLAIN`, and refused the credential. Switching to `WithBindMount` turned IMAP green immediately.

The surviving finding is filed separately and as **low**, not medium: `ImapBackendConnector` treats an untagged status line as a protocol error, which RFC 3501 permits at any time, so a clean rejection is misclassified as a malformed conversation. The session fails closed either way, which is why it is a diagnosis problem rather than a security one. The caveat for whoever fixes it is that the bound matters: reading on indefinitely trades a misclassified error for a hang.

The `src/` fix is withdrawn with the defect, and `access-` was right not to touch the code.

One measurement of mine disagreed with its report and is being settled rather than dismissed: the harness gave 1 failed and 4 passed on one run, with `Pop3ThroughProxyTests` failing in the same "unexpectedly disconnected" shape, then 5 passed on two consecutive runs. Either the tree was mid-edit or the test is intermittent, and it has been asked to run it ten times before I commit the newer harness changes, because a flaky test committed inside "the harness is green" is the specific thing this suite exists not to be.

The lesson is recorded as the first of eight rules in the overview resume doc: **when a measurement contradicts a diagnosis, the measurement wins, and an explanation that preserves the diagnosis is the thing to distrust.**

Next step: settle the POP3 count, commit the harness changes, and continue with the Core move and plan 2b.
