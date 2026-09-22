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
CONSOLE_APP="$CONSOLE_REPO/src/StyloMail.Desktop/bin/Debug/net10.0/StyloMail.Desktop"
CONSOLE_HOST_APP="$CONSOLE_REPO/src/StyloMail.Host/bin/Debug/net10.0/StyloMail.Host"
CONSOLE_RUN="${CONSOLE_RUN:-/tmp/stylomail-console-ux}"

# Deliberately not 5000 (macOS AirPlay Receiver listens there) and not the
# value docs/running.md uses, so a harness run cannot be confused with a real
# deployment on this machine.
CONSOLE_PORT="${CONSOLE_PORT:-5271}"
CONSOLE_BASE="http://127.0.0.1:${CONSOLE_PORT}"

export DOTNET_ROOT="${DOTNET_ROOT:-/usr/local/share/dotnet}"
export PATH="/usr/local/share/dotnet:$PATH"

console_require_app() {
    if [[ ! -e "$CONSOLE_APP" && ! -x "$CONSOLE_APP" ]]; then
        echo "Build first: dotnet build src/StyloMail.Desktop/StyloMail.Desktop.csproj" >&2
        exit 1
    fi
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
        echo "Build the Host first: dotnet build src/StyloMail.Host/StyloMail.Host.csproj" >&2
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

    # And the decision body the detail pane renders.
    #
    # This is a fixture rather than something the Host produced, and that is a
    # limitation of the deployment rather than a shortcut: producing a decision
    # needs a semantic provider key, and a rejected key currently fails the
    # whole assessment request rather than degrading to unavailable evidence.
    # The harness must not hold the operator's real key, so the pane would
    # otherwise be the one surface no script could reach.
    #
    # Say so when reading a screenshot from this run. It shows the pane's
    # rendering, driven live, over a body the Host did not produce.
    export STYLOMAIL_SMOKE_DECISION_FILE="$CONSOLE_REPO/ux-scripts/decision-fixture.json"
}
