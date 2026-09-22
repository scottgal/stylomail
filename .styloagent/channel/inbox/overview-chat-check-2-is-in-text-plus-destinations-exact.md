**From:** chat-
**Timestamp:** 2026-09-22T20:56:22.8824210+01:00
**Priority:** normal

# chat- check 2 is in: text-plus-destinations, exact match, and the fifty-first escalates by construction

Check 2 is in, red-first, and the pieces fit the way you ruled.

## The fingerprint

**Chat's fingerprint is over the normalised text plus the link destinations**, so the detector's fingerprint comparison is the text comparison and the components ride along. That gives you the property you wanted **by construction rather than by threshold**: identical words with a different destination produce a different digest, so the fifty-first message escalates because it is a different message rather than because a similarity score missed it.

**Normalisation is trim plus collapse whitespace, and case is deliberately preserved.** Folding case would make "Invoice attached" and "invoice attached" duplicates, and the difference between them is exactly the sort of thing a duplicate check should not be deciding alone.

**Components are length-prefixed rather than joined by a separator.** A separator needs a character that cannot occur in free text, and there is none, so two messages could otherwise collide by splitting their content differently across the boundary.

**An empty text and no destinations is `ComponentCount = 0`, which cannot agree with anything**, so a captionless image is not a duplicate of every other captionless image. Same rule as the fingerprint guard, applied to the basis the fingerprint is now computed from.

## The check

**Dismisses on a match, otherwise returns null so the next check runs.** It reads the campaign's **Availability** rather than the presence of a signal, which is what keeps "we looked and it is clean" apart from "we did not look". When no campaign is configured the check does not run and does not claim to have looked.

**It starts at exact agreement, and that is written in as a decision**: "near" is deliberately not attempted yet, exactness is the conservative end of the same axis, and loosening needs the dismissal counts behind it.

## One gotcha the tests found

**The window excludes a match against the same assessment id**, which is how a message never matches itself. My first test gave two different messages the same event id and saw no match, which was the window being right and my test being unrealistic: triage has no assessment id yet, so it uses the event id, and two different messages carry different ones.

## Verified

Build 0 warnings, 0 errors. **1456 passed, 0 failed**, Assessment 150 measured rather than inferred. Sweep signals clean.

Next: check 3, links and homographs, which reuses the Core analysis and raises no new question. Then behaviour, then the drain integration with the separated write, then the counts.
