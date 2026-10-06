"""Exact Avalonia telemetry collector identity checks for verify-console-trends.sh."""

import os
import shlex
from pathlib import Path


COLLECTOR_NAME = "Avalonia.BuildServices.Collector.dll"


def trusted_paths(package_cache=None):
    cache = package_cache or os.environ.get("NUGET_PACKAGES") or os.path.join(
        os.path.expanduser("~"), ".nuget", "packages"
    )
    root = (
        Path(cache)
        / "avalonia.buildservices"
        / "11.3.2"
        / "tools"
        / "netstandard2.0"
    ).resolve()
    return root, root / COLLECTOR_NAME


def classify_identity(comm, observed_pgid, expected_pgid, tokens, package_root, collector_file):
    if comm != "dotnet":
        return "executable-mismatch"
    if observed_pgid != str(expected_pgid):
        return "pgid-mismatch"
    if tokens.count("exec") != 1:
        return "exec-shape-mismatch"

    paths = [
        token.rstrip(",:")
        for token in tokens
        if os.path.basename(token.rstrip(",:")).lower() == COLLECTOR_NAME.lower()
    ]
    if not paths:
        return "collector-argument-missing"

    unresolved = False
    outside_root = False
    for value in paths:
        try:
            candidate = Path(value).resolve(strict=True)
        except (OSError, RuntimeError):
            unresolved = True
            continue
        try:
            candidate.relative_to(package_root)
        except ValueError:
            outside_root = True
            continue
        if candidate == collector_file:
            return "verified"
    if outside_root:
        return "collector-path-outside-trusted-root"
    if unresolved:
        return "collector-path-unresolved"
    return "collector-path-not-exact-installed-target"


def probe_process(pid, expected_pgid, ps_text, package_root=None, collector_file=None):
    """Return a safe identity code; never return or log the argument string."""
    try:
        if package_root is None or collector_file is None:
            package_root, collector_file = trusted_paths()
        identity = ps_text(
            ["ps", "-o", "pid=,pgid=,comm=", "-p", str(pid)]
        ).strip().split(None, 2)
        if len(identity) != 3 or identity[0] != str(pid):
            return "identity-probe-unavailable"
        arguments = ps_text(["ps", "-o", "args=", "-p", str(pid)])
        tokens = shlex.split(arguments)
        return classify_identity(
            identity[2], identity[1], str(expected_pgid), tokens, package_root, collector_file
        )
    except Exception:
        return "identity-probe-unavailable"
