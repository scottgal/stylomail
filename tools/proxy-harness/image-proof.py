#!/usr/bin/env python3
"""Builds and verifies the governed image-observation scenario.

This is harness-only. It never talks to a production route, never copies message content into an
artifact, and uses only the Python standard library. Three subcommands:

  build-message  writes a multipart message with one real content image and one 1x1 tracking pixel
  direct-hash    fetches the last INBOX message straight from the backend and reports its SHA-256
  verify-route   asserts the image route's facts and scans the response for leaked content

The verifier prints one content-free JSON summary on success and exits non-zero on the first
mismatch, naming the field and the observed value.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import imaplib
import json
import os
import sqlite3
import sys


def fail(message: str) -> None:
    raise SystemExit(message)


# ---------------------------------------------------------------------------------------------
# build-message
# ---------------------------------------------------------------------------------------------

def build_message(args: argparse.Namespace) -> None:
    content = open(args.content, "rb").read()
    tracking = open(args.tracking, "rb").read()
    boundary = "stylomail-image-proof"

    def part(filename: str, content_type: str, payload: bytes) -> str:
        encoded = base64.b64encode(payload).decode("ascii")
        lines = [encoded[index:index + 76] for index in range(0, len(encoded), 76)]
        return (
            f"--{boundary}\r\n"
            f"Content-Type: {content_type}; name=\"{filename}\"\r\n"
            f"Content-Disposition: attachment; filename=\"{filename}\"\r\n"
            "Content-Transfer-Encoding: base64\r\n\r\n"
            + "\r\n".join(lines)
            + "\r\n\r\n"
        )

    body = (
        f"From: {args.sender}\r\n"
        f"To: {args.recipient}\r\n"
        f"Message-ID: <{args.message_id}>\r\n"
        f"Subject: {args.subject}\r\n"
        "Date: Mon, 19 Oct 2026 12:00:00 +0000\r\n"
        "MIME-Version: 1.0\r\n"
        f"Content-Type: multipart/mixed; boundary=\"{boundary}\"\r\n\r\n"
        f"--{boundary}\r\n"
        "Content-Type: text/plain; charset=utf-8\r\n\r\n"
        f"{args.body_token}\r\n\r\n"
        + part(args.content_name, "image/png", content)
        + part(args.tracking_name, "image/png", tracking)
        + f"--{boundary}--\r\n"
    )
    with open(args.out, "wb") as stream:
        stream.write(body.encode("utf-8"))

    # A manifest lets the caller name the expectations without restating the fixture sizes.
    manifest = {
        "content": os.path.basename(args.content),
        "contentBytes": len(content),
        "tracking": os.path.basename(args.tracking),
        "trackingBytes": len(tracking),
        "messageBytes": len(body.encode("utf-8")),
        "messageId": args.message_id,
    }
    with open(args.manifest, "w", encoding="utf-8") as stream:
        json.dump(manifest, stream, indent=2)
        stream.write("\n")


# ---------------------------------------------------------------------------------------------
# direct-hash
# ---------------------------------------------------------------------------------------------

def direct_hash(args: argparse.Namespace) -> None:
    password = os.environ.get(args.password_env)
    if not password:
        fail(f"{args.password_env} is absent; the direct backend fetch was not attempted.")

    client = imaplib.IMAP4(args.host, args.port)
    try:
        client.login(args.user, password)
        status, selected = client.select(args.mailbox, readonly=True)
        if status != "OK":
            fail(f"Backend SELECT failed with status {status}.")
        count = int(selected[0])
        if count < 1:
            fail("Backend INBOX is empty; the direct relay comparison has no message.")
        status, data = client.fetch(str(count), "(RFC822)")
        if status != "OK" or not data or not isinstance(data[0], tuple):
            fail(f"Backend direct FETCH failed with status {status}.")
        raw = data[0][1]
    finally:
        try:
            client.logout()
        except Exception:
            pass

    print(json.dumps({
        "bytes": len(raw),
        "sha256": hashlib.sha256(raw).hexdigest(),
    }, sort_keys=True))


# ---------------------------------------------------------------------------------------------
# verify-route
# ---------------------------------------------------------------------------------------------

FORBIDDEN_KEYS = {
    "address",
    "body",
    "content",
    "data",
    "filename",
    "from",
    "raw",
    "rawbytes",
    "subject",
    "to",
}


def check_keys(document, path: str = "$") -> None:
    """Refuses a response shape that names a content field, even when its value is absent."""
    if isinstance(document, dict):
        for key, value in document.items():
            if key.lower().replace("_", "") in FORBIDDEN_KEYS:
                fail(f"Route response carries forbidden key {path}.{key}.")
            check_keys(value, f"{path}.{key}")
    elif isinstance(document, list):
        for index, value in enumerate(document):
            check_keys(value, f"{path}[{index}]")


def verify_route(args: argparse.Namespace) -> None:
    route = json.load(open(args.route, encoding="utf-8"))
    text = open(args.route, encoding="utf-8").read()
    content = open(args.content, "rb").read()
    tracking = open(args.tracking, "rb").read()
    check_keys(route)

    def expect(path: str, actual, expected) -> None:
        if actual != expected:
            fail(f"{path}: expected {expected!r}, observed {actual!r}")

    expect("provenance", route.get("provenance"), "proxy_image_observation/1")
    expect("window", route.get("window"), "last_24_hours")

    messages = route.get("messages")
    if not isinstance(messages, list):
        fail("Route response has no messages array.")

    if args.mode == "disabled":
        expect("imagesEnabled", route.get("imagesEnabled"), False)
        if len(messages) != 1:
            fail(f"Disabled route expected one retrieved message, observed {len(messages)}.")
        message = messages[0]
        expect("messages[0].state", message.get("state"), "disabled")
        expect("messages[0].trafficKind", message.get("trafficKind"), "Retrieval")
        expect("messages[0].observations", message.get("observations"), [])
        if args.database:
            check_no_image_rows(args.database)
        scan(text, args, content, tracking)
        if args.database:
            scan_image_tables(args.database, args, content, tracking)
        summary = {
            "mode": "disabled",
            "imagesEnabled": False,
            "messageState": message.get("state"),
            "observations": 0,
            "imageRows": 0,
            "leakScan": "clean",
        }
        emit_summary(args, summary)
        return

    expect("imagesEnabled", route.get("imagesEnabled"), True)
    expect("retentionDays", route.get("retentionDays"), 30)
    if len(messages) != 1:
        fail(f"Enabled route expected one retrieved message, observed {len(messages)}.")

    message = messages[0]
    expect("messages[0].trafficKind", message.get("trafficKind"), "Retrieval")
    expect("messages[0].state", message.get("state"), "clean")
    observations = message.get("observations")
    if not isinstance(observations, list) or len(observations) != 1:
        fail(f"Expected exactly one observation, observed {observations!r}.")
    observation = observations[0]

    expect("observation.state", observation.get("state"), "clean")
    expect("observation.availability", observation.get("availability"), "available")
    expect("observation.availabilityReason", observation.get("availabilityReason"), "none")
    expect("observation.sourceVersion", observation.get("sourceVersion"), "stylomail-image/2")
    expect("observation.firstOverBudgetOrdinal", observation.get("firstOverBudgetOrdinal"), None)
    expect("observation.lastOverBudgetOrdinal", observation.get("lastOverBudgetOrdinal"), None)
    expect("observation.expiresAt < observedAt", observation.get("expiresAt") is not None, True)

    counts = observation.get("counts") or {}
    expect("counts.imageParts", counts.get("imageParts"), 2)
    expect("counts.imagesSkippedAsChrome", counts.get("imagesSkippedAsChrome"), 1)
    expect("counts.imagesAdmitted", counts.get("imagesAdmitted"), 1)
    expect("counts.imagesProduced", counts.get("imagesProduced"), 1)
    expect("counts.imagesNotProduced", counts.get("imagesNotProduced"), 0)
    expect("counts.imagesOverBudget", counts.get("imagesOverBudget"), 0)

    parts = observation.get("parts")
    if not isinstance(parts, list) or len(parts) != 1:
        fail(f"Expected one admitted part record, observed {parts!r}.")
    part = parts[0]

    # text/plain is part 2, the content image is 3, the tracking pixel is 4. The skipped pixel
    # must not appear as a part record, and the admitted ordinal must be the trusted walk ordinal.
    expect("part.ordinal", part.get("ordinal"), 3)
    expect("part.disposition", part.get("disposition"), "inspected")
    expect("part.availability", part.get("availability"), "available")
    expect("part.availabilityReason", part.get("availabilityReason"), "none")
    expect("part.format", part.get("format"), "png")
    expect("part.declaredFormat", part.get("declaredFormat"), "png")
    expect("part.width", part.get("width"), 64)
    expect("part.height", part.get("height"), 64)
    expect("part.pixelCount", part.get("pixelCount"), 4096)
    expect("part.findings", part.get("findings"), [])
    expect("part.imageEndOffset", part.get("imageEndOffset"), len(content))
    expect("part.trailingByteCount", part.get("trailingByteCount"), 0)
    expect("part.trailingBytesObserved", part.get("trailingBytesObserved"), None)
    expect("part.trailingScanComplete", part.get("trailingScanComplete"), True)

    scan(text, args, content, tracking)
    if args.database:
        check_image_rows(args.database)
        scan_image_tables(args.database, args, content, tracking)

    summary = {
        "mode": "enabled",
        "imagesEnabled": True,
        "messageState": message.get("state"),
        "observationState": observation.get("state"),
        "availability": observation.get("availability"),
        "counts": counts,
        "part": {
            "ordinal": part.get("ordinal"),
            "format": part.get("format"),
            "width": part.get("width"),
            "height": part.get("height"),
            "pixelCount": part.get("pixelCount"),
            "findings": part.get("findings"),
            "imageEndOffset": part.get("imageEndOffset"),
        },
        "leakScan": "clean",
    }
    emit_summary(args, summary)


def check_no_image_rows(database: str) -> None:
    with sqlite3.connect(database) as connection:
        outcomes = connection.execute("SELECT COUNT(*) FROM proxy_image_outcome;").fetchone()[0]
        parts = connection.execute("SELECT COUNT(*) FROM proxy_image_part;").fetchone()[0]
    if outcomes != 0 or parts != 0:
        fail(f"Images disabled still wrote {outcomes} outcome rows and {parts} part rows.")


def check_image_rows(database: str) -> None:
    with sqlite3.connect(database) as connection:
        outcomes = connection.execute("SELECT COUNT(*) FROM proxy_image_outcome;").fetchone()[0]
        parts = connection.execute("SELECT COUNT(*) FROM proxy_image_part;").fetchone()[0]
    if outcomes != 1 or parts != 1:
        fail(f"Images enabled wrote {outcomes} outcome rows and {parts} part rows; expected 1 and 1.")


def scan(text: str, args: argparse.Namespace, content: bytes, tracking: bytes) -> None:
    forbidden = forbidden_values(args, content, tracking)
    lowered = text.lower()
    for value in forbidden:
        if not value:
            continue
        if value in text or value.lower() in lowered:
            fail(f"Route response leaked a supplied value: {value[:48]!r}.")


def forbidden_values(args: argparse.Namespace, content: bytes, tracking: bytes) -> list[str]:
    values = list(args.forbid or [])
    values += [
        args.content_name,
        args.tracking_name,
        args.subject,
        args.body_token,
        args.sender,
        args.recipient,
        # Raw image material in the shapes a leak could take.
        content[:16].hex(),
        tracking[:16].hex(),
        base64.b64encode(content)[:32].decode("ascii"),
        base64.b64encode(tracking)[:24].decode("ascii"),
        "89504e470d0a1a0a",
        "iVBORw0KGgo",
    ]
    return values


def scan_image_tables(database: str, args: argparse.Namespace, content: bytes, tracking: bytes) -> None:
    """The persisted observation is content-free in every text column, not just on the wire."""
    values = forbidden_values(args, content, tracking)
    with sqlite3.connect(database) as connection:
        for table in ("proxy_image_outcome", "proxy_image_part"):
            columns = [row[1] for row in connection.execute(f"PRAGMA table_info({table})")]
            for row in connection.execute(f"SELECT * FROM {table}"):
                for column, value in zip(columns, row):
                    if not isinstance(value, str):
                        continue
                    for supplied in values:
                        if supplied and supplied in value:
                            fail(f"{table}.{column} carries a supplied value: {supplied[:48]!r}.")


def emit_summary(args: argparse.Namespace, summary: dict) -> None:
    if args.summary:
        with open(args.summary, "w", encoding="utf-8") as stream:
            json.dump(summary, stream, indent=2, sort_keys=True)
            stream.write("\n")
    print(json.dumps(summary, sort_keys=True))


# ---------------------------------------------------------------------------------------------
# entry point
# ---------------------------------------------------------------------------------------------

def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    build = subparsers.add_parser("build-message")
    build.add_argument("--out", required=True)
    build.add_argument("--manifest", required=True)
    build.add_argument("--content", required=True)
    build.add_argument("--tracking", required=True)
    build.add_argument("--content-name", default="harness-content.png")
    build.add_argument("--tracking-name", default="harness-tracker.png")
    build.add_argument("--sender", required=True)
    build.add_argument("--recipient", required=True)
    build.add_argument("--message-id", required=True)
    build.add_argument("--subject", default="Image observation proof")
    build.add_argument("--body-token", default="HARNESS-IMAGE-BODY-7c1f")
    build.set_defaults(handler=build_message)

    direct = subparsers.add_parser("direct-hash")
    direct.add_argument("--host", required=True)
    direct.add_argument("--port", required=True, type=int)
    direct.add_argument("--user", required=True)
    direct.add_argument("--password-env", required=True)
    direct.add_argument("--mailbox", default="INBOX")
    direct.set_defaults(handler=direct_hash)

    verify = subparsers.add_parser("verify-route")
    verify.add_argument("--mode", choices=["enabled", "disabled"], required=True)
    verify.add_argument("--route", required=True)
    verify.add_argument("--summary")
    verify.add_argument("--database")
    verify.add_argument("--content", required=True)
    verify.add_argument("--tracking", required=True)
    verify.add_argument("--content-name", default="harness-content.png")
    verify.add_argument("--tracking-name", default="harness-tracker.png")
    verify.add_argument("--sender", required=True)
    verify.add_argument("--recipient", required=True)
    verify.add_argument("--subject", default="Image observation proof")
    verify.add_argument("--body-token", default="HARNESS-IMAGE-BODY-7c1f")
    verify.add_argument("--forbid", action="append", default=[])
    verify.set_defaults(handler=verify_route)

    args = parser.parse_args()
    args.handler(args)


if __name__ == "__main__":
    main()
