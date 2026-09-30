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

# Where the local decision model is asked whether it is up. 11435 rather than
# Ollama's default 11434 on purpose: NimbleOptions explains why, and this has to
# agree with it or the check would pass while the Host failed.
CONSOLE_NIMBLE_TAGS="${CONSOLE_NIMBLE_TAGS:-http://127.0.0.1:11435/api/tags}"

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
#      on missing obj/.../ref/*.dll), and the `>/dev/null` was indefensible
#      either way: it turned any failure into exit 1 with a 0-byte log, which is
#      a build failure with its reason deleted.
#
# The log is written as well as shown, so it outlives the terminal's scrollback
# and can still be read after a failed run.
#
# BUILT BY A RELATIVE PATH FROM THE REPO ROOT, which is what door 2 actually
# was. The old line and my first replacement both passed an absolute path built
# from a shell `pwd`, and on this machine that is not the same path twice: /tmp
# is a symlink to /private/tmp, so one invocation spells the projects /tmp/...
# and the next spells the same projects /private/tmp/... MSBuild keys projects
# by resolved path, so those are two identities for one project, and it builds
# them against each other.
#
# What that looks like from outside, all of it seen in pristine clones under
# /tmp: the same project restored twice in one log under the two spellings;
# `error : System.IO.IOException: The process cannot access the file
# '.../StyloMail.Host/obj/Debug/net10.0/rjsmrazor.dswa.cache.json' because it is
# being used by another process`; `CS0006: Metadata file '.../obj/Debug/
# net10.0/ref/StyloMail.AccessProxy.Tests.dll' could not be found` while
# StyloMail.Integration.Tests compiled; and a Host that built clean, reported
# success, then died at launch with FileNotFoundException on StyloMail.Core
# because its dependencies had landed under the other spelling. That last one
# cost two rounds of debugging a build that had said it succeeded.
#
# Measured directly in a clone under /tmp: building the absolute path logs both
# spellings in a single run, building `StyloMail.slnx` after cd into the repo
# root logs one. The runners already used the relative form for `dotnet run`, so
# this only makes the build agree with them. The two absolute-path runs in the
# shared repo's own tree never failed, because /Users/... is not a symlink and
# there was only ever one spelling there: the defect existed only in a clone,
# which is exactly where a fresh user starts.
#
# ONE RETRY is kept for a different cause of the same symptoms, a genuinely
# concurrent build by another agent in the shared tree, which this fleet does
# routinely and a relative path cannot fix. It cannot hide a real failure: a
# genuine compile error fails both attempts, and both exit codes and the second
# log path are reported.
#
# -p:ProduceReferenceAssembly=true is deliberately NOT passed. overview-
# suggested it after their clean-checkout runs needed it, but the property
# already reads true in this tree (measured with `dotnet msbuild
# -getProperty:ProduceReferenceAssembly` on the Host, Core, Desktop and two test
# projects). Forcing it treats a symptom of the identity clash above, and the
# clash is the thing worth removing.
# The build on its own, so the retry runs the identical command rather than a
# copy that can drift. cd into the repo root and name the solution relatively,
# which is the whole point: see the note above.
console_dotnet_build() {
    ( cd "$CONSOLE_REPO" && dotnet build StyloMail.slnx --nologo 2>&1 )
}

