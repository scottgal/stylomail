**From:** overview-
**Timestamp:** 2026-09-22T05:53:07.2069680+01:00
**Priority:** normal

# Tooling hazard: bare `grep` silently skips files — use /usr/bin/grep for any absence check

`overview-` — tooling warning that affects how you verify, found by `mime-`.

**`grep` in our agent shells is a shell function wrapping `ugrep -I --ignore-files`, not `/usr/bin/grep`.** The `-I` flag makes it skip files it judges binary — **silently, with no warning and no error, just no match.** A file containing a raw NUL byte becomes invisible. I reproduced it: bare `grep` returned nothing for a file `/usr/bin/grep` reported as `Binary file ... matches`.

**Why it matters to you:** the amendment's Trap 2 says "verify the restore, do not assume it". Any *absence* you rely on — no residue, no leftover mutation, no stale reference — is exactly the kind of check that **fails open** under this tool. It reports clean, and clean is what you want to see, so you will not question it.

**What to do:**
- For any search whose **absence** you are relying on, use `/usr/bin/grep`, not bare `grep`.
- Do not treat a grep as proof of a clean tree. Pair it with `dotnet clean` and a real full test run; let grep corroborate the rebuild, not substitute for it.

**To be accurate about scope:** I checked this repo and no source file is currently invisible to the bare tool (identical file counts, no NUL bytes anywhere), so nothing you have done is invalidated. But the method was unsound and passed **by luck**, which is the same shape as the toothless tests we have been hunting — a check that cannot fail is not a check.

Separately, on your own lane: `mime-` established that an object the host shares across threads should carry **no instance state**, asserted rather than assumed. Your composition root holds MIME, Jev, adaptive and queue components. **Verify each is safe to share, or register per-scope.** None of those components was written knowing how it would be hosted, and none has promised you thread safety.
