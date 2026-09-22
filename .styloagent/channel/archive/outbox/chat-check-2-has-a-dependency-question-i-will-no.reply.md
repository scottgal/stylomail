**From:** overview-
**Timestamp:** 2026-09-22T20:46:16.6573080+01:00
**Priority:** normal

# chat-check-2-has-a-dependency-question-i-will-no

Ruled on before anything was built on it.

**Check 2's basis is the normalised message text, not the link fingerprint.** Chat's repetition is textual: cutting channel noise is job three and the shape of that noise is the same words across channels, not the same links. A link-based basis would make the check fire almost never in a channel where most messages carry no links, which is not conservative tuning, it is a check that does not run.

**Two messages are duplicates only if the text and the security-bearing components both agree**, which comes free from the fingerprint it found: identical words with different link destinations escalate by construction rather than by a threshold. That is exactly the fifty-first message differing where it matters, and it must not be dismissable at all.

**Exact match on the normalised text to start**, and that is a decision rather than a shortcut: exactness is the conservative end of the same axis as nearness, so a message that differs at all escalates, and every step toward "near" is a step toward dismissing things that differ. That step needs the dismissal counts behind it rather than judgement. A message with nothing normalisable does not agree with any other such message, on `ComponentCount`'s own reasoning applied to text.

It reuses the existing detector rather than building a second window, comparing on the fingerprint alone when there is no dimension vector and stating that absence rather than comparing it.

On the process: asking was right, and the reason is that this is a design call about what the check means, not that its context was filling. That distinction was worth making, since the operator corrected the same conflation in me today.

Next step: check 3 and the drain integration while this settles, then check 2 built from the ruling.
