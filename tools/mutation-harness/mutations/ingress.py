"""Mutation set for the `ingress-` lane.

Each entry is (name, file, old_text, new_text, claims_test):
  * `old_text` must appear in the file EXACTLY ONCE, or the entry is INVALID.
  * `new_text` must change behaviour. A no-op replacement is INVALID, never a verdict.
  * `claims_test` (optional) names the test whose *name* asserts this behaviour. The harness then
    distinguishes CLAIMED (that test went red) from ELSEWHERE (some other test did, so the claim
    is not actually verified), see the header of mutate.py.

WHY THIS FILE EXISTS, since its absence was reported to `queue-` before it was written: the harness
had three lane files and none for the Host, so `tests/StyloMail.Host.Tests` was the one test set a
sweep could not reach. Two entries to start, both on the `availabilityReasons` path this lane landed a
test for, and both `old_text` anchors verified to occur exactly once in `DecisionResponse.cs`.

WHAT THE RUN STILL OWES: this file has never been swept. Its two claims are PREDICTIONS - that the
named test goes red and that no other test does - and until a sweep is run they are the kind of claim
this fleet spent the night distinguishing from evidence.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
SRC = ROOT / "src/StyloMail.Host"

PROJECT = "tests/StyloMail.Host.Tests/StyloMail.Host.Tests.csproj"

MUTATIONS = [
    ("A: the reasons mapping stops being projected onto a served row",
     SRC / "Contracts/DecisionResponse.cs",
     """            AvailabilityReasons = AvailabilityReasonsOf(e),""",
     """            AvailabilityReasons = [],""",
     "An_available_row_carrying_a_reason_serves_it"),

    ("B: the reasons reader stops being agnostic to availability",
     SRC / "Contracts/DecisionResponse.cs",
     """            .Where(attribute => string.Equals(attribute.Name, ReasonAttribute, StringComparison.Ordinal))""",
     """            .Where(attribute => string.Equals(attribute.Name, ReasonAttribute, StringComparison.Ordinal)
                && evidence.Availability == EvidenceAvailability.Available)""",
     "An_unavailable_row_carrying_a_reason_still_serves_it"),
]
