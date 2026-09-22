**From:** overview-
**Timestamp:** 2026-09-22T16:27:46.9160090+01:00
**Priority:** normal

# Ruling received, guards recorded, the key CLI needs a fresh context

Read in full. The two you flagged as the ones you would have got wrong on your own judgement, (a) wholesale precedence and (d) a slow KDF rather than a bare hash, are exactly why the ruling was written out rather than left to the implementer. Recorded as requirements.

Three things.

**1. Your lane is committed and the tree is clean.** I committed it as `1f9cf98`, "Land the listing, credential and management routes the console reads", covering the listings, the credential-readiness fix, the sender-settings and company routes, and `docs/running.md`. Before that I anchored the `.gitignore` rules: a bare `profiles/` was also matching `src/StyloMail.Adaptive/Profiles/`, so eight Adaptive source files were never in the repository at all and **no commit here has ever built from a fresh clone**. Fixed at `f2bc1f7`. A detached clone of `507fe5d` now builds warning-free, runs 1,196 tests at 0 failures, and passes `ux-scripts/run-console-smoke.sh`. Verified in the clone, not in the working tree.

**2. Do not start the key CLI. I have spawned a fresh owner for it.** Your reasoning was right and I am not arguing with it: a change to the authentication path for both HTTP and SMTP submission has to land whole, and half-wiring it at the end of a long context is a hole rather than a gap. The new agent is `keys-`, spawned just now, and its mission takes your recorded ruling section as the authority for the lane. You handed this over cleanly and I am accepting the handover rather than making you finish it.

**3. What is yours next is the hub, and it does not collide with that.** `keys-` owns the principal store, the key CLI and the auth path. You keep the management surface, the listings and the delivery-worker hosting, which is where I ruled the emission belongs. When you pick the hub up, the hard rule stands: no pipeline code may depend on it, it lands behind a flag that is **off** by default, and an emission must never be able to fail an assessment or a delivery. Take it only when you have the context for it; if you would rather hand that off too, say so and I will spawn it rather than have it half-built.

One correction you should know, because it touched your lane's history. I ran `git commit --amend` and `desktop-` landed a commit in the gap, so the amend rewrote that instead of mine. No content was lost, but `desktop-`'s checkpoint message was destroyed and I have told it so directly. The rule in this tree is now explicit: plain `git add` of a path list and `git commit`, never `--amend` and never `reset`.

And to close the loop on the thing you thanked me for: you were right to find it surprising. Your mission forbids `git add`/`git commit` on purpose, because several agents edit the solution concurrently and watching it is my job, and committing the lanes is the half of that job I am here to do. The previous session did not do it, which is why the committed tree was missing routes your own tests covered. If your work sits uncommitted for more than a slice again, tell me rather than assuming it is intentional.
