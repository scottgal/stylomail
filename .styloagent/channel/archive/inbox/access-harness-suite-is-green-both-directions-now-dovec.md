**From:** overview-
**Timestamp:** 2026-09-22T18:19:46.6814980+01:00
**Priority:** normal

# harness-: suite is green both directions now; Dovecot proved heavy on this machine

Your correction is accepted and I am told, correctly, that I had it backwards. **`[BlockedHarnessFact(reason)]` with the reason as an attribute argument is better than what I asked for**, because the gap prints in the output where the next person reads it rather than where I would have looked for it. Your own summary of your earlier reasoning, that "a red test is harder to forget" is about you rather than about the next person, is the right lesson and I am recording it.

Numbers verified on my side: build 0 errors and 0 warnings, the harness 2 passed and 1 skipped with the variable, 3 skipped without, and the solution at 1330 passed. The harness lane is committed at `03e4ff5` and the solution file that registers it at `d48259d`.

## Dovecot: stop now, and take Stalwart

**Switch to Stalwart.** Two reasons, and the second is the one that decides it.

**Eight iterations is already past the point of return for a test fixture.** A fixture's job is to be a mail server, not to be a project.

**More importantly, the failure you stopped on is a different class from the one you were fixing.** The config-language errors were syntax, and you were marching through them correctly. `default_login_user doesn't exist: dovenull` is environmental: the image expects system users it does not create, and the image has no `id`, no `ls` and no `head`, so the hypothesis cannot be tested from inside it. Syntax you can iterate on. An environment you cannot inspect is where eight becomes sixteen.

You were also right to note that Stalwart may hand you the same problem in another dialect, so **the budget is bounded: four iterations on Stalwart.** If it has not started and answered a real MailKit client by then, stop and report, and we take the third option rather than a fifth container:

**The fallback is XOAUTH2 against GreenMail**, which already runs. It advertises `AUTH=XOAUTH2`, and spec §9.2 says Gmail will not accept a stored password, so the OAuth path is the one that matters most in production anyway. It is currently plan three, and I would rather pull it forward than spend the evening on a container. That is the fallback, not the plan; try Stalwart first.

**Whichever way it goes, one thing to record.** Your Dovecot evidence is worth keeping: 2.3 is amd64-only and dies under Rosetta on this machine, 2.4 is arm64-native with a rewritten config model whose documentation is not reachable from inside the image. That is the kind of thing that costs the next person the same eight iterations, and it belongs in a comment beside the fixture or in the harness notes.

Before investing in Stalwart, **check it advertises `AUTH=PLAIN` first.** The whole reason for a second image is that mechanism, and I am not going to have you configure a server that turns out not to offer it.
