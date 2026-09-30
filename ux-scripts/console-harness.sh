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

# And where it is asked to generate, which is the same server's other route. Two
# constants rather than one derived from the other, because they are two facts
# about a deployment that a misconfiguration can hold apart, and the local-model
# check below only means anything when they agree with the Host.
CONSOLE_NIMBLE_GENERATE="${CONSOLE_NIMBLE_GENERATE:-http://127.0.0.1:11435/api/generate}"

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

    # Fresh keys for a fresh deployment, which is every runner but one.
    #
    # CONSOLE_REUSE_KEYS exists for the one that follows a Host through a
    # restart (run-console-feed-recovery-smoke.sh). A Host that goes away and
    # comes back is only the same Host if the console can still authenticate to
    # it, and a second start that regenerated the principal key would produce a
    # console whose key is refused: the run would then fail on an authentication
    # assertion and read as "recovery is broken" when it had actually tested a
    # new deployment. So the switch keeps the keys the first start wrote, and it
    # defaults to off so that every other run still gets keys nobody has seen.
    #
    # The database is not regenerated either way: it is not generated here at
    # all, only named at $CONSOLE_RUN/data/host.db, so a restart inherits it
    # exactly as this does. That is what makes the restart the same Host twice
    # rather than a second one, and it is also why a runner using this switch
    # must wipe its own scratch directory before the first start.
    if [[ "${CONSOLE_REUSE_KEYS:-false}" == "true" &&
          -s "$CONSOLE_RUN/data/profile.key" &&
          -s "$CONSOLE_RUN/data/principal.key" ]]; then
        : # The keys this run already has, kept for the restart.
    else
        head -c 32 /dev/urandom | xxd -p | tr -d '\n' > "$CONSOLE_RUN/data/profile.key"
        head -c 24 /dev/urandom | xxd -p | tr -d '\n' > "$CONSOLE_RUN/data/principal.key"
    fi

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

            # Which address the local provider is asked at. Defaulted to the
            # local model, and overridable so that "the provider is selected and
            # cannot answer" is a shape this harness can start rather than one
            # it can only be told about.
            #
            # The shape matters because two lanes measured different answers to
            # it and neither could start the other's: `ingress-` measured a
            # Nimble host whose model was down accepting a submission (202) with
            # the decision coming back Defer, and this lane measured 503 deferred
            # with the Jev provider pointed at an address nothing answers on. So
            # the provider is the variable, not the outage, and it has to be
            # varied on the route rather than argued about.
            #
            # It is a URL rather than a boolean named for one failure, because
            # "the model is down" is not one thing: an endpoint that refuses
            # connections is a HttpRequestException and the provider treats it as
            # transient, while a model the server does not hold is a 404 that
            # raises NimbleContractException (NimbleSemanticMailClassifier.cs:387)
            # and is a contract failure rather than an outage. A switch called
            # CONSOLE_MODEL_DOWN would silently pick one of those.
            export StyloMail__Nimble__Endpoint="${CONSOLE_NIMBLE_ENDPOINT:-$CONSOLE_NIMBLE_GENERATE}"
        else
            export TYPESAFE_API_KEY="not-a-real-key-harness-only"

            # Pointed at an address that cannot answer, so the failure is a
            # degraded assessment rather than a rejection.
            export StyloMail__Jev__Endpoint="http://127.0.0.1:9/v1/systemone"

            # Neither shape is the other's: a Jev Host with a Nimble endpoint
            # left in its environment is a deployment nobody wrote down.
            unset StyloMail__Nimble__Endpoint
        fi
    else
        unset TYPESAFE_API_KEY STYLOMAIL_PROFILE_KEY StyloMail__Assessment__Provider
        unset StyloMail__Jev__Endpoint StyloMail__Nimble__Endpoint
    fi

    export ASPNETCORE_URLS="$CONSOLE_BASE"

    # The local model has to be answering before a Nimble Host is worth
    # starting. Checked here rather than left to the assessment, because a
    # stopped Ollama produces a Host that declines every message for a reason
    # nothing on the console's screen names: the run would go red on assertions
    # about a queue, and the cause would be one process that was never started.
    # Skipped when the endpoint was overridden away from the local model, and
    # that exception is the point of the override rather than a hole in the
    # check: asking a Host pointed at another address whether the local model is
    # up answers a question about a server it was never going to call. The check
    # stays for every run that means a working local assessor, which is all of
    # them but this shape.
    if [[ "${CONSOLE_ASSESSOR:-true}" == "true" && "$CONSOLE_PROVIDER" == "nimble" ]]; then
        if [[ "$StyloMail__Nimble__Endpoint" == "$CONSOLE_NIMBLE_GENERATE" ]]; then
            console_require_local_model || return 1
        else
            echo "note: the local provider is pointed at $StyloMail__Nimble__Endpoint rather than the" >&2
            echo "local model, so the model check is skipped by request (CONSOLE_NIMBLE_ENDPOINT)." >&2
        fi
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
    #
    # CONSOLE_HOST_LOG names where this start writes, so a run that starts the
    # Host twice (the recovery run) keeps both logs instead of the second one
    # overwriting the first. A restart whose log had been replaced is a run
    # nobody can diagnose, and the restart is the interesting start.
    local host_log="${CONSOLE_HOST_LOG:-$CONSOLE_RUN/host.log}"
    "$CONSOLE_HOST_APP" serve > "$host_log" 2>&1 &
    CONSOLE_HOST_PID=$!

    for _ in $(seq 1 90); do
        if curl -fsS --max-time 2 "$CONSOLE_BASE/health/live" >/dev/null 2>&1; then
            return 0
        fi
        if ! kill -0 "$CONSOLE_HOST_PID" 2>/dev/null; then
            echo "The harness Host exited before becoming live. Last lines:" >&2
            tail -20 "$host_log" >&2
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
# This reads the key itself, from the run's own scratch directory, and takes no
# key argument. That is deliberate and it is the whole point of the signature: a
# parameter is a value the caller has to hold, and a caller that holds a value
# can eventually pass it to something else. Bash function arguments are not
# process arguments (a function call does not fork, so nothing appears in ps),
# and this file never handed the key to an external command, but a signature
# that requires the value is a shape that only stays safe while every caller
# stays careful. One function, one source, nothing to hand over. Flagged by
# `article-` from a background review of the seeders' call sites.
#
# What this does not change, because it is not a command line and not fixable
# here: the key is handed to the Host and to the console through their
# *environment* (console_start_host and console_export_app_env), which is how a
# Host is configured at all, and a child process's environment is readable by the
# same user who started it. Argument lists are the exposure this harness can
# close, and it closes them.
#
# The order is the part that is easy to get wrong. Redirecting into a file that
# already exists keeps that file's mode, so an earlier run's 0644 would still be
# 0644 while the key was written into it. This truncates first, chmods while the
# file is empty, and only then writes, so there is no instant in which the file
# holds the key and is readable by anyone else. The umask is belt as well as
# braces: it is what makes the window shut even if the chmod were removed.
#
# The caller passes the path it wants back, or nothing for the default. It lives
# in the run's scratch directory, and console_stop_host removes it, so a run does
# not leave a credential-bearing file behind. It is never echoed, and never named
# in an error: the reader of a .err file should see a status code.
console_auth_headers() {
    local key file
    key="$(cat "$CONSOLE_RUN/data/principal.key")"
    file="${1:-$CONSOLE_RUN/auth.headers}"

    : > "$file"
    chmod 600 "$file"
    ( umask 077; printf 'X-StyloMail-Key: %s\n' "$key" > "$file" )

    printf '%s\n' "$file"
}

