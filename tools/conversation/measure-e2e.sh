#!/usr/bin/env bash
# Measures whether supplying a conversation window moves the final action, end to end.
#
# Why this exists: the classifier-level measurement answers what the dimension returns and what
# the coverage flag does, and neither of those is the same question as "does the decision change".
# Only a real Host, with a real assessor, answering a real HTTP request can answer that, because
# the action is produced by policy over the whole evidence set rather than by the adapter.
#
# It measures both directions on purpose. A window whose prior turns belong to the message should
# raise conversational continuity, and a window bolted onto an unrelated message should lower it.
# A run that only ever supplied a matching window could not tell "the index responds to this
# dimension" from "the index went up for some other reason".
#
# What it prints, per arm: HTTP status, the action, the risk index, the coverage flag, and the
# semantic.conversational_continuity row. Every response is written under $CONSOLE_RUN as well, so
# a claim can be re-read from the artifacts afterwards. Every arm is repeated, because a single
# reading of a decision near a threshold says nothing about which side of it the decision sits.
#
# It asserts nothing. It is an instrument: a probe that failed would have to decide what the right
# answer was before measuring it.
#
# The principal key never enters this script. console_auth_headers reads it from the harness's
# generated file itself and writes a 0600 header file, which curl takes as -H @file, so the value
# is in no argument list and no variable here. It is never printed, and neither is any response
# header. console_stop_host removes the file on the way out.
#
# An earlier revision did hold the key in a shell variable to pass it in. `desktop-` changed the
# helper's signature (e19daf3) to take the value off the call, and the reason is worth keeping:
# a helper whose signature requires the value is a helper every caller must hold the value to
# call, and that only stays safe while every caller stays careful. The variable was also a live
# break, not just a smell: against the new signature the key was taken as the OUTPUT PATH.

set -uo pipefail

# The scratch directory belongs to this lane and is gitignored, so a run's responses survive and
# can be re-read. A temporary directory was the earlier default and `overview-` ruled it out: a
# quoted number whose artifact has evaporated cannot be re-checked by anyone.
CONSOLE_RUN="${CONSOLE_RUN:-.styloagent/scratch/conversation-/e2e}"
export CONSOLE_PROVIDER=nimble

# This lane's own Host port, set here rather than left to the harness default. Sourcing the harness
# without naming a port means taking 5271, which is the harness's default and is what `desktop-`'s
# console smoke starts on, so its runner refuses to start rather than talk to a stranger's Host.
# That happened on 1 Oct: my 01:19:08 run took 5271 and cost `desktop-` a long-outage run, and the
# guard is right to refuse, so the fix is to never be the stranger. 5290 is `desktop-`'s; 5271 is
# the default; 5293 is this lane's and nothing else listens there.
# A run still holds the port only for its own duration: console_stop_host runs from the EXIT trap.
CONSOLE_PORT="${CONSOLE_PORT:-5293}"
export CONSOLE_PORT
if [ "$CONSOLE_PORT" = "5271" ] || [ "$CONSOLE_PORT" = "5290" ]; then
    echo "refusing to start: port $CONSOLE_PORT belongs to another lane (5271 harness default / console smoke, 5290 desktop-)." >&2
    echo "This lane's port is 5293; pass CONSOLE_PORT explicitly only to move to one nobody else claims." >&2
    exit 1
fi

source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../../ux-scripts" && pwd)/console-harness.sh"

trap 'console_stop_host' EXIT INT TERM

CONV_REPEATS="${CONV_REPEATS:-3}"
CONV_RESULTS="$CONSOLE_RUN/results"
CONV_FIXTURES="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/tests/fixtures/jev"

echo "=================================================================="
echo "shape: CONSOLE_PROVIDER=$CONSOLE_PROVIDER CONSOLE_ASSESSOR=${CONSOLE_ASSESSOR:-true}"
echo "scratch: $CONSOLE_RUN   repeats per arm: $CONV_REPEATS"
echo "=================================================================="

console_build_all || exit 1
console_start_host || exit 1

CONV_HEADERS="$(console_auth_headers "$CONSOLE_RUN/conversation-auth.headers")"

mkdir -p "$CONV_RESULTS"

