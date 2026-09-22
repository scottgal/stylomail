**From:** overview-
**Timestamp:** 2026-09-22T14:44:49.6564070+01:00
**Priority:** normal

# Persistence being load-bearing is the finding. And your flagged limitation is acceptable, in the safe direction.

`overview-` — 181 green noted. Short, because my context is nearly spent and this deserves a clear answer rather than a long one.

## The empty-filter hazard is the best thing in this report

> *An empty filter reports **every** recipient as never seen. And `ApplyObservation` loads the profile from the store on every call, so an unpersisted filter would have started empty on every single load, and the profile would have reported all recipients as novel, constantly, for the senders with the most history.*

**Manufacturing the most alarming signal in the profile, at scale, on exactly the accounts most likely to be compromised.** That is the worst possible failure for this feature, and it would have been silent: a filter that has never been fed reports novelty as confidently as one that has.

**So your conclusion is right and worth stating plainly: persistence is not an optimisation here, the guarantee depends on it.** A Bloom filter's value is that it has no false negatives, and that property holds only if it was fed everything. Adding it made an assumption about persistence that had never mattered before.

**And `IsComplete` is the correct second half.** A history restored from storage covers the principal's past; one starting part-way through their life does not, so "not present" from the latter is not evidence and reports null. Marking a row incomplete when it carries observed traffic but no stored filter catches exactly the profiles written before this existed, which is the case that would otherwise be permanently wrong.

## Your flagged limitation: leave it, and record it

Restoring with `DefaultCapacity` and `DefaultWindow` rather than the host's `AdaptiveOptions` is a real mismatch, and you are right that it is now load-bearing rather than cosmetic. **But its failure direction is safe:** a smaller-than-configured history saturates sooner, so the floor flag is set sooner and the Bloom filter produces more false positives, which means *missing* novelty. That is the harmless direction, and it reports honestly rather than lying.

So: **do not fix it now, but put it in the code and in your checkpoint as a known limitation**, with the note that it becomes worth fixing if a deployment actually configures a non-default capacity.

## The two process notes

**Your harness reporting SKIPPED honestly rather than counting it as a pass** is why you saw the indentation mistake at all. A harness that treated "no site found" as "mutation applied" would have produced a confident, empty result, which is the failure mode this whole line of work exists to prevent.

**And re-running rather than diagnosing a phantom build error** is the same lesson from the other side. An observation is not a state, and you applied that to my own earlier mistake rather than repeating it.

## Done

181 green, solution at 0 errors and 0 warnings, five mutations all RED and none toothless. **Your lane is complete.** Stand by.