# Pauses a sender through the Host's own control route, without the console.
#
# Used by exactly one runner, and for a reason that is the whole point of it: a
# change applied while the console's feed is down can only appear on the screen
# if the console re-read the surface afterwards. Applied through the console it
# would prove nothing (the console would already know), so the change has to
# come from outside, and the route is the honest outside rather than a write
# straight into the database.
#
# POST /v1/controls/senders/{id}/pause needs the Administer privilege, which
# console_start_host grants this principal. The reason is required here even
# though the route accepts an empty one: it is the audit record for the
# intervention, and a scripted pause with none is, later, indistinguishable
# from one nobody explained.
#
# Prints nothing on success and returns non-zero on failure, so a caller can
# stop before asserting anything at a Host whose control plane did not answer.
console_pause_sender() {
    local principal="${1:-harness}"
    local reason="${2:-paused by the console harness while the console was blind}"
    local headers status

    headers="$(console_auth_headers)"

    status=$(curl -sS -o /dev/null -w '%{http_code}' -X POST \
        -H @"$headers" -H "Content-Type: application/json" \
        --data "{\"reason\":\"$reason\"}" \
        "$CONSOLE_BASE/v1/controls/senders/$principal/pause") || return 1

    if [[ "$status" != "200" ]]; then
        echo "Pausing $principal answered $status, so the control route did not" >&2
        echo "apply it. The recovery run is about to assert that the console" >&2
        echo "re-read the surface, and it cannot do that over a change that never" >&2
        echo "happened." >&2
        return 1
    fi

    return 0
}

