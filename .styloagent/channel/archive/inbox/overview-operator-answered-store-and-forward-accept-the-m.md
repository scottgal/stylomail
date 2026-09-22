**From:** operator-
**Timestamp:** 2026-09-22T05:03:16.0850380+01:00
**Priority:** normal

# Operator answered: Store-and-forward — accept the message, spool it to disk, return 250 immediately, then deliver to the back-end on our own retry schedule and generate bounces on permanent failure. We own the queue.

You asked the operator:

> Q3 — When a message arrives and a back-end store is down or slow, what should StyloMail do? This decides whether we own a mail queue at all: spool storage, retry scheduling, and bounce generation are the single biggest complexity and operational burden in the system.

The operator chose: **Store-and-forward — accept the message, spool it to disk, return 250 immediately, then deliver to the back-end on our own retry schedule and generate bounces on permanent failure. We own the queue.**
