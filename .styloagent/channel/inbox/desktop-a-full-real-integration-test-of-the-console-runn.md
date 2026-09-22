**From:** overview-
**Timestamp:** 2026-09-23T00:43:51.3013780+01:00
**Priority:** normal

# A full real integration test of the console, running the app against the real Host

The operator wants a **full real integration test**: the Avalonia app running, driven, and asserted against a real Host. You own the console and you built the harness this extends, so it is yours.

## What exists

`ux-scripts/run-console-smoke.sh`, `console-harness.sh` and `console-smoke.yaml` already do the hard part: a throwaway Host on loopback with locally generated values, the real headless Avalonia console pointed at it, a scripted run, screenshots, and teardown on every exit path including interrupts. I have run it and it passes.

**What it is, though, is a smoke.** It proves the console starts, connects and renders. The operator is asking for the whole surface exercised against the real thing.

## What I want

**Extend the scripted run to cover the console's actual work end to end**, in the same harness rather than a second one:

- **Connection**: a key entered, refused when the Host is wrong, and the live/stale distinction.
- **Senders**: the listing, the sidebar grouping, pause and resume, and the `source` field that says whether a sender was minted or configured.
- **Messages and decisions**: the list, selecting one, and **the join from a listed message to its decision**. That was the console's headline use case and its last wiring gap, so it is the one assertion that must not be shallow.
- **The decision pane**: the evidence it renders, and that it shows `deliveryTiming` rather than implying the system could have stopped something it only reacted to.
- **Feedback and quarantine release**, including the refusal paths, because a control that only succeeds is untested.
- **Management**: companies and sender settings, and the two stored-but-unread fields still being **labelled as not yet acted on**, since a control that looks like it works is a false statement about the system.

**Assertions, not screenshots.** Screenshots are the artefact that lets a human check what the assertions cannot; the test itself has to fail when the console is wrong.

**Repeatable from any starting state and leaving nothing behind**, which the harness already does and the extension must not break.

## The one standard I care about most here

This is the tier that finds what unit tests cannot, and this project has spent two days learning why: **an in-memory fake is ours, so it tests our assumptions about the client rather than the client**. Driving the real window against a real Host is the only thing that catches a control that renders but does not work, or a route the console calls that the Host does not serve.

So: when the run is green, **report what it would have caught if it were wrong**. If a step could pass against a console that was silently broken, it is not an assertion and I would rather hear that than have it counted.

## Rules

- **Do not run `git add` or `git commit`.** Leave it in the tree and report; I verify and commit.
- **Never `git commit --amend`, never `git reset`.**
- Build with `export DOTNET_ROOT=/usr/local/share/dotnet` and `export PATH="/usr/local/share/dotnet:$PATH"`. **Analyzers are errors.**
- **Never use an em-dash.** Use a colon or a full stop.
- The harness is Debug-only by design; that is fine, and keep it that way.

**Run it and report the result**, with the totals, what each step asserts, and anything the run could not reach.
