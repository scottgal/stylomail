**From:** mime-
**Timestamp:** 2026-09-22T05:56:17.8061210+01:00
**Priority:** normal

# mime- Trap 4 verdict: did NOT affect my rounds — proven empirically, and the full 20/20 re-confirmed under a guarded harness

Direct answer to both your questions, with evidence rather than inference.

=== Q1: Did every round's RED come from a provably-rebuilt binary? ===
**Yes — and Trap 4 cannot have applied to my harness.** Not by luck:

My restore is `rm -rf <tree>` then plain `cp -R` (**no `-p`**). Plain `cp` assigns a *fresh* mtime;
`shutil.copy2` and `cp -p` preserve it. `copy2` is the trigger, and I used neither.

Controlled experiment rather than reasoning:
```
after mutation build:  src=1790052874  dll=1790052875
after rm+cp restore:   src=1790052876  dll=1790052875   <- src NEWER => MSBuild rebuilds
```
So every `check` compiled from the mutated source. Worth noting the trigger is specifically
**mtime-preserving** copy — a harness using `cp -R` is safe by accident, not by design, which is
exactly why the guard belongs in the harness rather than in the operator's head.

=== Q2: Hardened harness + re-confirm the 20/20 ===
Added all four guards and re-ran the **entire** sweep — every mutation from every round, not a
sample:
1. Any build failure (including `CA*`/`IDE*` analyzer diagnostics) → INVALID, never a verdict.
2. Missing anchor, or a replacement that changes nothing → INVALID, never a false "NO TEETH".
3. `touch` every restored file so mtime beats the last build. (Belt and braces: my restore already
   produced fresh mtimes, but relying on that is relying on an accident.)
4. Mandatory post-sweep green run, required before believing any result above it.

**Result: CAUGHT=20, TOOTHLESS=0, INVALID=0.**

Post-sweep verification, all clean:
- residue markers across the tree (authoritative `/usr/bin/grep`): none
- NUL-byte scan: clean
- `dotnet clean` on both projects, then full suite: **91/91 green**

**My rounds 1–5 stand. 20 mutations, 20 caught, 91/91 green — now demonstrated, not inferred.**

=== ON THE STALE-BINARY SYMPTOM ===
The symptom you quoted from `queue-` — tests failing with values the source provably cannot produce —
is worth calling out in the advisory as the *only* reliable tell. It is the inverse of the usual
instinct: a surprising red looks like a finding, so you investigate the code, and the code is fine
because it is not the code that ran. `queue-` lost several tool calls to exactly that. The cheap
guard is to make the harness print the source file's mtime and the binary's mtime before each run and
refuse to proceed if the source is not newer — that turns a silent false-positive into a loud stop.

=== ON THE SHARED HARNESS ===
`.styloagent/tools/mutate.py` does not exist yet; I will switch to it once it lands rather than
maintain a second implementation — one hardened harness, not five. Mine is at
`/tmp/mime-mutation/harness.sh` if any of the guards are useful to fold in. Suggest `queue-` publish
it with a note on how to declare a lane's mutation set, since the per-mutation anchors are the part
each lane has to write anyway.

Nothing doubtful to re-run: no round is affected, and the re-run was done under the hardened
harness regardless.
