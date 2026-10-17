"""Control for the split defect: a rename must not move a row between fit and calibration.

The defect was real. The split was hashed from the row id, so renaming exemplar ids (seed-1: to
ex:) moved every row between fit and calibration and the held-out metrics moved with them. The fix
keys the partition by the text digest. This test reads the committed artefact, rebuilds the
partition, renames every id, rebuilds again, and asserts the two partitions are equal.

Two controls keep the assertion from being vacuous: the partition must be non-empty and not the
whole set, and changing one row's content digest must change its membership.
"""

from __future__ import annotations

import copy
import json
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).parent))

from train_elements import calibration_set  # noqa: E402

ARTEFACT = pathlib.Path(__file__).parent.parent / "service" / "artefacts" / "elements-corpus-1.json"


def rows_from_artefact() -> list[dict]:
    document = json.loads(ARTEFACT.read_text(encoding="utf-8"))
    return [
        {
            "id": exemplar["id"],
            "element": exemplar["element"],
            "label": exemplar["label"],
            "split": exemplar["split"],
            "text_digest": exemplar["text_digest"],
        }
        for exemplar in document["exemplars"]
    ]


def test_the_committed_artefact_carries_a_text_digest_per_exemplar() -> None:
    rows = rows_from_artefact()
    assert rows, "no exemplars"
    assert all(row["text_digest"] for row in rows), "an exemplar has no text digest"


def test_renaming_ids_does_not_move_a_row_between_fit_and_calibration() -> None:
    rows = rows_from_artefact()
    before = calibration_set(rows)

    # Controls: the partition exists and is not everything, so equality below means something.
    assert before, "the calibration set is empty"
    assert len(before) < len(rows), "the calibration set is every row"

    renamed = copy.deepcopy(rows)
    for index, row in enumerate(renamed):
        row["id"] = f"renamed-{index:04d}"
    after = calibration_set(renamed)

    assert after == before


def test_changing_content_changes_the_partition() -> None:
    """A fresh content digest must change the selected set.

    Renaming ids must NOT (the test above), and changing content must. Together those pin the key to
    content. The digest chosen sorts first in its group, so the row it belongs to becomes the group's
    first row; the value that was selected before shifts one place and drops out, and the selected
    set therefore changes. Asserting direction would depend on rank and on how many rows the group
    holds, so the claim is the set inequality, plus a check that the new value is present.
    """
    rows = rows_from_artefact()
    before = calibration_set(rows)
    assert before and len(before) < len(rows)

    armed = copy.deepcopy(rows)
    target = next(row for row in armed if row["split"] == "train" and row["element"] == "credential_request")
    target["text_digest"] = "0" * 64

    after = calibration_set(armed)
    assert after != before
    assert "0" * 64 in after
