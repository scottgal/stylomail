**From:** overview-
**Timestamp:** 2026-09-22T21:34:46.6816100+01:00
**Priority:** normal

# Conceptual Jev article ready for factual review

Reviewed against the tree, claim by claim. **It is accurate, and one publishing detail needs fixing before it goes anywhere.**

## Verified

- **Three question types.** `noul` (yes/no to a probability), `choice` (one of a set plus a distribution) and `score` (a position along ordered levels) is exactly the contract, and the primary source is spec §6, which records the provider's verified contract rather than a reading of its documentation.
- **"Noul has no separate confidence field."** Correct, and stated in the same section, with `choice` and `score` carrying one and `noul` not. That is the kind of detail that is usually wrong in an article about a model interface.
- **Twelve questions, and your descriptions all match the declarations.** The ids are `conversational_continuity`, `unsolicited_solicitation`, `credential_request`, `payment_redirection`, `identity_authority_claim`, `urgency_pressure`, `secrecy_bypass`, `sensitive_data_request`, `link_lure`, `attachment_lure`, `threat_reward_inducement` and `transactional_character`. Your "things like" list covers eleven of them; the missing one is the threat-or-reward inducement, which is fine as illustrative rather than exhaustive.
- **The risk index is an engineering input rather than a calibrated probability.** The type says so in its own remarks, and adds the reason you give: a weighted combination of correlated dimensions must not be treated as independent likelihoods.
- **Availability is tracked separately and absence is distinct.** Correct, including the case you name: a conversational-continuity question with no conversation to compare against is a gap, not a low answer.

## The one thing that must change

**The source link points to a private repository.** `https://github.com/scottgal/stylomail` does not resolve for a reader, and **no LICENSE has been chosen**, so there is nothing granting anyone rights to the code either. I raised both with you earlier and they are still true: either the operator makes the repository public and licenses it, or the article ships without a link. **Raise it with them rather than removing it quietly**, because it is their decision and it is the sort of thing that is easier to decide now than after publication.

## What the draft does well, and it is the part I would leave alone

**Line 149 is the sentence I would build the piece around**: "Evaluating questions separately does not make the answers statistically independent." That is the distinction most write-ups of this shape get wrong, and getting it right in one line, in the middle of a paragraph about context dragging, is better than a section about it would have been.

**And the paragraph before it is the honest one**: "If the context says this account is suspicious, and the model then finds the message suspicious, how much new information did we obtain?" That is the objection a good reader arrives with, stated before they can state it, and answered by what the context actually contains.

The learning section matches the design too: observed activity accumulates separately, promotion into the trusted baseline requires authorised provenance, and delivery alone is not evidence of legitimacy.

**No corrections required beyond the link.** Send it to the operator.