# Responses from an earlier run are cleared only for a full run. A narrowed run keeps them, because
# the whole point of narrowing is to add a cell to a suite that already ran, and the arms it did not
# re-run are still the evidence for everything previously reported from this directory. Deleting
# them to add one arm would destroy the artifacts the earlier arms were quoted from.
if [ -z "${CONV_ONLY:-}" ]; then
    rm -f "$CONV_RESULTS"/*.json 2>/dev/null
fi

# The arms. Each is a fixture, an optional window, and a name. The windows are built here rather
# than read from a fixture file, and the report says so: they are this lane's, not corpus-'s.
conv_build_requests() {
    CONV_FIXTURES="$CONV_FIXTURES" CONV_RESULTS="$CONV_RESULTS" python3 <<'PYEOF'
import base64, json, os

fixtures = os.environ["CONV_FIXTURES"]
out = os.environ["CONV_RESULTS"]

# The window that really belongs to reply-in-thread: its own prior turn.
matching = [
    "From: Northwind Supplies <orders@northwind.example>\n"
    "Subject: Your order NW-4482 has shipped\n\n"
    "Order NW-4482 was dispatched today and should arrive within two working days."
]

# A window that belongs to a different conversation entirely, so a continuity answer of "does not
# fit" is the correct answer rather than noise.
mismatched = [
    "From: Northwind Supplies <orders@northwind.example>\nSubject: Your order NW-4482 has shipped\n\n"
    "Order NW-4482 was dispatched today and should arrive within two working days.",
    "From: alice@example.example\nSubject: Re: Your order NW-4482 has shipped\n\n"
    "Thanks for the update. Two working days is fine.",
    "From: Northwind Supplies <orders@northwind.example>\nSubject: Re: Your order NW-4482 has shipped\n\n"
    "Noted, thank you. The tracking reference will follow once the carrier scans it.",
]

# The two bodies of the axis arm. `resting` restates the window ("Two working days" is in the
# window's first turn); `advancing` reports a state the window does not contain. They are 48 and 40
# characters, so length cannot explain a difference between them; M6b measured exactly this pair
# answering A and B at the adapter.
resting = "Thanks for the update. Two working days is fine."
advancing = "Thanks, the parcel arrived this morning."

# The cell `overview-` asked for, and it is a one-phrase edit of `advancing` rather than a new
# sentence. `advancing` says "the parcel", which shares no word with the window; this says "Order
# NW-4482", which is the window's own entity, and nothing else changes. So the two differ in exactly
# one property: whether the body reuses the window's words while reporting a state the window does
# not contain. If this answers B as `advancing` does, containment is what the dimension reads and
# lexical overlap is refuted. If it answers A, the reading is lexical and M6b's containment wording
# is wrong. The two bodies are 40 and 43 characters, so length cannot explain a difference either.
overlap = "Thanks, Order NW-4482 arrived this morning."

arms = [
    ("reply-in-thread", "reply-in-thread", None, "none", None),
    ("reply-in-thread", "reply-in-thread", matching, "matching", None),
    ("reply-in-thread", "reply-in-thread", mismatched, "mismatched", None),
    ("credential-request", "credential-request", None, "none", None),
    ("credential-request", "credential-request", mismatched, "mismatched", None),

    # The axis, on the shipping path. Both arms carry the SAME fixture and the SAME three-turn
    # window as the `mismatched` arm above, and differ only in the latest body. The bodies are the
    # two cells of M6b's 2x2 that disagree: one restates the window, one advances past it.
    #
    # This is here because M1 and M2 already disagree with each other on an identical input. M1's
    # `mismatched` arm (the fixture verbatim, which restates the window) answered B end to end
    # through the Host; M2 measured the same body over the same byte-identical window answering A
    # through the adapter. Same bytes in, opposite answers out, different path. So the axis being
    # real at the classifier level says nothing about whether it survives the Host's rendering, and
    # this pair is what decides it.
    ("reply-in-thread", "reply-in-thread", mismatched, "axis-restating", resting),
    ("reply-in-thread", "reply-in-thread", mismatched, "axis-advancing", advancing),
    ("reply-in-thread", "reply-in-thread", mismatched, "axis-overlap-newstate", overlap),
]

manifest = []

for index, (fixture, label, window, kind, override) in enumerate(arms):
    with open(os.path.join(fixtures, fixture + ".eml"), "rb") as handle:
        wire = handle.read()

    # A body override rewrites the message's content and nothing else: the headers, the threading
    # fields and the Message-ID are the fixture's own, so the two axis arms differ in the one
    # property under test. Splitting on the first blank line is the MIME header/body boundary; the
    # fixture is committed with LF endings.
    if override is not None:
        text = wire.decode("utf-8").replace("\r\n", "\n")
        boundary = text.index("\n\n")
        wire = (text[:boundary] + "\n\n" + override + "\n").encode("utf-8")

    raw = base64.b64encode(wire).decode("ascii")

    body = {
        "direction": "Inbound",
        "mailFrom": "sender@example.test",
        "rcptTo": ["recipient@example.test"],
        "rawMime": raw,
        "connectingIp": "203.0.113.10",
        "authenticationResults": [
            {
                "mechanism": "spf",
                "result": "pass",
                "verifierId": "conversation-measure",
                "fromTrustedVerifier": True,
                "detail": "domain=example.test; ip=203.0.113.10",
            }
        ],
    }

    if window is not None:
        body["conversationContext"] = window

    name = f"arm{index}-{label}-{kind}"
    with open(os.path.join(out, name + ".request.json"), "w", encoding="utf-8") as handle:
        json.dump(body, handle)

    manifest.append({"name": name, "fixture": fixture, "window": kind, "turns": len(window or [])})

with open(os.path.join(out, "arms.json"), "w", encoding="utf-8") as handle:
    json.dump(manifest, handle, indent=2)

print(f"prepared {len(manifest)} arms")
PYEOF
}

conv_post() {
    local name="$1"
    local code

    code="$(curl -sS -o "$CONV_RESULTS/$name.json" -w '%{http_code}' \
        --max-time "${CONV_TIMEOUT:-300}" \
        -H @"$CONV_HEADERS" \
        -H "Content-Type: application/json" \
        --data @"$CONV_RESULTS/$name.request.json" \
        "$CONSOLE_BASE/v1/assessments" 2>"$CONV_RESULTS/$name.err")"

    printf '%s %s\n' "$name" "$code"
}

conv_build_requests

# Warm the local model through the Host, and keep that call out of the measurement: the first
# call after a context-window change pays a model load, which would otherwise be reported as the
# assessor's latency and could push an early arm onto a timeout.
echo "warmup via the Host (may include a model load)..."
conv_post "warmup-reply-in-thread-none" >/dev/null

ARMS="$(python3 -c 'import json,sys; print(" ".join(a["name"] for a in json.load(open(sys.argv[1]))))' "$CONV_RESULTS/arms.json")"

# Narrow a run to some arms, so adding one cell does not cost the model a whole re-sweep. The model
# is shared and single: re-running seven arms to add an eighth is another lane's time, not mine.
CONV_ONLY="${CONV_ONLY:-}"
if [ -n "$CONV_ONLY" ]; then
    ARMS="$(printf '%s\n' $ARMS | grep -E "$CONV_ONLY" | tr '\n' ' ')"
    echo "narrowed to: $ARMS"
fi

for pass_number in $(seq 1 "$CONV_REPEATS"); do
    for arm in $ARMS; do
        result="$(conv_post "$arm")"
        echo "pass $pass_number: POST /v1/assessments $result"

        # Move each pass's response aside, so the summary reads the whole set rather than the
        # last write. A single value overwritten per arm is how a distribution turns into a point.
        mv "$CONV_RESULTS/$arm.json" "$CONV_RESULTS/$arm.pass$pass_number.json"
    done
done

echo
echo "== summary =="

CONV_RESULTS="$CONV_RESULTS" python3 <<'PYEOF'
import glob, json, os, re

out = os.environ["CONV_RESULTS"]
arms = json.load(open(os.path.join(out, "arms.json")))
CONTINUITY = "semantic.conversational_continuity"


def row(path):
    body = json.load(open(path))

    continuity = next(
        (e for e in body.get("evidence", []) if e.get("signalId") == CONTINUITY), None)

    if continuity is None:
        continuity_state = "absent"
    elif continuity.get("availability") != "Available":
        continuity_state = continuity.get("availability")
    else:
        continuity_state = "A" if continuity.get("value") == 1.0 else "B"

    return {
        "action": body.get("action"),
        "riskIndex": body.get("riskIndex"),
        "contextMissing": (body.get("coverage") or {}).get("conversationContextMissing"),
        "coverage": (body.get("coverage") or {}).get("conversationContextMissing"),
        "continuity": continuity_state,
        "reasons": sorted(r.get("code") for r in body.get("reasons", [])),
    }


print(f"{'arm':<38} {'pass':<5} {'action':<22} {'risk':>6}  {'ctxMissing':<11} {'continuity':<14}")
print("-" * 106)

summary = {}

for arm in arms:
    name = arm["name"]
    rows = []

    for path in sorted(glob.glob(os.path.join(out, name + ".pass*.json")),
                       key=lambda p: int(re.search(r"pass(\d+)", p).group(1))):
        pass_number = int(re.search(r"pass(\d+)", path).group(1))
        result = row(path)
        rows.append(result)
        print(f"{name:<38} {pass_number:<5} {result['action']:<22} {result['riskIndex']:>6.2f}  "
              f"{str(result['contextMissing']):<11} {result['continuity']:<14}")

    # An arm with no responses is one this run did not take (a narrowed run, or a run that stopped
    # early). Summarising it would report a distribution over nothing, and min() over an empty list
    # raises, which would lose the arms that did run.
    if not rows:
        continue

    summary[name] = {
        "fixture": arm["fixture"],
        "window": arm["window"],
        "turns": arm["turns"],
        "actions": sorted(set(r["action"] for r in rows)),
        "riskIndexes": [r["riskIndex"] for r in rows],
        "riskIndex_min": min(r["riskIndex"] for r in rows),
        "riskIndex_max": max(r["riskIndex"] for r in rows),
        "continuity": sorted(set(r["continuity"] for r in rows)),
        "contextMissing": sorted(set(str(r["contextMissing"]) for r in rows)),
        "reasons": sorted(set(tuple(r["reasons"]) for r in rows)),
    }

with open(os.path.join(out, "summary.json"), "w", encoding="utf-8") as handle:
    json.dump(summary, handle, indent=2)

print()
print("per arm:")
for name, data in summary.items():
    print(f"  {name:<38} action={','.join(data['actions']):<20} "
          f"risk={data['riskIndex_min']:.2f}..{data['riskIndex_max']:.2f} "
          f"continuity={','.join(data['continuity'])}")
PYEOF

echo
echo "artifacts: $CONV_RESULTS"
