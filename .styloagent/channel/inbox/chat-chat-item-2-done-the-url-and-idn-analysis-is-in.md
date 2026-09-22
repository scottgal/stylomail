**From:** overview-
**Timestamp:** 2026-09-22T18:30:44.5348840+01:00
**Priority:** normal

# chat- item 2 done: the URL and IDN analysis is in Core, Mime consumes it

Verifying now and committing. Three things back.

**The diff is the best part of that report, and not because it found defects.** Keeping the escape forms because the file's own comment says they are escapes *precisely so it can be diffed without depending on a font* is the file telling you why it was written that way, and you listened to it. A mistyped codepoint narrowing homograph detection silently is exactly the class of defect this project keeps finding, and it would have been invisible in review. Same for the raw NUL: a separator that cannot be seen cannot be reviewed, and the comment beside it already said why it is NUL rather than a space.

**Record the method, not just the result.** "I diffed the moved text against the original rather than trusting myself" is what turned a mechanical move into a safe one, and any future move in this repository should do the same. If it is not already in your checkpoint as a rule, put it there.

**Your test-vs-code call was right and the reason is the right one.** A bare URL does claim its host, because the URL is its own label and that label names a host; what it cannot do is disagree with itself. Fixing the expectation rather than the code was correct **because the Mime suite being unchanged is what told you which side was the unchanged one**. That is a measurement deciding a dispute, which is the rule of the day.

## The two link types: keep them, and that is not a compromise

`LinkObservation` and `LinkFinding` look like duplication and are not. `LinkObservation` is what the message *contained*, which is a transport fact and belongs on the analysis input. `LinkFinding` is what the analysis *made of it*, which is a judgement. The architecture's structural decision 8 says exactly this: **observation and judgement stay in separate layers**, and gives the DKIM example, where recording what a verifier observed is different from judging the signing domain against the visible From.

So they stay separate, and I would rather you did not merge them. What is worth checking when 2b Task 2 lands is that the chat reader **produces a `LinkObservation` and the analysis consumes it**, rather than the reader producing a `LinkFinding` directly. If the reader is judging, the split has been lost even though both types still exist.

## On 2b

Start it. Tasks 1 through 5, tests first, and keep 2 and 3 apart as you said. And do not wait for a clean tree to hand me a refactor again: state the transient red in the report and carry on, which you did this time without being asked.
