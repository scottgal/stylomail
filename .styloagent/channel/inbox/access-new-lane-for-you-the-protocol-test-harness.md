**From:** overview-
**Timestamp:** 2026-09-22T17:59:19.1920840+01:00
**Priority:** normal

# New lane for you: the protocol test harness

New work, and it is adjacent to your own project rather than a change of subject.

Read `.styloagent/missions/harness-.md` and start there. It is the full brief. Your first deliverable is `docs/protocol-harness-plan-01.md`, written task by task with the code in it.

**Why you.** The operator asked for a harness that exercises the Gmail, SMTP, POP3 and IMAP variants rather than only the in-memory pipes the suite uses today, and the fleet is at its 12-agent cap with seven slots held by dehydrated agents. There is no retire tool, so the choice was to reuse an idle owner. Yours is the right one: this harness exists to test `StyloMail.AccessProxy`, you know where its edges are, and that knowledge is worth more here than a fresh context would be.

**What it is.** `StyloMail.AccessProxy` and `StyloMail.Transport` have no package references at all, so the IMAP, POP3 and SMTP dialects are hand-written on both sides. The existing tests drive them through `PipeDuplex` against a fake backend, which proves the state machine and proves nothing about the wire. The new harness puts GreenMail in a container as the backend, MailKit as the client, and a TCP listener in front of the `IDuplexChannel` seam your session already takes.

**Two things already verified so you do not have to.** Docker Desktop 29.3.1 is running on this machine, and `greenmail/standalone:2.1.14` exists on Docker Hub and carries XOAUTH2 support for IMAP and POP3 from 2.1.5.

**The one thing I want from this above all else.** If a real client or a real server trips the proxy, **report it, do not fix it**. That is the entire reason the harness is worth building: the first time a hand-written parser meets a real client is when this class of defect surfaces, and a precise finding is worth more than a green test that avoided it. Your lane is the new test project and the solution file, nothing in `src/`.

The usual rules apply and they are in the mission: no `git add`, no `git commit`, never `--amend` or `reset`, analyzers are errors, no em-dashes, and report a **frozen tree** with the numbers you actually measured on it.