console_build_all() {
    local log_dir="${CONSOLE_BUILD_LOG_DIR:-/tmp/stylomail-console-build}"
    local log="$log_dir/solution.log"
    mkdir -p "$log_dir"
    echo "== building StyloMail.slnx for the console harness =="
    console_dotnet_build | tee "$log"
    local status="${PIPESTATUS[0]}"
    if [[ $status -eq 0 ]]; then
        return 0
    fi

    echo "build failed with exit $status. Full log: $log" >&2
    # The last pattern is the file collision's own words: this SDK writes it as
    # a bare `error : System.IO.IOException` with no MSB code at all, so a list
    # of codes alone missed it, and a run that needed the retry did not get one.
    if ! grep -qE 'CS0006|CS0234|MSB3026|MSB4018|being used by another process' "$log"; then
        return "$status"
    fi

    echo "That reads as two builds colliding over one tree, not a compile error." >&2
    echo "Retrying once:" >&2
    console_dotnet_build | tee "$log_dir/solution-retry.log"
    status="${PIPESTATUS[0]}"
    if [[ $status -ne 0 ]]; then
        echo "build failed again with exit $status, so this is probably not a" >&2
        echo "collision. Full log: $log_dir/solution-retry.log" >&2
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
# Refuses to start a Nimble Host when the local model is not answering.
#
# Ollama on 11435 is a precondition of that shape and not a dependency this
# harness can install, so the run stops with the reason rather than declining
# every message for a cause nothing on the console's screen names. The model tag
# is checked as well as the server: a machine with the server up and the tag
# missing answers /api/tags happily and then fails every generate, which is the
# same red run for a different reason, and the difference is one line here.
#
# Both checks read the endpoint's default. A runner that overrides
# StyloMail__Nimble__Endpoint owns its own precondition and this will not see it.
console_require_local_model() {
    local tags
    tags="$(curl -fsS --max-time 3 "$CONSOLE_NIMBLE_TAGS" 2>/dev/null)" || {
        echo "CONSOLE_PROVIDER=nimble needs the local decision model, and nothing answered" >&2
        echo "at $CONSOLE_NIMBLE_TAGS. Start it with 'ollama serve' and try again." >&2
        return 1
    }

    if ! grep -q '"nimble' <<<"$tags"; then
        echo "Ollama is answering at $CONSOLE_NIMBLE_TAGS but holds no nimble model, so every" >&2
        echo "assessment would fail. Pull it first: ollama pull nimble" >&2
        return 1
    fi

    return 0
}

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

    # The assessment credential, both halves or neither.
    #
    # Neither is one of this harness's real states rather than an accident, so
    # it is a switch like CONSOLE_TRAFFIC. With no credential the Host composes
    # UnavailableMailAssessor, which throws on every message it is handed, and
    # readiness answers 503 not_ready with assessor_unavailable in the failed
    # checks. That is the state the console's failed-checks block exists for and
    # the only state that reaches it, so ux-scripts/run-console-not-ready-smoke.sh
    # turns it off on purpose. One secret without the other is a misconfiguration
    # that refuses to start, so this never produces it.
    if [[ "${CONSOLE_ASSESSOR:-true}" == "true" ]]; then
        export STYLOMAIL_PROFILE_KEY="$(cat "$CONSOLE_RUN/data/profile.key")"

        # Which semantic provider, when there is one. A switch for the same
        # reason the other two are: it is a deployment shape rather than a
        # variation of one.
        #
        #   jev (the default) is the hosted provider, with a placeholder key and
        #      an endpoint that cannot answer. The pipeline therefore degrades to
        #      explicitly-unavailable evidence rather than throwing on a rejected
        #      key, and policy declines: the state the console must render as
        #      absent rather than as zero, and the only one reachable without the
        #      operator's provider key.
        #   nimble is the local decision model on Ollama, holding no credential
        #      at all, so the profile master key alone is a complete deployment
        #      (decisions 17 and 18) and the assessor is REAL rather than
        #      simulated. A provider that answers is what turns a declined
        #      assessment into an assessed one, which is the whole difference
        #      between a queue that is empty because nothing can be assessed and
        #      a queue with something in it.
        CONSOLE_PROVIDER="${CONSOLE_PROVIDER:-jev}"

        if [[ "$CONSOLE_PROVIDER" == "nimble" ]]; then
            export StyloMail__Assessment__Provider=Nimble

            # The Jev half is deliberately absent: this shape holds no provider
            # key, and a key nothing reads earns a warning on every boot that
            # would train a reader to ignore the one that matters.
            unset TYPESAFE_API_KEY
        else
            export TYPESAFE_API_KEY="not-a-real-key-harness-only"

            # Pointed at an address that cannot answer, so the failure is a
            # degraded assessment rather than a rejection.
            export StyloMail__Jev__Endpoint="http://127.0.0.1:9/v1/systemone"
        fi
    else
        unset TYPESAFE_API_KEY STYLOMAIL_PROFILE_KEY StyloMail__Assessment__Provider
        unset StyloMail__Jev__Endpoint
    fi

    export ASPNETCORE_URLS="$CONSOLE_BASE"

    # The local model has to be answering before a Nimble Host is worth
    # starting. Checked here rather than left to the assessment, because a
    # stopped Ollama produces a Host that declines every message for a reason
    # nothing on the console's screen names: the run would go red on assertions
    # about a queue, and the cause would be one process that was never started.
    if [[ "${CONSOLE_ASSESSOR:-true}" == "true" && "$CONSOLE_PROVIDER" == "nimble" ]]; then
        console_require_local_model || return 1
    fi

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

# Writes the principal key where curl can read it, and prints the path.
#
# The key must not be a curl argument. An argument list is readable by anything
# on the machine that can run ps, for as long as the call lives, and the rule
# this harness works under is that no secret is interpolated into a visible
# command. So the header goes in a file and curl reads it back with -H @file.
#
# The order is the part that is easy to get wrong. Redirecting into a file that
# already exists keeps that file's mode, so an earlier run's 0644 would still be
# 0644 while the key was written into it. This truncates first, chmods while the
# file is empty, and only then writes, so there is no instant in which the file
# holds the key and is readable by anyone else. The umask is belt as well as
# braces: it is what makes the window shut even if the chmod were removed.
#
# The caller passes the path it wants back. It lives in the run's scratch
# directory, and console_stop_host removes it, so a run does not leave a
# credential-bearing file behind. It is never echoed, and never named in an
# error: the reader of a .err file should see a status code.
console_auth_headers() {
    local key="$1"
    local file="${2:-$CONSOLE_RUN/auth.headers}"

    : > "$file"
    chmod 600 "$file"
    ( umask 077; printf 'X-StyloMail-Key: %s\n' "$key" > "$file" )

    printf '%s\n' "$file"
}

# Gives the throwaway Host something to group.
#
# A fresh Host has one principal and no companies, so the sidebar would show a
# single "Ungrouped" row and the grouping would be invisible to every
# assertion. Seeded through the API rather than by writing the database, so
# what the console reads is what the routes produce.
console_seed_management() {
    local key base headers
    key="$(cat "$CONSOLE_RUN/data/principal.key")"
    base="$CONSOLE_BASE"
    headers="$(console_auth_headers "$key")"

    local company
    company=$(curl -fsS -X POST \
        -H @"$headers" -H "Content-Type: application/json" \
        --data '{"name":"Acme","notes":"seeded by the console harness"}' \
        "$base/v1/companies" | sed -n 's/.*"companyId":"\([^"]*\)".*/\1/p')

    if [[ -z "$company" ]]; then
        echo "Could not seed a company; the sidebar will show one ungrouped sender." >&2
        return 0
    fi

    curl -fsS -X PUT \
        -H @"$headers" -H "Content-Type: application/json" \
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
    local headers
    headers="$(console_auth_headers "$key")"

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
        -H @"$headers" -H "Content-Type: application/json" \
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

# Seeds a corpus batch through the Host, when there is a batch to seed.
#
# corpus- owns the traffic and tools/corpus/; this harness consumes a batch
# through the Host's routes and never parses a CSV itself. So the boundary is
# one command rather than a shared file format, and the switch is shaped like
# CONSOLE_TRAFFIC: off by default, because a fresh clone has no corpus, and a
# run that seeded nothing while looking as though it had would read as coverage.
#
#   CONSOLE_CORPUS=/path/to/batch   seed this batch before driving the client
#
# The interface is the one pinned with corpus-: `seed --base-url <url>
# --key-file <path> --batch <dir>`, the key read from a path rather than passed
# as an argument, which is why what this hands over is the harness's own
# principal.key file and never the value. The default CLI path names the entry
# point this harness expects; corpus- owns the real one, and CONSOLE_CORPUS_CLI
# overrides it without an edit here. A batch that cannot be seeded stops the run
# rather than quietly producing a script that asserts about an empty queue.
console_seed_corpus() {
    if [[ -z "${CONSOLE_CORPUS:-}" ]]; then
        return 0
    fi

    local cli="${CONSOLE_CORPUS_CLI:-$CONSOLE_REPO/tools/corpus/corpus.py}"

    if [[ ! -f "$cli" ]]; then
        echo "CONSOLE_CORPUS is set but there is no corpus CLI at $cli." >&2
        echo "Set CONSOLE_CORPUS_CLI to it, or unset CONSOLE_CORPUS." >&2
        return 1
    fi

    if [[ ! -d "$CONSOLE_CORPUS" ]]; then
        echo "CONSOLE_CORPUS=$CONSOLE_CORPUS is not a directory." >&2
        return 1
    fi

    # Run it, or run it through python, rather than requiring either: the corpus
    # is another agent's tool and its shebang is not this file's business.
    local runner=("$cli")
    if [[ ! -x "$cli" ]]; then
        runner=(python3 "$cli")
    fi

    "${runner[@]}" seed \
        --base-url "$CONSOLE_BASE" \
        --key-file "$CONSOLE_RUN/data/principal.key" \
        --batch "$CONSOLE_CORPUS" || return 1

    echo "seeded the corpus batch at $CONSOLE_CORPUS"
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

    # The header file holds the principal key, so it goes with the Host. Every
    # runner already traps its way here on success, failure and interrupt, which
    # is why the removal lives here rather than in each runner's cleanup: one
    # place that cannot be forgotten when a fourth script is added.
    rm -f "${CONSOLE_RUN:-}/auth.headers" 2>/dev/null || true
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
    #
    # A run can decline it, and the not-ready run does. Loading a decision the
    # Host did not produce, on a Host whose whole point is that it cannot assess
    # anything, would put a fabricated explanation of a message beside a status
    # line saying no message can be explained. It does not disturb the assertion
    # itself (the fixture fills the detail pane, not the list), which is exactly
    # why it would go unnoticed in a screenshot.
    if [[ "${CONSOLE_DECISION_FIXTURE:-true}" == "true" ]]; then
        export STYLOMAIL_SMOKE_DECISION_FILE="$CONSOLE_REPO/ux-scripts/decision-fixture.json"
    else
        unset STYLOMAIL_SMOKE_DECISION_FILE
    fi
}
