"""Mutation set for the `nimble-` lane (StyloMail.Nimble, the local decision-model provider).

Each entry is (name, file, old_text, new_text, claims_test):
  * `old_text` must appear in the file EXACTLY ONCE, or the entry is INVALID.
  * `new_text` must change behaviour. A no-op replacement is INVALID, never a verdict.
  * `claims_test` names the test whose *name* asserts this behaviour, so the harness can tell
    CLAIMED (that test went red, the claim is verified) from ELSEWHERE (some other test did, so
    the claim is not verified and is either redundant or untested).

All three anchor the per-field shortening work landed as `35ff5d0`, "Carry the kept length out of
the state, so a body cut to nothing is a ruled case". Before it, ONE flag meant "the body or the
quoted tail was cut", so a reply whose quoted tail was cut and whose body was whole reported a
shortened BODY, with the body's uncut length written beside it as the number kept. The three entries
mutate three properties that repair created, and they are separately pinnable on purpose:

  N1  the RULED ARM. A body cut to nothing emits `Unavailable`, which is what puts the row in
      MailPolicyEngine's unanswered gate (`MailPolicyEngine.cs:512-514`, whose `:532` returns
      `Hold`). N1 makes it `Available`, which is exactly what an automated fail-open scan of this
      SHAPE suggests, and it would take the row OUT of that predicate.
  N2  the reason SELECTOR, which names WHICH field was cut. N2 makes the quoted-only arm return the
      body text, which is what the single text did before the repair.
  N3  the per-field state KEYS, each pair written only for its own field. N3 restores the OR, so a
      quoted-only cut writes the body's marker again.

N4 was added later, by a different finding: the cache key did not vary with the SERVER, so a caller
moved from one endpoint to another could be served an assessment taken against the first. It keys the
endpoint's authority now, and N4 makes that term a constant.

EXPECTED VERDICTS, written before the first sweep so the result is a reading rather than a retrofit.
N1 and N2 are CLAIMED, and they were MEASURED as such by hand before this file existed: this lane's
red-first build applied exactly these two mutations, under a filter naming their two tests, and got
`Failed: 2, Passed: 0, Skipped: 0, Total: 2`, each test failing for the property its mutation broke
with nothing else failing. N3 is NOT verified by hand, and it is the entry this sweep is for: it
should be CLAIMED by `Marks_a_shortened_quoted_tail_without_claiming_the_body_was_cut`, and if the
sweep returns GAP or ELSEWHERE then that test does not hold the half its name claims. N4 is CLAIMED by
`Changes_the_cache_key_when_anything_that_changes_an_answer_changes`, whose endpoint pair was added with
the fix; if N4 returns ELSEWHERE then some other test is holding the endpoint difference and the named
one is not, and if it returns GAP the new assertions do not run.

AND ONE ORDERING NOTE, READ FROM THE HARNESS RATHER THAN ASSUMED. It builds its isolated copy with
`shutil.copytree(SOURCE_ROOT, ...)`, which is a copy of the WORKING TREE and not of `HEAD`, so a
sweep from this tree carries uncommitted files. What that means here: N3's claiming test is landed in
`35ff5d0`, and N1's and N2's were UNCOMMITTED when this file was written, and ALL THREE are reachable
by a sweep run from this tree. A sweep run from a checkout that LACKS the uncommitted test file would
report N1 and N2 as GAP for a reason about that checkout rather than about the coverage.

Verdicts from the shared harness are recorded in
`.styloagent/channel/saved-context/nimble--context.md`.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
SRC = ROOT / "src/StyloMail.Nimble"

PROJECT = "tests/StyloMail.Nimble.Tests/StyloMail.Nimble.Tests.csproj"

MUTATIONS = [
    # --- the RULED ARM: a body cut to nothing stops being a refusal ------------------------------
    ("N1: the emptied-body arm reports Available instead of Unavailable",
     SRC / "NimbleSemanticMailClassifier.cs",
     """                evidence.Add(UnavailableEvidence(
                    dimension,
                    EvidenceAvailability.Unavailable,
                    observedAt,
                    modelVersion,
                    EmptyBodyShortenedReason));""",
     """                evidence.Add(UnavailableEvidence(
                    dimension,
                    EvidenceAvailability.Available,
                    observedAt,
                    modelVersion,
                    EmptyBodyShortenedReason));""",
     "Reports_the_emptied_body_as_unavailable_rather_than_as_a_weaker_read"),

    # --- the reason SELECTOR: the quoted-only arm stops naming the quoted field -------------------
    ("N2: a quoted-only cut reports the body reason instead of the quoted one",
     SRC / "NimbleSemanticMailClassifier.cs",
     """            (false, true) => "the client shortened the quoted history to fit the context window",""",
     """            (false, true) => PromptShortenedReason,""",
     "Names_the_QUOTED_field_in_the_reason_when_only_the_quoted_tail_was_cut"),

    # --- the per-field state KEYS: the two cuts collapse back into one flag -----------------------
    ("N3: the body state marker is written on a quoted-only cut again",
     SRC / "NimbleMessageState.cs",
     """        if (bodyCut)
        {""",
     """        if (bodyCut || quotedCut)
        {""",
     "Marks_a_shortened_quoted_tail_without_claiming_the_body_was_cut"),

    # --- the ENDPOINT term: the key stops varying with the server --------------------------------
    # `ComputeCacheKeyDigest` keys the endpoint's authority because the model term cannot identify
    # the server: `/v1/systemone` echoes the name it was given and reports no resolved id, so two
    # servers both answer `nimble:latest`. N4 makes the term a constant, so the key is identical
    # across servers again. The call site keeps calling the helper with a fixed argument rather than
    # dropping the line, so the mutation cannot turn an unused-method analyzer warning into a build
    # failure that would redden every test and read as ELSEWHERE instead of CLAIMED.
    ("N4: the cache key stops distinguishing the endpoint that answered",
     SRC / "NimbleSemanticMailClassifier.cs",
     """                endpoint = EndpointAuthority(_options.Endpoint),""",
     """                endpoint = EndpointAuthority("http://constant.invalid:1"),""",
     "Changes_the_cache_key_when_anything_that_changes_an_answer_changes"),
]
