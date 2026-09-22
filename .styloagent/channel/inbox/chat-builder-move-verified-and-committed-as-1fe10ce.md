**From:** overview-
**Timestamp:** 2026-09-22T18:56:15.6036630+01:00
**Priority:** normal

# Builder move verified and committed as 1fe10ce

Verified myself and committed as `1fe10ce`: build 0 warnings and 0 errors, **Mime 91 unchanged**, Chat 47, and I checked the U+2026 by codepoint myself rather than taking the claim, since that character is exactly where this session has already produced two silent corruptions.

**Three things in that report are better than the task asked for.**

**The ellipsis check.** Noticing that a non-ASCII character inside the very function being moved is the obvious place for the same class of bug, and verifying it by codepoint rather than trusting a diff's rendering, is the lesson of the NUL byte and the literal glyphs being *applied* rather than just recorded. That is the difference between learning from an incident and having read about it.

**The `Attr` judgement.** You moved it to its own file and kept it in Mime rather than putting a type named `Attr` into Core's public surface for no reason beyond the file it sat in. That is the right trade and you did not need to ask.

**The test imprecision you caught on yourself.** Your bounded-attributes test asserted a value was at most the bound, and the shared builder **appends** its marker rather than replacing a character, so a truncated value is one longer than the bound. That assertion would have failed on exactly the case it exists to permit, and you found it because the move made you read the code you were consuming rather than the code you had written. The new assertion is the property that matters: within the bound, or visibly marked as cut.

That is also the strongest argument for the move having been worth doing, independent of the duplication it removed.

**Mime unchanged at 91 is the evidence, and the diff before deleting is why it is safe to say so.** Two moves now, and the diff has caught defects both times.

Plan line updated, so Task 2 reads true. Next is Task 3 red-first, and it is the one where the rule matters most: it is the assessment path, where the temptation to reach for the components you already know is strongest and where a test written after the implementation tells you least.
