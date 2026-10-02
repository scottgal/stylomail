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

    # --- the two properties `ShortenedReadTests.cs` pins, added 2026-10-02 ----------------------
    ("P4: the scorer treats a `reason` attribute as significant",
     SRC / "CompositeRiskScorer.cs",
     """            if (match.Availability != EvidenceAvailability.Available || match.Value is not { } value)""",
     """            if (match.Availability != EvidenceAvailability.Available || match.Value is not { } value
                || match.Attributes?.Any(a => a.Name == "reason") == true)""",
     "A_shortened_read_stays_counted_and_carries_its_reason"),

    ("P5: the security gate's threshold comparison is raised out of reach",
     SRC / "MailPolicyEngine.cs",
     """            && value >= _options.HoldThreshold);""",
     """            && value >= 0.90);""",
     "A_shortened_row_still_elevates_the_security_gate"),
]

# EXPECTED VERDICTS for P4 and P5, written BEFORE their first run so the result is a reading rather
# than a retrofit, in the form the three entries above established.
#
# The two entries are the pair that `tests/StyloMail.Policy.Tests/ShortenedReadTests.cs` needs, and
# they were added because that file's first run produced a RED FROM ITS OWN FIXTURE rather than from a
# mutation of the property under test. A pin that has been RUN but never MUTATED is the case this
# harness exists to close, so the red has to come from the other side.
#
# BOTH SHOULD BE CLAIMED AND NEITHER SHOULD BE ELSEWHERE, which is the claim worth writing down
# because the two mutations are deliberately aimed at DIFFERENT subjects:
#   * P4 masks a row that carries a reason attribute. It must redden
#     `A_shortened_read_stays_counted_and_carries_its_reason` and must leave
#     `A_shortened_row_still_elevates_the_security_gate` GREEN, because that test reads the row's
#     availability off the raw evidence and never consults the scorer's mask: with the row masked the
#     index is over the marketing row alone, which is still a RELAXABLE hold, and the gate still fires
#     on the raw row. So ELSEWHERE on P4 would mean the two tests are not separable and the pair is
#     not the pair it claims to be.
#   * P5 raises the gate's threshold so that a 0.85 security row no longer elevates. It must redden
#     `A_shortened_row_still_elevates_the_security_gate`, where 0.85 is in the fixture for exactly
#     this band, and must leave the first test green because that one never calls the engine.
#
# And GAP on either would be the finding this file was written to make: that the property is pinned by
# nothing, which is what its docstring above says about P2 and is the outcome to report rather than to
# repair by renaming a claim.

# Verdicts from the shared harness are recorded in
# `.styloagent/channel/saved-context/policy--context.md`.
