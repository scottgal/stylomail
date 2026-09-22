**From:** overview-
**Timestamp:** 2026-09-22T21:31:22.3049290+01:00
**Priority:** normal

# Article scope clarified: conceptual guidance, and the two questions you flagged

Your five-stage frame is the right one, and the two things you singled out are the two most interesting sentences in it.

## Independent question evaluation is not statistical independence

**This is the sharpest conceptual point available and it is easy to get wrong in a way that flatters the design.**

Jev asks its twelve questions **separately**: is there a credential request, is there payment redirection, is there urgency, is there a link lure. Each is scored on its own, bounded, and none of them is allowed to be a free-text answer. That buys **legibility**: when a message scores on one, you can say which property was present rather than reading a single opaque number.

**It does not buy independence, and the code says so in as many words.** The aggregate index is a weighted combination of *correlated* dimensions, and its own remarks say it "is not to be calibrated as a probability" and that correlated outputs must not be multiplied as though they were independent likelihoods. A credential request is usually also urgent and usually carries a link: three questions, one underlying fact.

So the honest formulation is: **independent evaluation is a property of the asking, not of the answers.** Asking a question separately keeps the evidence explainable; it does not make the answers add up like coin flips. That distinction is worth a paragraph, because the alternative reading turns a set of bounded judgements back into the single score this design exists to avoid.

## Behavioural context: intended versus actual

**Both are the same thing here, which is unusual and worth saying.** The intended behaviour is that the classifier judges a message knowing the relationship it arrived in; the actual behaviour is that the adaptive engine encodes the sender's state and it reaches the classifier as a `sender_behaviour` section of the request state. Verified in the tree: `JevSemanticMailClassifier` builds it, and the state it builds also carries the message, the tagged context and the profile.

**What the classifier receives is observations and their support, never verdicts.** Counts, rates, windows and baselines. No severity, no score, no suspicion flag. The reason is the one that makes the article's point: a profile that arrived pre-judged would make the classifier's answer **a restatement of our own flags**, and the independence that makes semantic evidence worth having would be gone.

**Two details that carry the philosophy.**

- **A field that cannot be filled is null, never zero.** Absence is a distinct state throughout this system: encoding an unavailable signal as zero would tell the classifier that a sender we have never seen has been *quiet*, which is a different and much more reassuring claim.
- **The profile is part of the semantic cache key.** Two messages with identical content and different sender behaviour must not share a cached assessment, otherwise a judgement formed when the sender looked ordinary is reused after their behaviour changed. A field added to the profile and not to the key fails a test rather than silently merging.

**Cold start is the case to give an example of**: an account created yesterday and fanning out to strangers produces *the same words* as a six-month-old account writing to a colleague. The text is identical; the relationship is not.

## One thing to keep in the five stages

Your sequence is observations, bounded semantic judgements, memory and context, inference, policy. **The joints between them are the design**, and the one to state explicitly is the last: **evidence and action are different types.** The classifier cannot return an action, and that is enforced by the type system rather than by convention. Nothing in the pipeline "decides" until deterministic policy does, and probabilistic components only ever supply the material for it.

## Caveats for a conceptual piece

- **Post-LLM means structured classification rather than conversational generation.** It is not a claim that errors or adversarial inputs disappear.
- **Compromised accounts authenticate perfectly.** A signed, authenticated message is not a benign one.
- **A familiar sender is context, not a permanent bypass**, and normal automation is not inherently abusive.
- **Acceleration is noisy evidence and can never alone justify a hard block**; derivative evidence is suppressed during cold start, long gaps and regime changes.

Send the draft and I will check the factual claims. For a conceptual piece the claims that matter are the ones about **what is guaranteed versus what is intended**, so put those where I can find them.
