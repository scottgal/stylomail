# harness- (held by `access-`) : the protocol test harness

This lane puts real clients and real servers on real sockets around StyloMail's hand-written protocol
code. The operator asked for it directly: a way to exercise the Gmail, SMTP, POP3 and IMAP variants
rather than only the in-memory pipes the suite uses today.

**The fleet is at its 12-agent cap**, so this lane has no new agent. It is held by `access-`, whose
own project is the thing this harness exists to test, and who therefore starts with the domain
knowledge that matters. If a slot frees up, the lane can move to a dedicated `harness-`.

## Read first, in this order

1. **`docs/protocol-harness-plan-01.md`**. The first deliverable, written as a task-by-task plan with
   the code in it. Execute it in order, test first.
2. **`tests/StyloMail.AccessProxy.Tests/Support/`**, especially `ProxyHarness.cs` and `PipeDuplex.cs`.
   That is the in-memory tier being built above, and its vocabulary is the one to extend rather than
   replace.
3. `src/StyloMail.AccessProxy/Sessions/Channels.cs` for `IDuplexChannel` and `StreamDuplexChannel`,
   and `Sessions/AccessProxySessionBase.cs` for `RunAsync`.
4. `.styloagent/spec.md` section 9, the client access proxy.

## Why this exists, because it decides what counts as done

**`StyloMail.AccessProxy` and `StyloMail.Transport` have no package references at all.** The IMAP,
POP3 and SMTP dialects are hand-written on both sides. Today's tests drive them through `PipeDuplex`
against a fake backend, which proves the state machine and proves nothing about the wire. A
hand-rolled protocol parser fails on framing, TLS, and the quirks of real clients and real servers,
and none of those exist in an in-memory pipe. **That is the class of defect you are here to find.**

## Prerequisites, already verified

- **Docker Desktop 29.3.1 is running and responding.** Nothing to install.
- **`greenmail/standalone:2.1.14` exists on Docker Hub** and is the tag the plan pins. XOAUTH2 is in
  the image from 2.1.3 (SMTP) and 2.1.5 (IMAP and POP3), which is why that tag matters later.

## The rules that bind every lane here

- **Do not run `git add` or `git commit`.** Leave your work in the tree and report; `overview-`
  verifies and commits the lane. **Never `git commit --amend`, never `git reset`**: several agents
  share this tree and one commit has already been destroyed that way today.
- Build with `export DOTNET_ROOT=/usr/local/share/dotnet` and
  `export PATH="/usr/local/share/dotnet:$PATH"`. The solution is `StyloMail.slnx`. **Add your new
  project to it**, or it does not exist as far as CI is concerned. **Analyzers are errors**, so a
  warning is a failed build.
- **The harness is opt-in.** Every test is skipped unless `STYLOMAIL_HARNESS=1`. A suite that needs a
  container runtime must never be the reason a normal `dotnet test` fails. Verify both directions.
- **Never use an em-dash**, in code, comments, documentation or commit messages. Use a colon or a
  full stop.
- **Stay in your lane**: the new test project, the solution file, and nothing else. Do not modify
  `src/` at all. If the harness finds a defect in the proxy, that is a **finding to report**, not a
  licence to fix it, and a finding is worth more than a green test.

## Done when

- Every task in `docs/protocol-harness-plan-01.md` is complete, tests written first and seen to fail.
- `STYLOMAIL_HARNESS=1 dotnet test tests/StyloMail.Integration.Tests/StyloMail.Integration.Tests.csproj`
  is green, and a plain `dotnet test StyloMail.slnx` reports those tests as **skipped** with every
  other test unchanged.
- `dotnet build StyloMail.slnx` is 0 errors and 0 warnings.
- You have reported to `overview-` with: files created, the package versions `dotnet add` resolved,
  the exact totals with and without the variable, what you ran, and **anything you could not verify**.

**Report a frozen tree.** Nothing edited after the last verification run, and the numbers in the
report are the numbers measured on that tree. A lane mid-cycle is indistinguishable from a lane that
is broken, and only the freeze distinguishes them.

## What to do when the harness finds something

It probably will, and that is the point. If a real client or server trips the proxy, **do not fix the
proxy**. Capture the exact bytes or the exact failure, report it to `overview-`, and carry on with the
plan. The first time a hand-written parser meets a real client is exactly when this happens, and a
precise finding is the deliverable.

Do not start the SMTP or Cloudflare work. Those are plans two and three.
