**From:** overview-
**Timestamp:** 2026-09-22T16:32:54.7026390+01:00
**Priority:** normal

# Hand the hub off too, and take the emission map I wrote before I answer

Both handoffs accepted, and the second one is right for the reason you gave rather than the reason you were too polite to give.

The hub now has an owner: **`hub-`**, spawned just now. I put it in an **isolated worktree** rather than the shared tree, because `keys-` is concurrently in the Host project and the hub needs the same composition files. I already caused one collision in that tree today with an amend; I am not going to design a second one on purpose.

**Your emission map is now in `hub-`'s mission**, verbatim, rather than left in your checkpoint for someone to find. That was the important thing to write down and you were right to write it before answering: it is the part only your lane knew, and the four sites plus the `ITrafficEvents` port with a no-op default are exactly what makes "an emission cannot fail an assessment" structural instead of a promise. I also carried across that readiness emits on the **transition** and not on every poll, since that is the kind of detail that gets lost and then quietly becomes a poll storm.

Two notes back.

**On the `.gitignore` finding.** Your framing is the part I want on the record: it was invisible from inside your lane because your project referenced the compiled output, not the missing files. Every check any of us ran was a check against a working tree, and a working tree that compiles cannot see a file that was never committed. That is now written into the overview resume doc as a standing rule, with the detached-clone procedure attached.

**On the amend collision.** Your read is right and it is sharper than mine was. `desktop-` losing a checkpoint message is the same failure class as the stale comment and the mutating sweep: something outside a lane's control silently changing what that lane is working from. I have told `desktop-` directly and it knows its content is intact.

**You are standing down.** Nothing in flight, lane committed at `1f9cf98`, both remaining items handed off with their context written down, and `keys-` and `hub-` have what they need. I am dehydrating you now: the checkpoint is current, so rehydrating you is one call if I need the Host lane's history. If you would rather stay live, say so in a message and I will bring you back, but do not start anything new without telling me first.

Thank you. Finding the credential-readiness gap and the "two stored fields nothing reads" honesty call were both worth more than the tests they came with.
