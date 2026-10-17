"""Ingest public phishing and ham corpora into weakly labelled segment rows.

What the labels mean, said plainly because the metrics depend on it:

* A message from the Nazario phishing corpus is malicious by the corpus's own label. The segment
  chosen for an element is the one whose text carries that element's cues; the label comes from the
  corpus, and the cue only decides which element the segment is evidence about.
* A message from the SpamAssassin ham corpora is benign. A ham segment carrying the same cues is a
  hard negative for that element; when an element has too few cue-bearing ham segments, the
  remainder is filled from other ham segments and marked as an easy negative.

Exemplar identifiers are opaque and carry no version: they are "ex:<element>:<text digest>", so the
same text keeps the same id across retrains and a renamed artefact never makes an id read as a
provenance claim. Provenance travels in scorer_version and in the source and licence fields of the
training rows.

This is weak supervision, not ground truth. A phishing message can contain a segment that is not a
credential request, and a ham message can quote one. The held-out numbers are therefore a statement
about this labelling rule as much as about the classifier, and are reported with that caveat.

PII: email addresses, phone numbers, URLs and long digit runs are replaced with tokens. Names and
free text inside salutations are left as they are; this is not a full redaction and is not claimed
as one. Corpus text is not committed: run this script to regenerate it.

    python ingest_corpora.py --out corpus-labels.jsonl --manifest corpus-manifest.json
"""

from __future__ import annotations

import argparse
import bz2
import email
import hashlib
import html
import io
import json
import mailbox
import pathlib
import os
import re
import tarfile
import tempfile
import urllib.request

ELEMENTS = (
    "credential_request",
    "urgency_pressure",
    "threat",
    "impersonated_greeting",
    "attachment_lure",
)

CUES = {
    "credential_request": [
        r"\b(confirm|verify|update|validate|restore|unlock)\b[^.\n]{0,60}\b(password|credentials|login|account|card|pin|identity)\b",
        r"\b(enter|provide|send|reply with|submit|sign in|log in)\b[^.\n]{0,60}\b(password|username|credentials|card number|pin|account number|online banking)\b",
    ],
    "urgency_pressure": [
        r"\b(within|in the next|in less than)\s+\d+\s*(hour|hours|minute|minutes|day|days)\b",
        r"\b(immediate(ly)?|urgent|final notice|last chance|act now|act today|expires? (today|soon)|suspend(ed)?|terminat(ed|ion)|deleted|disable(d)?)\b",
        r"\b(24|48|72)\s*hours?\b",
    ],
    "threat": [
        r"\b(legal action|lawsuit|court|prosecut\w*|police|arrest|imprison\w*)\b",
        r"\b(ransom\w*|bitcoin|btc|encrypt(ed|ion)|locked|destroy\w*|publish\w*|dark web)\b",
        r"\b(we will|will be)\b[^.\n]{0,50}\b(release|share|publish|destroy|delete|report|prosecute)\b",
    ],
    "impersonated_greeting": [
        r"\bdear\s+(valued\s+)?(customer|client|user|member|account holder|paypal (member|user)|amazon (customer|user)|microsoft (customer|user)|apple (customer|user))\b",
        r"\b(this is|i am|i'm)\b[^.\n]{0,50}\b(support|security|service desk|it department|bank|ceo|cfo|hr|administrator)\b",
        r"\b(official (notice|notification)|security (alert|team|department)|account (team|department)|fraud (team|department))\b",
    ],
    "attachment_lure": [
        r"\b(attach(ed|ment)|enclosed)\b",
        r"\b(open|view|download|enable (macros|editing|content))\b[^.\n]{0,60}\b(document|invoice|file|pdf|attachment|form|statement|details)\b",
        r"\b(invoice|statement|payslip|receipt|purchase order)\b[^.\n]{0,60}\b(attached|enclosed)\b",
    ],
}

# Topical terms used to find HARD negatives in ham: benign mail that talks about the same subject as
# an element without asking for anything. This is what makes the negative set hard, and it is also
# how the count grows without inventing rows: SpamAssassin ham contains real password-reset
# confirmations, invoices, security notices and urgent-but-legitimate business mail.
TOPICS = {
    "credential_request": r"\b(password|credentials|login|log in|sign in|account|verify|verification|reset|security|two factor|2fa|pin)\b",
    "urgency_pressure": r"\b(urgent|immediately|today|deadline|expires?|expiring|final|reminder|asap|time sensitive|last day)\b",
    "threat": r"\b(legal|lawyer|attorney|court|police|claim|fine|penalty|compliance|action|dispute|debt)\b",
    "impersonated_greeting": r"\b(dear customer|dear client|dear member|support team|customer service|security team|account team|bank|official|notification)\b",
    "attachment_lure": r"\b(attached|attachment|invoice|statement|receipt|document|pdf|file|enclosed|purchase order)\b",
}

