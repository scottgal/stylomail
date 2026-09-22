**From:** ingress-
**Timestamp:** 2026-09-22T14:37:29.6696850+01:00
**Priority:** normal

# New work: the executable's operating documentation, plus one API gap for the desktop console

ITEM 1 DONE. ITEM 2 NOT STARTED, deliberately, per your instruction. Read on.

## 1. `docs/running.md` — written, and verified by running the binary

262 lines, covering all eight of your bullets: the two front ends and why the split exists, startup
order with the forced resolutions and why each is forced, the composition assertions, middleware
order, conditional routes, the startup transport line, every environment variable with the
never-from-a-file rule, and how to verify a running instance. I added the CLI command set, exit codes
and the transport config table, because "how do I run this" needs them.

**34 checks, all against a real process, all passing** — a script that starts the binary, drives real
sockets and real HTTP requests, and asserts on what came back. Not read off `Program.cs`.

Things that were worth actually running, because reading would have left me less sure than I sounded:

- **Readiness is a probe, not a startup flag.** Verified by taking the spool's write permission away
  *underneath a running process*: `503 {"status":"not_ready","failedChecks":["spool"]}`, and back to
  `200` when restored. I would have documented the 503 as likely and been right, which is not the same
  as knowing.
- **The startup refusals abort with `SIGABRT`** — shell exit code `134`, not a tidy `1`. Five distinct
  misconfigurations were run and each names what an operator has to fix, including both values for the
  ingress/queue bound mismatch. Worth documenting precisely, because a script checking `$?` sees 134.
- **A CLI command does not start hosted services.** I had written that from reading the code, then
  found a sharp way to check it: run a CLI command under a config that makes the SMTP listener's
  *construction* throw. A command that started hosted services would fail; it exits 0. That is
  deterministic rather than a timing race.
- **`Port: 0` genuinely binds an OS-chosen port**, confirmed by asking the kernel what the process
  listens on rather than by trusting the option.
- **An unconfigured `RecipientDomains` refuses inbound** (`550 5.7.1 Relay access denied`) — the
  "unset must not mean unrestricted" rule, run rather than reasoned.

One correction to myself, since it is the kind of thing that would have made the document wrong: my
first version of the empty-domains check configured a domain and then asserted the empty behaviour
against it. The check failed, I re-read my own probe rather than the system, and the system was right.
The probe also produced one transient failure during a rebuild window; I re-ran it three times on a
tree I verified clean and it is 34/34, so I am confident it was the rebuild and not the behaviour —
but I am flagging that I saw it rather than reporting only the clean runs.

## 2. The sender-listing route — held

`desktop-` has not asked yet, and your instruction was explicit that only what the console asks for
gets built. So I have not built anything and will not until the message arrives. When it does I will
apply your three rules: authenticated, tenant-scoped from the principal, privilege-separated;
cross-tenant reads absent rather than forbidden; and if it asks for something that should not be a
route, I will say so rather than adding it.

No UI, and nothing speculative.

STATE: `docs/running.md` in the tree. 150 Host tests green, solution 0 errors / 0 warnings (I cleared
an xUnit analyzer warning of my own that appeared when the analyzers tightened), 0 failures across 20
tree-verified-clean runs.

NEXT: waiting on `desktop-`. Nothing else in flight.
