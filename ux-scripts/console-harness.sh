#!/usr/bin/env bash
# Shared plumbing for the desktop- console driving scripts. Sourced, not run.
#
# Modelled on mylo's ux-scripts/reader-harness.sh, and for the same reason: a
# script that asserts on a running Host has to control which Host it is talking
# to, or its assertions are about whatever the person running it happens to have.
#
# So this starts its own throwaway Host on loopback, with locally generated
# values and storage under a scratch directory, and kills it on the way out
# including on failure and on interrupt. The consequence worth having: the
# scripts are repeatable by construction rather than by remembering to clean up,
# they can be run twice in a row, and they never touch a real deployment.
#
# Nothing here is a real credential. The profile key is generated per run; the
# provider key is deliberately a placeholder because these runs make no provider
# call (see the note on StyloMail__Jev__Endpoint below).

set -uo pipefail

CONSOLE_REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONSOLE_HOST_APP="$CONSOLE_REPO/src/StyloMail.Host/bin/Debug/net10.0/StyloMail.Host"
CONSOLE_RUN="${CONSOLE_RUN:-/tmp/stylomail-console-ux}"

# Deliberately not 5000 (macOS AirPlay Receiver listens there) and not the
# value docs/running.md uses, so a harness run cannot be confused with a real
# deployment on this machine.
CONSOLE_PORT="${CONSOLE_PORT:-5271}"
CONSOLE_BASE="http://127.0.0.1:${CONSOLE_PORT}"

export DOTNET_ROOT="${DOTNET_ROOT:-/usr/local/share/dotnet}"
export PATH="/usr/local/share/dotnet:$PATH"

# Builds everything a console run needs, from the repo root, with the build's
# own output shown rather than hidden.
#
# This replaces two earlier shapes, and both of them made a fresh checkout fail
# before a single assertion ran:
#
#   1. A refusal. `console_require_app` printed "Build first: dotnet build ..."
#      and exited 1 instead of building, so a first-time user's only progress
#      was to work out that the script wanted a prerequisite it would not make.
#      That message was the first wall on a clean clone and nothing past it was
#      reachable.
#   2. A `dotnet build .../StyloMail.Host.csproj -v quiet --nologo >/dev/null`
#      copied into each runner. Building one project by path rather than through
#      the solution is the shape that fails to resolve project references in
#      some clean checkouts (CS0234 on StyloMail.Chat and StyloMail.Jev, CS0006
#      on missing obj/.../ref/*.dll). I could not reproduce that failure from
#      this tree, but a solution build cannot have it, and the `>/dev/null` is
#      indefensible either way: it turned any failure into exit 1 with a 0-byte
#      log, which is a build failure with its reason deleted.
#
# The log is written as well as shown, so it outlives the terminal's scrollback
# and can still be read after a failed run.
console_build_all() {
    local log_dir="${CONSOLE_BUILD_LOG_DIR:-/tmp/stylomail-console-build}"
    local log="$log_dir/solution.log"
    mkdir -p "$log_dir"
    echo "== building StyloMail.slnx for the console harness =="
    dotnet build "$CONSOLE_REPO/StyloMail.slnx" --nologo 2>&1 | tee "$log"
    local status="${PIPESTATUS[0]}"
    if [[ $status -ne 0 ]]; then
        echo "build failed with exit $status. Full log: $log" >&2
    fi
    return "$status"
}