NAZARIO = [
    "https://monkey.org/~jose/phishing/phishing0.mbox",
    "https://monkey.org/~jose/phishing/phishing1.mbox",
    "https://monkey.org/~jose/phishing/phishing2.mbox",
    "https://monkey.org/~jose/phishing/phishing3.mbox",
]

SPAMASSASSIN = [
    "https://spamassassin.apache.org/old/publiccorpus/20030228_easy_ham.tar.bz2",
    "https://spamassassin.apache.org/old/publiccorpus/20030228_easy_ham_2.tar.bz2",
    "https://spamassassin.apache.org/old/publiccorpus/20030228_hard_ham.tar.bz2",
    "https://spamassassin.apache.org/old/publiccorpus/20021010_easy_ham.tar.bz2",
    "https://spamassassin.apache.org/old/publiccorpus/20021010_hard_ham.tar.bz2",
]

TARGET_PER_ELEMENT = 60
NEGATIVE_TARGET_PER_ELEMENT = 200
HARD_NEGATIVE_TARGET = 180
MAX_SEGMENTS = 32
MAX_SEGMENT_CHARS = 6000


def fetch(url: str, cache: pathlib.Path) -> bytes:
    cache.mkdir(parents=True, exist_ok=True)
    path = cache / re.sub(r"[^A-Za-z0-9._-]", "_", url.split("/")[-1])
    if path.exists() and path.stat().st_size > 0:
        return path.read_bytes()
    print(f"fetching {url}")
    with urllib.request.urlopen(url, timeout=120) as response:
        data = response.read(64 * 1024 * 1024)
    path.write_bytes(data)
    return data


def strip_html(text: str) -> str:
    text = re.sub(r"(?is)<(script|style).*?</\1>", " ", text)
    text = re.sub(r"(?s)<[^>]+>", " ", text)
    text = html.unescape(text)
    return re.sub(r"[ \t\r\f\v]+", " ", text)


def body_of(message: email.message.Message) -> str:
    parts = []
    for part in message.walk():
        content_type = part.get_content_type()
        if content_type not in ("text/plain", "text/html"):
            continue
        try:
            payload = part.get_payload(decode=True)
        except Exception:
            continue
        if payload is None:
            continue
        charset = part.get_content_charset() or "utf-8"
        try:
            text = payload.decode(charset, errors="replace")
        except LookupError:
            text = payload.decode("utf-8", errors="replace")
        if content_type == "text/html":
            text = strip_html(text)
        parts.append(text)
    return "\n\n".join(parts)


def segments(text: str) -> list[str]:
    chunks = []
    for raw in re.split(r"\n\s*\n+", text):
        chunk = "\n".join(line for line in raw.splitlines() if not line.lstrip().startswith(">"))
        chunk = re.sub(r"\s+", " ", chunk).strip()
        if len(chunk) < 40:
            continue
        chunks.append(chunk[:MAX_SEGMENT_CHARS])
        if len(chunks) >= MAX_SEGMENTS:
            break
    return chunks


def cue_counts(text: str) -> dict[str, int]:
    lowered = text.lower()
    return {
        element: sum(1 for pattern in CUES[element] if re.search(pattern, lowered))
        for element in ELEMENTS
    }


def redact(text: str) -> str:
    text = re.sub(r"\b[\w.+-]+@[\w-]+\.[\w.]+\b", "[email]", text)
    text = re.sub(r"https?://\S+", "[url]", text)
    text = re.sub(r"\b(?:\+?\d[\d\s().-]{7,}\d)\b", "[number]", text)
    return text


def normalise(text: str) -> str:
    return re.sub(r"[^a-z0-9 ]", "", text.lower()).strip()


def digest(text: str) -> str:
    return hashlib.sha256(normalise(text).encode("utf-8")).hexdigest()


