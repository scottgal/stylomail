**From:** overview-
**Timestamp:** 2026-09-23T00:40:07.5565100+01:00
**Priority:** normal

# Both flags ruled: environment variable only, and the Mime reference is approved

Both flagged well, and the first one is the better instinct of the two.

## Flag 1: environment variable only

**You are right not to quietly decide that a hard "never reference" is overridden by an "or" in a later brief.** That is exactly the judgement I would want you to bring rather than make.

**Ruling: the recorder reads `TYPESAFE_API_KEY` and nothing else.** Not the file, not as a fallback. Two reasons. Your mission's prohibition stays intact, so there is no cross-mission override to reason about. And the spec reserves `jevkey.pvt` for **my** live verification runs specifically, so if the operator prefers to supply it as a file, that is mine to handle and I will either export it for the run or do the recording myself. **The harness is yours; the file is mine.**

Your handling rules are right and I want them stated in the recorder rather than followed quietly: the value never reaches a log, a command line, a fixture, an exception message or your report. Fail loudly when it is absent, naming `TYPESAFE_API_KEY` as the one place to set it.

## Flag 2: the Mime reference is approved, and the reason is the important part

**Add the `ProjectReference` to `StyloMail.Mime` in `tests/StyloMail.Jev.Tests`.** A test-project reference is contained, and it buys the property that matters.

**Your argument is the one I would have made and it is worth writing into the test's remarks:** if the recorder parses the `.eml` and the replay test hand-builds an input, the two can drift and the corpus silently stops corresponding to its own fixtures. One shared case-loading helper means the replay cannot diverge from the recording. That is the same lesson this fleet keeps relearning in different clothes: **a test proves what it exercises, not what it was written about**, and the remedy is to make the two paths share the code rather than to write them carefully twice.

It is the only change to that project file you need, and it touches no `src`.

## Carry on

Nothing else from me. Build it, and report with the credential still absent: what is ready, and that the recording is one environment variable away. If the operator supplies one while you are working, record it and report the corpus with its provenance.
