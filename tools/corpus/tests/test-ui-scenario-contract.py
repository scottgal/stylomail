#!/usr/bin/env python3
"""Regression harness for the corpus-to-Avalonia scenario contract.

The document beside `manifest.json` is a projection of authored facts and observed Host results. It
must give the client stable selectors without ever turning an intent into a predicted state.
"""

from __future__ import annotations

import importlib.util
import json
import pathlib
import shutil
import subprocess
import sys
import tempfile

REPO = pathlib.Path(__file__).resolve().parents[3]
CORPUS = REPO / "tools" / "corpus" / "corpus.py"

FAILURES: list[str] = []
CHECKS: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    CHECKS.append(name)
    if ok:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name}{(': ' + detail) if detail else ''}")
        FAILURES.append(name)


def generate(out: pathlib.Path) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, "-B", str(CORPUS), "generate", "--seed", "77", "--count", "6",
         "--profile", "mailbox", "--coverage", "full", "--out", str(out)],
        cwd=str(REPO), capture_output=True, text=True,
    )


def load_module():
    spec = importlib.util.spec_from_file_location("corpus_for_ui_contract_test", CORPUS)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def test_generation_writes_a_stable_unobserved_contract(root: pathlib.Path) -> None:
    first, second = root / "first", root / "second"
    one, two = generate(first), generate(second)
    check("both batches generate", one.returncode == 0 and two.returncode == 0,
          (one.stderr or two.stderr).strip()[:160])
    if one.returncode or two.returncode:
        return
    contract_path = first / "ui-scenarios.json"
    check("a UI contract is written beside the manifest", contract_path.is_file())
    check("the contract is deterministic before observations",
          contract_path.read_bytes() == (second / "ui-scenarios.json").read_bytes())
    contract = json.loads(contract_path.read_text(encoding="utf-8"))
    scenarios = contract.get("scenarios", [])
    check("the contract has a version and one scenario per message",
          contract.get("uiScenarioContractVersion") == 1 and len(scenarios) == 6)
    check("scenario ids are stable and unique",
          len({row.get("scenarioId") for row in scenarios}) == 6)
    check("unseeded values are unknown rather than predicted",
          all(row["observed"] == {
              "httpStatus": None, "action": None, "state": None,
              "queueId": None, "internalMessageId": None,
          } for row in scenarios))
    check("unseeded selectors make no row-existence claim",
          not contract["selectors"]["anyQueuedOrHeld"]["satisfied"]
          and not contract["selectors"]["anyDecisionJoinResolved"]["satisfied"]
          and contract["selectors"]["allPlantedFactsVerified"]["status"] == "notChecked")


def test_observed_results_drive_selectors_and_verification(root: pathlib.Path) -> None:
    out = root / "observed"
    result = generate(out)
    check("observation batch generates", result.returncode == 0, result.stderr.strip()[:160])
    if result.returncode:
        return
    manifest_path = out / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["messages"][0]["seeded"] = {
        "httpStatus": 202, "action": "Hold", "state": "Held", "queueId": "queue-1",
        "internalMessageId": "message-1",
    }
    module = load_module()
    module.write_ui_scenario_contract(
        manifest_path, manifest,
        {"status": "passed", "checkedMessages": 1, "declaredFactCount": 2},
    )
    contract = json.loads((out / "ui-scenarios.json").read_text(encoding="utf-8"))
    first = contract["scenarios"][0]
    check("observed values are copied without reclassification",
          first["observed"]["action"] == "Hold" and first["observed"]["state"] == "Held"
          and first["observed"]["internalMessageId"] == "message-1")
    check("state and join selectors name only observed rows",
          contract["selectors"]["anyQueuedOrHeld"]["scenarioIds"] == [first["scenarioId"]]
          and contract["selectors"]["anyDecisionJoinResolved"]["scenarioIds"] == [first["scenarioId"]])
    check("verification is recorded only when check supplied it",
          contract["selectors"]["allPlantedFactsVerified"]["status"] == "passed")


def main() -> int:
    root = pathlib.Path(tempfile.mkdtemp(prefix="corpus-ui-contract-"))
    try:
        for name, fn in sorted(globals().items()):
            if name.startswith("test_") and callable(fn):
                print(f"\n{name}")
                try:
                    fn(root)
                except Exception as exc:
                    check(name, False, f"raised {type(exc).__name__}: {exc}")
    finally:
        shutil.rmtree(root, ignore_errors=True)
    print()
    if FAILURES:
        print(f"{len(CHECKS)} assertion(s) run, {len(FAILURES)} FAILED: {FAILURES}")
        return 1
    print(f"{len(CHECKS)} assertion(s) run, all held")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
