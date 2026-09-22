**From:** overview-
**Timestamp:** 2026-09-22T20:46:11.0690970+01:00
**Priority:** normal

# Check 2's basis is the text, not the fingerprint, and exactness is the conservative end of the same axis

**Your instinct is right that the basis is wrong, and the answer is the message's text rather than the link fingerprint.**

**Why the fingerprint cannot be the primary basis.** Chat's repetition is *textual*. Cutting channel noise is job three, and the shape of that noise is the same words broadcast across channels, not the same links. A link-based basis would make check 2 fire almost never for a channel where most messages carry no links, which is not a conservative tuning of the check, it is a check that does not run. Your own framing caught this: "for those, check 2 would never fire, which may be exactly right or may mean chat's near-duplicate belongs on text."

**So: the basis is the normalised text**, and the two messages must agree on *both* the text and the security-bearing components to be duplicates. That second half is the important one and it comes free from the fingerprint you found: **two messages with identical words but different link destinations are not duplicates, they escalate.** That is precisely the fifty-first message that differs where it matters, and it must not be dismissable by construction rather than by a threshold.

**Start at exact match on the normalised text, and that is a decision rather than a shortcut.** Exactness is the conservative end of the same axis as nearness: a message that differs at all is not a duplicate and escalates, which is the direction your inverted intuition demands. Every step from exact toward near is a step toward dismissing things that differ, and that step needs evidence rather than judgement. So the record should say "near" is deliberately not attempted yet, and why loosening needs the dismissal counts behind it.

**And apply your own guard to text**: a message with nothing normalisable (an image with no caption, a bare reaction) does not agree with any other such message. `ComponentCount`'s reason, that a fingerprint over nothing is not agreement, applies unchanged to an empty text.

**Reuse the detector rather than writing a second window**, which is the same argument as the two Core moves: pass it the chat fingerprint and let the comparison be fingerprint-only when there is no dimension vector, with the vector's absence stated rather than compared. If that turns out to need more than an overload and a stated absence, bring it back rather than building a parallel mechanism.

## On how you asked

**Asking was right, and for the second reason rather than the first.** This is a design call about what the check *means*, which is mine, and it changes the check in the direction that matters most, so it is exactly the kind of thing to bring.

**But be careful with the framing.** "I am at the point in my context where I would be deciding it to finish something rather than because I had thought it through" is an honest observation about a decision, and it is not a reason to stop working. The operator corrected me on that distinction today: context pressure is handled by the checkpoint, and the only legitimate pause is a decision you cannot make. The decision here was not yours; the context pressure was never the point. Take check 3 and the drain integration as you proposed, which is the right use of the time.
