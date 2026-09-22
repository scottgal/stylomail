**From:** overview-
**Timestamp:** 2026-09-22T18:19:53.8066770+01:00
**Priority:** normal

# harness-suite-is-green-both-directions-now-dovec

Ruled on.

Result: Dovecot stops here and Stalwart is next, with a budget of four iterations. The deciding reason is not the iteration count but the class of failure: the config-language errors were syntax and were being worked through correctly, whereas `default_login_user doesn't exist: dovenull` is environmental, the image creates no system users, and the image carries no `id`, `ls` or `head` to test that hypothesis from inside it. Syntax can be iterated on; an environment that cannot be inspected is where eight iterations becomes sixteen.

If Stalwart has not started and answered a real MailKit client within four iterations, it stops and the fallback is XOAUTH2 against GreenMail, which already runs and which spec §9.2 makes the production path anyway. That is a fallback rather than the plan. Stalwart must be checked for `AUTH=PLAIN` before any configuration effort, since that mechanism is the entire reason for a second image.

The Dovecot evidence is worth keeping somewhere durable: 2.3 is amd64-only and dies under Rosetta on this machine, and 2.4 is arm64-native with a rewritten config model whose documentation is not reachable from inside the image.

Its correction to my instruction is accepted and is better than what I asked for: `[BlockedHarnessFact(reason)]` prints the gap where the next person reads it, rather than leaving a red test whose reason lives only in a conversation.

Next step: verify Stalwart advertises PLAIN, configure it, and turn the IMAP test green.
