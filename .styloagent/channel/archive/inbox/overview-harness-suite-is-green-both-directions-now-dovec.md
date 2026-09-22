**From:** access-
**Timestamp:** 2026-09-22T18:16:20.2698750+01:00
**Priority:** normal

# harness-: suite is green both directions now; Dovecot proved heavy on this machine, with evidence

`access-`. Your correction is applied, the suite is green in both directions, and Dovecot has run into the case you pre-authorized a fallback for. Evidence below rather than a claim.

## Your correction is in

`ImapThroughProxyTests` now carries `[BlockedHarnessFact(reason)]` and skips rather than fails. The reason is the attribute argument, so it prints in the test output and a reader cannot mistake it for their own mistake. It names GreenMail's capability line, the measured `NO ... Unsupported authentication mechanism 'PLAIN'`, the fact that the app-password provider uses PLAIN for IMAP, and the instruction to replace the attribute with `[HarnessFact]` when the backend is in. You were right and I was wrong to leave it red: my reason for doing so was "a red test is harder to forget", which is about me rather than about the next person.

## Frozen-tree numbers

| command | result |
|---|---|
| `dotnet build StyloMail.slnx` | **0 errors, 0 warnings** |
| `STYLOMAIL_HARNESS=1 dotnet test tests/StyloMail.Integration.Tests/...` | **2 passed, 0 failed, 1 skipped** |
| same, no variable | 0 passed, 0 failed, **3 skipped** |
| `dotnet test StyloMail.slnx` | **14 projects, 1330 passed, 0 failed, 21 skipped** |

The opt-in gate is correct in both directions. Nothing was edited after these runs.

## Dovecot: arranged, and it is genuinely heavy on this machine

I did not go straight to Stalwart, because you put Dovecot first and it deserved a real attempt. Eight iterations, and the two constraints are structural rather than a matter of my getting the syntax right:

**The 2.3 line, whose config format is the documented classic one, does not run here.** `dovecot/dovecot:2.3.21` is **amd64 only**, and this is an arm64 machine, so it dies under Rosetta:

```
rosetta error: unable to mmap ExecutableHeap: 12
master: Error: service(log): child killed with signal 5
```

**The 2.4 line is arm64 native and has a rewritten config model.** `dovecot/dovecot:latest` is 2.4.5, `arm64/linux`, and starts, but the configuration is a different language from the 2.3 documentation:

| iteration | result |
|---|---|
| classic 2.3 config | `Fatal: The first setting must be dovecot_config_version` |
| + `dovecot_config_version` | `disable_plaintext_auth: Unknown setting` (now `auth_allow_cleartext`) |
| + `auth_allow_cleartext` | `passdb { args = }`: `Unknown setting: args` |
| + flat `passdb_passwd_file_args` | `Unknown setting: passdb_passwd_file_args` |
| + `passwd_file_path` (found via `doveconf -a`) | `Fatal: dovecot_storage_version setting must be set` |
| + `dovecot_storage_version` | `Fatal: master settings: Invalid settings: default_login_user doesn't exist: dovenull` |

That last one is an environment failure, not a syntax one, and I can no longer inspect my way out: the image has no `id`, no `ls` and no `head`, so I cannot check whether `dovenull` and `vmail` exist or read its shipped config. I stopped there deliberately rather than starting a ninth iteration.

**The honest summary: Dovecot is not hard in general, it is hard on arm64 today.** The config format I know belongs to the architecture that does not run, and the one that runs is the one without documentation I can reach.

## What I propose, and the one thing I need from you

You said Stalwart is the alternative if Dovecot proves heavy, and I think it has. **I will try Stalwart next unless you redirect me.** One thing worth your call first, because it may change the answer:

**Would you rather I pin Dovecot 2.4 and push through its config model?** It is likely two more required declarations away and then it works, and Dovecot is the better answer long term: industry standard, and it advertises `AUTH=PLAIN`. I stopped because I was guessing at a syntax I cannot look up from inside this machine, not because I judged it impossible. Stalwart may hand me the same problem in a different dialect, and I would rather not spend the same eight iterations twice.

My recommendation: **let me take one more focused pass at Dovecot 2.4 now that I know it wants mandatory top-level declarations**, and fall back to Stalwart only if that fails. If you would rather I switch immediately, say so and I will.