# Starts a throwaway Host and waits for it to answer, rather than sleeping a
# fixed amount: a Host that never comes up must be reported as such.
#
# Three things here are fixes for a run that lied to me, and each is worth
# keeping:
#
#   1. It refuses to start if the port is already taken. Without this it
#      happily proceeded against whatever was already listening, which on this
#      machine was an orphaned Host from an earlier run holding a different
#      API key. Every authenticated call then answered 401 and the run
#      reported a console bug that did not exist.
#   2. It launches the built binary rather than `dotnet run`. A backgrounded
#      subshell around `dotnet run` records the SUBSHELL's pid, so killing it
#      killed the subshell and left the actual Host running: that is where the
#      orphan came from. Running the binary directly means the pid recorded is
#      the process that must die.
#   3. It reports the failure rather than continuing, because a harness that
#      cannot tell whose Host it is talking to is not testing anything.
console_start_host() {
    if lsof -nP -iTCP:"$CONSOLE_PORT" -sTCP:LISTEN >/dev/null 2>&1; then
        echo "Port $CONSOLE_PORT is already in use, so this run would talk to" >&2
        echo "whatever is listening there rather than to its own Host:" >&2
        lsof -nP -iTCP:"$CONSOLE_PORT" -sTCP:LISTEN >&2
        echo "Stop it, or set CONSOLE_PORT to a free port." >&2
        return 1
    fi

    if [[ ! -x "$CONSOLE_HOST_APP" ]]; then
        echo "No Host binary at $CONSOLE_HOST_APP, so there is nothing to start." >&2
        echo "console_build_all should have produced it; run it and check its log." >&2
        return 1
    fi

    mkdir -p "$CONSOLE_RUN/data/spool"

    head -c 32 /dev/urandom | xxd -p | tr -d '\n' > "$CONSOLE_RUN/data/profile.key"
    head -c 24 /dev/urandom | xxd -p | tr -d '\n' > "$CONSOLE_RUN/data/principal.key"

    export TYPESAFE_API_KEY="not-a-real-key-harness-only"
    export STYLOMAIL_PROFILE_KEY="$(cat "$CONSOLE_RUN/data/profile.key")"
    export ASPNETCORE_URLS="$CONSOLE_BASE"

    # The semantic provider is pointed at an address that cannot answer, so the
    # pipeline degrades to explicitly-unavailable evidence instead of throwing
    # on a rejected key. That is the state the console has to render honestly,
    # and it is the only state reachable without a real provider key.
    export StyloMail__Jev__Endpoint="http://127.0.0.1:9/v1/systemone"

    # The live feed, on by default here so the main smoke exercises the Hub and
    # screenshots the console following it. It is off by default in a real
    # deployment, which is the other state worth proving, so it is a switch
    # rather than a constant: CONSOLE_TRAFFIC=false starts a Host with no Hub
    # at all, and ux-scripts/run-console-no-feed-smoke.sh is what asserts the
    # console says so rather than looking broken.
    if [[ "${CONSOLE_TRAFFIC:-true}" == "true" ]]; then
        export StyloMail__Traffic__Enabled=true
    else
        unset StyloMail__Traffic__Enabled
    fi

    export StyloMail__Storage__SpoolRoot="$CONSOLE_RUN/data/spool"
    export StyloMail__Storage__DatabasePath="$CONSOLE_RUN/data/host.db"
    export StyloMail__Auth__Principals__0__PrincipalId="harness"
    export StyloMail__Auth__Principals__0__TenantId="harness"
    export StyloMail__Auth__Principals__0__Key="$(cat "$CONSOLE_RUN/data/principal.key")"
    export StyloMail__Auth__Principals__0__Privileges__0="Assess"
    export StyloMail__Auth__Principals__0__Privileges__1="Send"
    export StyloMail__Auth__Principals__0__Privileges__2="Review"
    export StyloMail__Auth__Principals__0__Privileges__3="Feedback"
    export StyloMail__Auth__Principals__0__Privileges__4="Administer"

    # The binary, not `dotnet run`. See point 2 above: a subshell around
    # `dotnet run` records the wrong pid and the Host outlives the cleanup.
    "$CONSOLE_HOST_APP" serve > "$CONSOLE_RUN/host.log" 2>&1 &
    CONSOLE_HOST_PID=$!

    for _ in $(seq 1 90); do
        if curl -fsS --max-time 2 "$CONSOLE_BASE/health/live" >/dev/null 2>&1; then
            return 0
        fi
        if ! kill -0 "$CONSOLE_HOST_PID" 2>/dev/null; then
            echo "The harness Host exited before becoming live. Last lines:" >&2
            tail -20 "$CONSOLE_RUN/host.log" >&2
            return 1
        fi
        sleep 1
    done

    echo "The harness Host did not become live within 90s." >&2
    return 1
}

# Gives the throwaway Host something to group.
#
# A fresh Host has one principal and no companies, so the sidebar would show a
# single "Ungrouped" row and the grouping would be invisible to every
# assertion. Seeded through the API rather than by writing the database, so
# what the console reads is what the routes produce.
console_seed_management() {
    local key base
    key="$(cat "$CONSOLE_RUN/data/principal.key")"
    base="$CONSOLE_BASE"

    local company
    company=$(curl -fsS -X POST \
        -H "X-StyloMail-Key: $key" -H "Content-Type: application/json" \
        --data '{"name":"Acme","notes":"seeded by the console harness"}' \
        "$base/v1/companies" | sed -n 's/.*"companyId":"\([^"]*\)".*/\1/p')

    if [[ -z "$company" ]]; then
        echo "Could not seed a company; the sidebar will show one ungrouped sender." >&2
        return 0
    fi

    curl -fsS -X PUT \
        -H "X-StyloMail-Key: $key" -H "Content-Type: application/json" \
        --data "{\"label\":\"Acme outbound\",\"companyId\":\"$company\",\"notes\":\"seeded\"}" \
        "$base/v1/senders/harness/settings" >/dev/null

    echo "seeded company $company with the harness principal filed under it"
}

