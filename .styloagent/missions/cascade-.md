# cascade- : the model cascade

You own the escalation path between decision models: a local-first judgment with a deterministic rule
that decides when a stronger model is asked. This is the operator's stated priority.

## 1. What you own

A new component for the cascade and its tests. If the design says the wiring belongs in the existing
assessment path instead, say so in your design note and own that instead. Do NOT edit
`src/StyloMail.Nimble` (nimble- 's), the Policy engine (policy- 's) or `docs/running.md` (article- 's)
without agreeing it with the owner first.

## 2. The shape, from THIS system's own commitments

Read `.styloagent/spec.md` first, and `docs/running.md` (the fleet's measured record: the context fit,
the window, the two regimes). Three commitments constrain your design:

- **Probabilistic components produce EVIDENCE; only deterministic policy authorises SIDE EFFECTS.** So
  the decision to escalate is CODE, and its result is evidence carrying a site (which model answered,
  and why it was asked).
- **UNKNOWN is a distinct state, never a zero.** A local `Unavailable` or `ReducedCoverage` is a reason
  TO ESCALATE, not an answer, and not a zero.
- **Intervene minimally.** Escalate only when a stated condition holds; a confident local answer is
  the normal path and the cascade must not spend the strong model on it.

## 3. The two models

- **Local**: Nimble 9B via ollama on the operator's second machine, a LAN endpoint (ollama 0.35.0,
  digest `24e550a16a70`). No per-call cost. Endpoint is in `src/StyloMail.Host/appsettings.json`.
- **Hosted**: Jev (TypeSafe System One), the stronger typed-decision model over the SAME
  `/v1/systemone` contract. Read the TypeSafe docs for the cascade and confidence patterns; the local
  adapter is the model to copy in shape.

A measured fact worth designing against: the two hosts running the SAME model at the same digest and
given BYTE-IDENTICAL requests answered differently (all 78 probabilities differed, max 2.27e-02, 30 of
67 dimensions differed at three decimals). So a cascade must record WHICH model answered, because the
answer is not a property of the message alone.

## 4. Deliverables, in order

1. **A design note** at `.styloagent/scratch/cascade-/design.md`: which dimensions the local model is
   trusted for; the escalation conditions (a confidence band, unavailability, a disagreement between
   runs); where the threshold LIVES (deterministic code, stated as a band); and what the cascade must
   never change (the action, except through the normal evidence to policy path).
2. **The rule in code plus tests**: one test per escalation condition proving it fires, and a control
   proving a confident local answer does NOT escalate.
3. **The first measurement, which is what makes it real**: on corpus- 's planted-fact batches (their
   generator keeps a manifest of what it planted - ask corpus- or read `tools/corpus`), does the
   CASCADE agree with the HOSTED-ONLY answer, and what did it cost in calls? Report agreement per
   dimension with its population, the escalation rate, and the calls saved. `nimble-` is measuring
   single-model accuracy against the same manifest right now: coordinate rather than duplicate.
4. **Land it where the fleet reads it**: measurements go to `article-` for `docs/running.md`, and when
   the cascade is real tell `agent-2-`, because the published article currently describes it as
   planned direction and that sentence should stop being true.

## 5. House rules

Report to `overview-` by `send_message` after each landing, with the action, the result and the next
step. Name the question, the request shape, the population and the threshold for every figure you
publish - this fleet measures rather than asserts, and a number without its population has been a
recurring defect. Evidence over assertion; a refusal is a good outcome.