def choose(segments_with_hits, used: set[str], target: int) -> list[tuple[str, dict[str, int]]]:
    chosen = []
    for text, hits in segments_with_hits:
        key = digest(text)
        if key in used:
            continue
        used.add(key)
        chosen.append((text, hits))
        if len(chosen) >= target:
            break
    return chosen


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=pathlib.Path, required=True)
    parser.add_argument("--manifest", type=pathlib.Path, required=True)
    parser.add_argument("--cache", type=pathlib.Path, default=pathlib.Path(__file__).parent / "_corpora")
    parser.add_argument("--seed-labels", type=pathlib.Path,
                        default=pathlib.Path(__file__).parent / "seed-labels.jsonl")
    args = parser.parse_args()

    seed_hashes = set()
    if args.seed_labels.exists():
        for line in args.seed_labels.read_text(encoding="utf-8").splitlines():
            if line.strip():
                seed_hashes.add(digest(json.loads(line)["text"]))

    rows = []
    used = set(seed_hashes)
    provenance = {"elements": list(ELEMENTS), "sources": [], "counts": {}}

    # Positives: Nazario, element chosen by the strongest cue in a segment.
    phishing_segments = []
    for url in NAZARIO:
        data = fetch(url, args.cache)
        # mailbox.mbox wants a path, not a file object. The downloaded archive is written to a
        # temporary file and removed immediately after parsing so no corpus text lingers here.
        with tempfile.NamedTemporaryFile(delete=False, suffix=".mbox") as temporary:
            temporary.write(data)
            temporary_path = temporary.name
        try:
            box = mailbox.mbox(temporary_path, create=False)
            for message in box:
                try:
                    text = body_of(message)
                except Exception:
                    continue
                for segment in segments(text):
                    hits = cue_counts(segment)
                    total = sum(hits.values())
                    if total == 0:
                        continue
                    element = max(ELEMENTS, key=lambda name: (hits[name], -ELEMENTS.index(name)))
                    phishing_segments.append((element, segment, hits))
        finally:
            os.unlink(temporary_path)
        provenance["sources"].append({"url": url, "label": "phishing", "licence": "Nazario public corpus, see the LICENSE.txt at the index"})

    for element in ELEMENTS:
        candidates = [(segment, hits) for name, segment, hits in phishing_segments if name == element]
        chosen = choose(candidates, used, TARGET_PER_ELEMENT)
        for segment, hits in chosen:
            rows.append({
                "id": f"ex:{element}:{digest(segment)[:12]}",
                "element": element,
                "label": 1,
                "split": "heldout" if int(digest(segment)[:2], 16) % 10 < 3 else "train",
                "source": "nazario-public-corpus",
                "licence": "Nazario public corpus (research corpus, see its LICENSE.txt)",
                "hard_negative": False,
                "cue_hits": hits[element],
                "text": redact(segment),
            })
        provenance["counts"][f"{element}:positive"] = len(chosen)

    # Negatives: SpamAssassin ham, cue bearing segments first (hard), other ham as easy fill.
    ham_segments = []
    for url in SPAMASSASSIN:
        data = fetch(url, args.cache)
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:bz2") as archive:
            for member in archive.getmembers():
                if not member.isfile():
                    continue
                handle = archive.extractfile(member)
                if handle is None:
                    continue
                try:
                    message = email.message_from_bytes(handle.read())
                    text = body_of(message)
                except Exception:
                    continue
                for segment in segments(text):
                    ham_segments.append((segment, cue_counts(segment)))
        provenance["sources"].append({"url": url, "label": "ham", "licence": "Apache SpamAssassin public corpus"})

    for element in ELEMENTS:
        topic = re.compile(TOPICS[element], re.IGNORECASE)
        hard = [(segment, hits) for segment, hits in ham_segments
                if hits[element] > 0 or topic.search(segment)]
        # Deterministic order: cue hits first, then the digest, so a rerun picks the same rows.
        hard.sort(key=lambda item: (-item[1][element], digest(item[0])))
        easy = [(segment, hits) for segment, hits in ham_segments
                if hits[element] == 0 and not topic.search(segment)]
        chosen_hard = choose(hard, used, HARD_NEGATIVE_TARGET)
        chosen_easy = choose(easy, used, NEGATIVE_TARGET_PER_ELEMENT - len(chosen_hard))
        for segment, hits in chosen_hard + chosen_easy:
            is_hard = any(segment is chosen[0] for chosen in chosen_hard)
            rows.append({
                "id": f"ex:{element}:{digest(segment)[:12]}",
                "element": element,
                "label": 0,
                "split": "heldout" if int(digest(segment)[:2], 16) % 10 < 3 else "train",
                "source": "spamassassin-ham",
                "licence": "Apache SpamAssassin public corpus",
                "hard_negative": is_hard,
                "cue_hits": hits[element],
                "text": redact(segment),
            })
        provenance["counts"][f"{element}:negative"] = len(chosen_hard) + len(chosen_easy)
        provenance["counts"][f"{element}:negative_hard"] = len(chosen_hard)

    args.out.write_text("\n".join(json.dumps(row) for row in rows) + "\n", encoding="utf-8")
    provenance["rows"] = len(rows)
    provenance["note"] = (
        "Weak supervision. The corpus supplies the malicious or benign label; the cue rule only "
        "selects the element a segment is evidence about. Text is redacted for structured identifiers "
        "only and is not committed."
    )
    args.manifest.write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {args.out} with {len(rows)} rows")
    for key, value in sorted(provenance["counts"].items()):
        print(f"  {key}: {value}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
