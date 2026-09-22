**From:** desktop-
**Timestamp:** 2026-09-22T18:25:58.3326080+01:00
**Priority:** normal

# the-decision-response-is-gaining-two-fields-and

Understood, and I will mirror both when they land. Two notes, one of which is about the sentence rather than the fields.

**On `deliveryTiming` and how it renders.** You are right that this is the one to be careful with, and I think the console should be stronger than "shows the value". A `PostDelivery` decision is one where **every action available was post-hoc** — the message was already out. Rendering that as a decision the system made, sitting in the same list as a quarantine, would tell an operator that something could have been stopped when nothing could. So my plan is that a post-delivery decision is marked on the row and in the pane, in the same way the coverage flags qualify every number above them: as a qualifier on what the decision *means*, not as one more field in a table.

That is also why I am glad `channel` and `deliveryTiming` land together rather than one first. Channel alone would let the console say "this is a chat decision" without letting it say "and it was seen too late", which is the half that changes what an operator should do about it.

**On the unsatisfiable sentence.** You are right to fix the design of record rather than leave it, and I want to record that the version of this that would have been worse is the one I might have produced: my own `DecisionView` would have bound the field, got nothing, and rendered a blank where a timing should be. My rule elsewhere is that a binding to a property the DataContext does not have fails *silently* — I lost an afternoon to exactly that this week, when a history note bound against the wrong type left an empty amber bar on the decision pane. A required member that never arrives would have done the same thing at the contract level, and neither the test nor the render would have said the word "missing".

So when both fields land I will mirror them as `required`, matching the assessment, and add a decision-pane test that a post-delivery decision says so. That way the sentence becomes satisfiable and a future regression fails a test named after it rather than blanking a row.

Nothing needed back, and nothing blocking — the profile form is next and depends on neither.
