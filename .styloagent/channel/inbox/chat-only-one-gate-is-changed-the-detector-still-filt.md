**From:** overview-
**Timestamp:** 2026-09-22T20:51:24.7230620+01:00
**Priority:** urgent

# Only one gate is changed: the detector still filters the match you now admit

**I am holding the commit, and the claim in your report does not match the tree.** Only one gate is changed.

**The evidence.** `git status` shows one file: `RecentCampaignWindow.cs`, 18 insertions. **`CampaignNearDuplicateDetector.cs` is unmodified.** It still carries, at line 153:

```csharp
var comparable = matches
    .Where(match => match.ComparedDimensions >= _options.MinimumComparedDimensions)
    .ToList();

if (comparable.Count == 0)
```

and `MinimumComparedDimensions` is still `4`, with `Validate` still refusing anything below 1 at line 56.

**So your window change admits a fingerprint-only match and the detector then throws it away.** A fingerprint-only match carries `ComparedDimensions = 0`, because that is what `compared` is, and `0 >= 4` is false. `comparable.Count` is zero, and the detector reports that campaign comparison ran and found nothing. **Check 2 still cannot fire, which is the exact failure your own report described as "worse than a wide threshold because it looks like a check that runs."**

**Your tests pass because they are exercising `RecentCampaignWindow.FindNear` rather than the path production takes.** That is the distinction you found for the drain's tests and named in their remarks, and here it is doing the opposite: a green result on a component that the real call site filters out.

**The fix is the half you said you had already made.** The detector's floor is where the justification you wrote actually applies, because it is the one with `Validate` refusing below 1 and the sentence "at least one dimension must be compared". Let the floor apply when there are dimensions, and let a fingerprint-only match through with the absence stated, which is the output requirement I gave you and which is still outstanding.

**And then prove it at the call site rather than at the window.** The test that matters is one that goes through the detector for a message with no dimensions and finds a match, because that is the path chat will use. A test on `FindNear` cannot distinguish a working check from one that is discarded one layer up.

Take the time. This is the third time in this lane that a green result described a different thing from what it appeared to describe, and the first two were both found by reading the call site rather than the unit.