# Produces a real decision on the throwaway Host and names it for the console.
#
# This is the piece that makes the run an integration test rather than a smoke.
# An assessment-only call is recorded in the ledger, so the console can open it
# over GET /v1/decisions/{id} and render evidence the Host actually produced.
#
# The provider is pointed at an unreachable address, so every semantic dimension
# comes back Unavailable and policy declines: the decision is a real one that
# says the semantic layer never looked. That is the state the console must
# render as absent rather than as zero, and this is the only way to reach it
# without the operator's provider key.
console_seed_decision() {
    local key="$1"
    local base="$2"

    printf 'From: "Accounts" <security@exampple.test>\r\nTo: alice@example.test\r\nSubject: Urgent: verify your account\r\nDate: Tue, 22 Sep 2026 10:00:00 +0000\r\nMessage-ID: <harness-%s@exampple.test>\r\nMIME-Version: 1.0\r\nContent-Type: text/html; charset="utf-8"\r\n\r\n<html><body><p>Verify your account within 24 hours.</p><p><a href="http://198.51.100.9/v">https://accounts.example.test/login</a></p></body></html>\r\n' "$$" > "$CONSOLE_RUN/seed.eml"

    local raw
    raw="$(base64 -i "$CONSOLE_RUN/seed.eml" | tr -d '\n')"

    python3 - "$raw" > "$CONSOLE_RUN/seed.json" <<'PYEOF'
import json, sys
print(json.dumps({
    "direction": "Inbound",
    "mailFrom": "security@exampple.test",
    "rcptTo": ["alice@example.test"],
    "rawMime": sys.argv[1],
    "connectingIp": "198.51.100.9",
}))
PYEOF

    curl -fsS -X POST \
        -H "X-StyloMail-Key: $key" -H "Content-Type: application/json" \
        --data @"$CONSOLE_RUN/seed.json" "$base/v1/assessments" \
        > "$CONSOLE_RUN/decision.json" 2>"$CONSOLE_RUN/seed.err" || {
        echo "Could not seed a decision; the pane will show its empty state." >&2
        return 0
    }

    local id
    id="$(sed -n 's/.*"assessmentId":"\([^"]*\)".*/\1/p' "$CONSOLE_RUN/decision.json")"

    if [[ -z "$id" ]]; then
        echo "The assessment answered without an id; the pane will show its empty state." >&2
        return 0
    fi

    echo "seeded decision $id"

    # Exported for the console, which opens it over the API rather than reading
    # a file: the point of seeding it is that it is real.
    export STYLOMAIL_SMOKE_DECISION_ID="$id"
}

# Kills only the Host this script started, by the pid it recorded.
#
# Deliberately not a pattern kill. `pkill -f StyloMail.Host` would take down
# every Host on the machine, including one another agent is running on a
# different port, and this repository has several agents that start one. A
# cleanup step that reaches outside what it started is worse than no cleanup.
console_stop_host() {
    if [[ -n "${CONSOLE_HOST_PID:-}" ]]; then
        kill "$CONSOLE_HOST_PID" 2>/dev/null || true
        wait "$CONSOLE_HOST_PID" 2>/dev/null || true
    fi
}

# Points the console at the harness Host. STYLOMAIL_HOST and
# STYLOMAIL_SMOKE_KEY are the Debug-only overrides in ConsoleEnvironment; a
# Release build reads neither and takes its key from the keychain.
console_export_app_env() {
    export STYLOMAIL_HOST="$CONSOLE_BASE"
    export STYLOMAIL_SMOKE_KEY="$(cat "$CONSOLE_RUN/data/principal.key")"

    # And a decision body for the detail pane, as a fallback.
    #
    # The fallback is second, not first: a script that seeds a decision with
    # console_seed_decision exports STYLOMAIL_SMOKE_DECISION_ID, and the console
    # prefers it, opening the decision over GET /v1/decisions/{id} because a
    # body the Host produced exercises the client, the contract and the pane
    # together. This file is what covers a script that wanted the pane populated
    # without seeding one.
    #
    # So say which you are looking at when reading a screenshot. The main smoke
    # seeds a decision and shows the Host's own evidence; a run that only
    # exported this file shows the pane's rendering over a body the Host did not
    # produce. Both are honest runs, and they are evidence about different
    # things.
    export STYLOMAIL_SMOKE_DECISION_FILE="$CONSOLE_REPO/ux-scripts/decision-fixture.json"
}