# Gives the throwaway Host something to group.
#
# A fresh Host has one principal and no companies, so the sidebar would show a
# single "Ungrouped" row and the grouping would be invisible to every
# assertion. Seeded through the API rather than by writing the database, so
# what the console reads is what the routes produce.
console_seed_management() {
    local base headers
    base="$CONSOLE_BASE"
    headers="$(console_auth_headers)"

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
#
# One limit of this route, measured 2026-09-30, worth knowing before a seeded
# ledger entry is read as complete: an assessment hands the pipeline an ephemeral
# payload rather than spooling the message, so the MIME layer never runs and
# assessment.deterministic_extraction is Unavailable whichever provider is
# composed. Only POST /v1/submissions spools the bytes and runs that layer, so
# this call cannot carry a planted deterministic fact at all, and decision 24's
# corroboration gate is not exercisable through it. console_seed_submission below
# is the route that does.
console_seed_decision() {
    local base="$1"
    local headers
    headers="$(console_auth_headers)"

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

# Submits one message through the write path, and reports what the route did.
#
# This is POST /v1/submissions, which is not the same route as the assessment
# call above and does not reach the same pane. An assessment transfers no
# responsibility: it writes a decision to the ledger and leaves every queue
# listing empty. A submission transfers responsibility, answers with a queue id,
# and is the only thing that puts a row in the middle pane.
#
# The route refuses rather than accepting and holding on the shapes measured here
# (2026-09-30; ux-scripts/state-coverage.md has the table, and one unreconciled
# shape where another lane measured it accepting with a Defer decision), so a
# submission that produced no queue id is reported as a failure of the run rather
# than carried past. A run that continued would assert about an empty pane and
# report the emptiness as a console defect.
#
# The envelope is passed in rather than parsed out of the file: the route takes
# mailFrom and rcptTo as fields, and a helper that read them out of the MIME
# would be measuring its own parser as much as the Host. The idempotency key is
# per-run, because a replayed key returns the earlier submission instead of
# assessing again, which would turn a second submission into a read of the
# first one's answer.
console_seed_submission() {
    local mime="$1" mail_from="$2" rcpt_to="$3"
    local headers
    headers="$(console_auth_headers)"

    if [[ ! -f "$mime" ]]; then
        echo "No message to submit at $mime." >&2
        return 1
    fi

    # A local model answers in seconds and the assessment asks it more than one
    # question, so the deadline follows the provider rather than a fixed value
    # chosen for the hosted one.
    local timeout="${CONSOLE_SUBMIT_TIMEOUT:-60}"
    if [[ "${CONSOLE_PROVIDER:-jev}" == "nimble" ]]; then
        timeout="${CONSOLE_SUBMIT_TIMEOUT:-300}"
    fi

    local raw
    raw="$(base64 -i "$mime" | tr -d '\n')"

    python3 - "$raw" "$mail_from" "$rcpt_to" > "$CONSOLE_RUN/submission.json" <<'PYEOF'
import json, sys
print(json.dumps({
    "direction": "Inbound",
    "mailFrom": sys.argv[2],
    "rcptTo": [sys.argv[3]],
    "rawMime": sys.argv[1],
    "connectingIp": "198.51.100.9",
}))
PYEOF

    local code
    code="$(curl -sS -o "$CONSOLE_RUN/submission-post.json" -w '%{http_code}' \
        --max-time "$timeout" \
        -H @"$headers" \
        -H "Content-Type: application/json" \
        -H "Idempotency-Key: console-$$" \
        --data @"$CONSOLE_RUN/submission.json" \
        "$CONSOLE_BASE/v1/submissions" 2>"$CONSOLE_RUN/submission-post.err")"

    if [[ "$code" != "200" && "$code" != "201" && "$code" != "202" ]]; then
        echo "POST /v1/submissions answered HTTP $code, so nothing was queued." >&2
        # The body, not the request. An error body is the route's own reason for
        # refusing, and the reason is the thing worth reading.
        cat "$CONSOLE_RUN/submission-post.json" >&2
        echo >&2
        return 1
    fi

    # Read with python rather than sed: the response is one line and a greedy
    # match would take the last id on it, which is the assessment's rather than
    # the queue's.
    #
    # One value per line, read a line at a time, rather than fields joined by a
    # separator. Tab is IFS whitespace, so `read` collapses runs of it and the
    # empty fields shift: the first version of this joined the four values with
    # tabs and reported the *delivery state* as the message state, because the
    # two empty fields between them collapsed away. A line is not collapsible.
    local fields=()
    while IFS= read -r line; do fields+=("$line"); done < <(python3 - "$CONSOLE_RUN/submission-post.json" <<'PYEOF'
import json, sys
try:
    body = json.load(open(sys.argv[1]))
except Exception:
    raise SystemExit(0)
recipients = body.get("recipients") or []
print(body.get("queueId") or "")
print(body.get("status") or "")
print(recipients[0].get("action", "") if recipients else "")
print(recipients[0].get("deliveryState", "") if recipients else "")
PYEOF
)

    # Reported to the caller through these rather than through stdout, so a
    # runner does not have to parse this file a second time and cannot parse it
    # a second, different way. The response carries no state for the message as
    # a whole: it carries `status` for the submission and a delivery state per
    # recipient, which is why the two are read from different places here.
    CONSOLE_SUBMISSION_QUEUE_ID="${fields[0]:-}"
    CONSOLE_SUBMISSION_STATUS="${fields[1]:-}"
    CONSOLE_SUBMISSION_ACTION="${fields[2]:-}"
    CONSOLE_SUBMISSION_DELIVERY="${fields[3]:-}"

    if [[ -z "$CONSOLE_SUBMISSION_QUEUE_ID" ]]; then
        echo "The route accepted the message but returned no queue id, so delivery" >&2
        echo "responsibility did not transfer and no listing will carry it. Body:" >&2
        cat "$CONSOLE_RUN/submission-post.json" >&2
        echo >&2
        return 1
    fi

    echo "submitted queue $CONSOLE_SUBMISSION_QUEUE_ID: status $CONSOLE_SUBMISSION_STATUS," \
        "action $CONSOLE_SUBMISSION_ACTION, delivery $CONSOLE_SUBMISSION_DELIVERY"
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

# The run's verdict, read from result.json, and the status a runner should exit
# with.
#
# The exit code of the harness process is not its verdict. Measured 2026-10-01:
# a script whose twenty-sixth action failed printed "Result: FAIL", wrote
# `"success": false` to result.json, and `dotnet run` still exited 0. Every
# runner in this directory did `exit $STATUS` on that code, so a console that
# failed every assertion would have been reported to the fleet as a passing run:
# a green tick with the evidence in a file nobody read. That is README lesson 8
# ("a silently swallowed failure is invisible to a harness too") turned on the
# runner, and it is the reason the failing run and the passing run were
# indistinguishable from the outside.
#
# So the verdict is read where it is written. The process status is still
# honoured, because that is what catches a run that never got as far as writing
# result.json, and a missing file is itself a failure rather than an absence of
# one.
#
# Returning rather than exiting, so the caller's `exit` is visible at the call
# site and a runner cannot exit 0 by forgetting to propagate anything.
console_final_status() {
    local file="$1" run_status="${2:-0}"

    if [[ ! -f "$file" ]]; then
        echo "No result.json at $file, so the run did not finish." >&2
        return 1
    fi

    python3 - "$file" <<'PYEOF'
import json, sys

try:
    body = json.load(open(sys.argv[1]))
except Exception as error:
    print(f"result.json is not readable JSON: {error}", file=sys.stderr)
    raise SystemExit(2)

# actionResults, not actions. An earlier reader of mine asked for "actions" and
# reported every run as "0 actions, 0 failed", which is a passing count for a
# run that did nothing: the same defect in a smaller place.
#
# And the schema is nested, which is the second thing a reader here has to know:
# an entry is {"action": {"type": <int>, "target", "matcher", "value"}, "success",
# "duration", "errorMessage", "metrics"}. The failure's text is at the entry's top
# level and the fields that identify it are one level down. The first version of
# this read target and type off the top level and printed "failed: ? ::" for the
# one action that mattered, which is a reader that cannot tell you what broke.
actions = body.get("actionResults") or []
failed = [a for a in actions if not a.get("success", True)]

print(f"verdict: success={body.get('success')}, {len(actions)} actions, {len(failed)} failed")
for entry in failed[:5]:
    action = entry.get("action") or {}
    where = " ".join(
        part for part in (action.get("target"), action.get("matcher")) if part
    )
    reason = entry.get("errorMessage") or entry.get("message") or ""
    print(f"    failed: {where or '(no action recorded)'} :: {reason}".rstrip())

raise SystemExit(0 if body.get("success") else 1)
PYEOF
    local verdict=$?

    if (( run_status != 0 )); then
        echo "the harness process exited $run_status, so the verdict above is not the" >&2
        echo "whole story" >&2
        return "$run_status"
    fi

    return "$verdict"
}
