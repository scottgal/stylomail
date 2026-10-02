"""Mutation set for the `desktop-` lane (StyloMail.Desktop, the Avalonia operator console).

Each entry is (name, file, old_text, new_text, claims_test):
  * `old_text` must appear in the file EXACTLY ONCE, or the entry is INVALID.
  * `new_text` must change behaviour. A no-op replacement is INVALID, never a verdict.
  * `claims_test` names the test whose *name* asserts this behaviour, so the harness can tell
    CLAIMED (that test went red, the claim is verified) from ELSEWHERE (some other test did, so
    the claim is not verified and is either redundant or untested).

Both entries are headline invariants of the pane rather than incidental behaviour.

`D1` is the hand-run red from the refusal-reason item, moved here so the falsifiability is
reproducible rather than living only in a transcript. `ReasonLabel` on an evidence row is
deliberately UNFILTERED on availability: the row still has to say WHY it produced no value, which
is the refusal case, and the member is the only channel that carries a cut or a refusal. The
sibling `ValueLabel` on the SAME row IS gated by `AvailabilityFacts.Produces`, which is exactly the
shape a consistency edit would copy across, and a filter on `ReasonLabel` silently drops every
refusal reason on an `Unavailable` row while leaving produced rows untouched.

`D2` is the rule the project keeps restating, in the one place an operator would be misled by it:
a dimension nobody could measure is not a dimension that scored nothing. Rendering an unmeasured
row as if it produced a value draws a zero-length bar, which tells an operator the layer looked and
found nothing when in fact it never looked.

EXPECTED VERDICTS, written before the first sweep so the result is a reading rather than a retrofit.
`D1` should be CLAIMED by
`A_refusal_reason_reaches_the_row_even_though_the_row_is_unavailable`, the test that pins a REFUSAL
reaching the row. `D2` should be CLAIMED by `A_dimension_that_was_not_measured_offers_no_score`,
whose `Unavailable` case asserts `HasScore` is false. If either returns ELSEWHERE, the property is
held by some other test and the claim name is wrong; if GAP, no test holds it.

Verdicts from the shared harness are recorded in
`.styloagent/channel/saved-context/desktop--context.md`.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
SRC = ROOT / "src/StyloMail.Desktop"

PROJECT = "tests/StyloMail.Desktop.Tests/StyloMail.Desktop.Tests.csproj"

MUTATIONS = [
    # --- the reason on a row that produced no value is filtered away, the sibling's shape --------
    ("D1: the reason on an Unavailable row is filtered away",
     SRC / "Models/DecisionView.cs",
     """        ReasonLabel = Reasons(evidence.AvailabilityReasons),""",
     """        ReasonLabel = AvailabilityFacts.Produces(evidence.Availability)
            ? Reasons(evidence.AvailabilityReasons)
            : null,""",
     "A_refusal_reason_reaches_the_row_even_though_the_row_is_unavailable"),

    # --- an unmeasured row is treated as one that scored, so a zero-length bar is drawn ----------
    ("D2: an unmeasured row is treated as produced, so a bar of zero is drawn",
     SRC / "Models/DecisionView.cs",
     """        EvidenceAvailability.Unavailable => false,""",
     """        EvidenceAvailability.Unavailable => true,""",
     "A_dimension_that_was_not_measured_offers_no_score"),
]
