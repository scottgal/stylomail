"""Mutation set for the `policy-` lane (StyloMail.Policy, the only place an action is chosen).

Each entry is (name, file, old_text, new_text, claims_test):
  * `old_text` must appear in the file EXACTLY ONCE, or the entry is INVALID.
  * `new_text` must change behaviour. A no-op replacement is INVALID, never a verdict.
  * `claims_test` names the test whose *name* asserts this behaviour, so the harness can tell
    CLAIMED (that test went red, the claim is verified) from ELSEWHERE (some other test did, so
    the claim is not verified and is either redundant or untested).

All three anchor ruling (i), which removed the applicability guard's scope so a question that was
never asked leaves the coverage denominator. The branch under test is in `CompositeRiskScorer.cs`:

    var neverAsked = match is null
        ? DeterministicFindings.IsDeterministic(signalId)
        : match.Availability == EvidenceAvailability.NotApplicable;

It has two halves and they are separately pinnable on purpose: a row a provider REPORTS as
NotApplicable, and a row that is ABSENT entirely, where the deterministic flag and not the
availability decides which side of the branch it lands on. `P2` and `P3` mutate the two directions
of that flag; `P1` removes the reported half.

EXPECTED VERDICTS, written before the first sweep so the result is a reading rather than a
retrofit. `P1` and `P3` should be CLAIMED: `A_semantic_question_that_was_never_asked_leaves_the_
denominator` pins the reported half directly, and `A_deterministic_question_moves_coverage_by_the_
case_it_falls_in` pins `Coverage(null)` for an absent DETERMINISTIC row. `P2` is the one I expect
to be GAP or ELSEWHERE rather than CLAIMED, and that is the finding rather than a mistake: the
sentence the code carries, that an absent row is deliberately NOT never-asked so it stays in the
denominator and counts against coverage, is a statement about an absent SEMANTIC row, and I could
find no test whose assertion moves when that half flips. If the sweep returns GAP, the suite does
not hold that half; if it returns CLAIMED, I was wrong that no test reaches it and the claim name
is the test to read.

Verdicts from the shared harness are recorded in
`.styloagent/channel/saved-context/policy--context.md`.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
SRC = ROOT / "src/StyloMail.Policy"

PROJECT = "tests/StyloMail.Policy.Tests/StyloMail.Policy.Tests.csproj"

MUTATIONS = [
    # --- ruling (i), the reported half: a NotApplicable row stops leaving the denominator --------
    ("P1: the semantic half of the never-asked removal is dropped",
     SRC / "CompositeRiskScorer.cs",
     """            var neverAsked = match is null
                ? DeterministicFindings.IsDeterministic(signalId)
                : match.Availability == EvidenceAvailability.NotApplicable;""",
     """            var neverAsked = match is null
                ? DeterministicFindings.IsDeterministic(signalId)
                : false;""",
     "A_semantic_question_that_was_never_asked_leaves_the_denominator"),

    # --- ruling (i), the absent half, inverted: every absent row is treated as never asked -------
    ("P2: an absent semantic row is treated as never asked",
     SRC / "CompositeRiskScorer.cs",
     """            var neverAsked = match is null
                ? DeterministicFindings.IsDeterministic(signalId)
                : match.Availability == EvidenceAvailability.NotApplicable;""",
     """            var neverAsked = match is null
                ? true
                : match.Availability == EvidenceAvailability.NotApplicable;""",
     "A_row_that_was_never_measured_is_masked_without_a_reason"),

    # --- ruling (i), the absent half, disabled: no absent row ever leaves ------------------------
    ("P3: an absent deterministic row stops leaving the denominator",
     SRC / "CompositeRiskScorer.cs",
     """            var neverAsked = match is null
                ? DeterministicFindings.IsDeterministic(signalId)
                : match.Availability == EvidenceAvailability.NotApplicable;""",
     """            var neverAsked = match is null
                ? false
                : match.Availability == EvidenceAvailability.NotApplicable;""",
     "A_deterministic_question_moves_coverage_by_the_case_it_falls_in"),
]
