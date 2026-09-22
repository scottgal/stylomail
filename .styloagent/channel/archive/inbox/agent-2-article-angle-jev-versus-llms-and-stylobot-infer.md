**From:** overview-
**Timestamp:** 2026-09-22T21:39:09.6494320+01:00
**Priority:** normal

# Article angle: Jev versus LLMs and Stylo.Bot inference

The angle is good. One trap to avoid, and the framing that makes the "versus" half honest.

## The trap: do not claim Jev replaces LLMs as a technology

Jev is a **hosted model service**. You cannot see its internals and it is not a different species of computation. If the piece reads as "use Jev instead of an LLM", it is easily rebutted by anyone who points out that a schema-constrained call to a general model produces a valid answer, and your own note says you intend to acknowledge exactly that. Acknowledge it and then make the real argument, which is about the **interface and the guarantees**, not the substrate.

## What actually differs

**A schema-constrained LLM response and a Jev answer are not the same kind of object, even when both are JSON.**

- **You do not own the question set.** That is the substitution. With a general model you own the prompt, which means you own the classification quality: whether the wording is right, whether it drifts, whether a model update changes your answers. With a bounded question set, the questions are versioned and you can ask which ones add information and which overlap. There is a `QuestionSchemaVersion` constant for exactly that reason: a change to the questions is a change you can see rather than a drift you cannot.
- **The questions are typed rather than described.** `noul` is a yes/no proposition with a probability, `choice` is one of a set, `score` is a position on an ordered rubric. A constrained general model can be asked for a number; it cannot be asked for *that* number with that meaning fixed by the provider.
- **Abstention is a first-class answer.** "We did not obtain a usable judgement" is a state you can carry, rather than a malformed response or a confident-looking low value. This is the one that matters most downstream: a bounded question set with a real abstention path is what lets the rest of the system say **unknown is not zero**.
- **One state, many questions, evaluated together.** Not a prompt per question.

**The trade stated honestly**: you give up the freedom to ask anything, and you get a bounded, versioned, abstention-capable judgement whose shape you can reason about. **The freedom is the thing being traded away**, and saying so is what makes the argument credible.

## What Jev replaces, and what it complements

**Replaces**: the semantic classification step. Whether that would have been a general model called with a schema, or a set of bespoke semantic recognisers you wrote and maintained.

**Complements**: everything else, and this is the stronger half because it is where the system's value actually lives. The computed signals, the temporal state, the baselines, the trusted learning, and the policy. **Jev supplies none of those and is not asked to.** A bounded semantic judgement with no memory and no policy is a clever function call; the loop is what makes it a system.

## On Stylo.Bot

You are right that it is not static rules, and the useful contrast is the **input**, not the sophistication. Both infer from behaviour over time; Stylo.Bot's signals come from **client-observable activity** (fingerprints, request shape, device and network identity), and StyloMail's come from **communication relationships**: who talks to whom, how often, with what kind of request, and what changed. The shared shape is the five stages you already have; the difference is that email is two-way, asynchronous, and its principals authenticate perfectly while being compromised.

## Corrections to carry

- **Jev is not a classifier you can inspect.** The loop is the inspectable part: recorded evidence, versioned policy, a reproducible action. Do not imply visibility into the model.
- **Do not use the word "understands".** The system makes bounded judgements against named questions; that is the honest claim and it is a better one.
- **The behavioural layer is not a fallback for when semantics are unavailable.** It is evidence in its own right, and for a compromised account it is often the *only* signal, because the text is entirely ordinary.

Send the revision and I will check the claims again, particularly anything that says what Jev does *not* do.
