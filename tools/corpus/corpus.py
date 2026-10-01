#!/usr/bin/env python3
"""Generate, seed and check a corpus of mail with a manifest of the facts planted in it.

This is the traffic instrument for the console's harness and for the fleet's measurement. It emits
two kinds of statement and never one field (architecture decision 27):

  * **planted facts** - deterministic properties really put in the message, each with the site it
    was put at. These are checkable by the pipeline, which makes the corpus an oracle for the
    deterministic layer: if a fact is in the message and the findings do not report it, that is a
    defect and this tool names it and points at where it is.
  * **the intent label** - what the message was meant to be. A judgement. It is a denominator and
    a distribution: never evidence in the ledger, never a policy input, never authority to act, and
    a rate computed from it is quoted with the corpus named rather than as accuracy.

Run it as a file, from the repository root:

    python3 tools/corpus/corpus.py generate --seed 1234 --count 20 --out .styloagent/scratch/batch --profile mixed
    python3 tools/corpus/corpus.py seed  --base-url http://127.0.0.1:5271 --key-file .styloagent/scratch/batch/principal.key --batch .styloagent/scratch/batch
    python3 tools/corpus/corpus.py check --base-url http://127.0.0.1:5271 --key-file .styloagent/scratch/batch/principal.key --manifest .styloagent/scratch/batch/manifest.json

Three properties are required rather than nice, and each is enforced here rather than promised:

  * **determinism**: seed + index gives byte-identical bytes. Every choice is drawn from a hash of
    (seed, index, purpose), never from a clock, a counter or the process's hash seed.
  * **no transport**: every request goes to the loopback Host's authenticated routes. There is no
    SMTP code path, no mailbox and no address this tool did not create, and every address it writes
    is under a reserved test domain.
  * **no self-authorship**: this tool never asks a model to write the corpus it is then measured on,
    because that would measure self-consistency rather than detection. A batch authored by a model
    would have to be labelled `authoredByModel: true` and reported apart; nothing here produces one.

NO SECRET VALUES. `--key-file` is a **path** to a file read in-process. A key is never an argument,
never logged, and never written into a manifest. The only thing this tool says about a key is
whether the file it was given could be read.

WHERE THE FACTS COME FROM. Every plantable fact below is one this lane **measured** on a real Host,
not one inferred from a signal name. The three that live in the submission rather than in the MIME
are marked `where: "submission"`, because that is where the pipeline reads them from: two messages
whose bytes are identical can differ in whether the fact is planted, and the manifest says which.
"""

from __future__ import annotations

import argparse
import base64
import csv
import hashlib
import io
import json
import os
import re
import sys
import urllib.error
import urllib.request
import zipfile
from dataclasses import dataclass, field
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

# 2 (1 Oct 2026): `planted[].expected` is a predicate object rather than the constant 1, per
# `overview-`'s ruling. A boolean cannot say "present and not firing" (`{"available": true, "value":
# 0}`), and cannot say "the claim is provenance, not a value" (`{"origin": "Deterministic"}`). Both
# are real: `deterministic.authentication_provenance` is Available with value 0 when provenance is
# complete, and `deterministic.template_fingerprint` is Available with a NULL value, which the integer
# could not express at all. This is a breaking change to a versioned schema, hence the bump.
#
# Version 3 changed what a conversation window CONTAINS and added the counts that make an over-long
# fixture visible. Version 2 put the window on the wire as the prior turn's body text alone; version
# 3 sends the prior turn's raw message as the Host received it, because `conversation-` measured that
# the continuity answer turns on the rendered turn shape, so a bodies-only window asks a different
# question from the one the Host asks. A version 2 consumer that compared window contents, or that
# measured a window's size to attribute a provider refusal, would silently read the wrong thing under
# the same number, which is why the number moved. It also added `turnCharacters` and
# `windowCharacters` to every message.
CORPUS_VERSION = 3

# Every address this tool writes is under a reserved test domain (RFC 2606 / RFC 5737), so a batch
# cannot accidentally name a real mailbox and nothing can be delivered anywhere even by mistake.
SENDER_DOMAIN = "example.test"
RECIPIENT = "operator@example.test"
CONNECTING_IP = "198.51.100.9"

# Measured on a throwaway Host, 30 Sep 2026. These are the facts this lane can plant and check; a
# profile may only declare one of these, so a manifest cannot claim a fact nothing here can verify.
FACTS: dict[str, dict] = {
    "deterministic.link_display_mismatch": {
        "where": "mime",
        "site": "html body: the <a> href host disagrees with the link text host",
        "constraint": (
            "NEEDS A HOST IN THE LINK TEXT. The comparison is href host against link text host, so "
            "an anchor whose text is prose ('click here to pay') has nothing to disagree with and "
            "the row comes back Available with a NULL value. Measured: a first quarantine fixture "
            "used prose anchor text and the manifest claimed a fact the message could not carry."
        ),
    },
    "deterministic.link_idn": {
        "where": "mime",
        "site": "html body: an <a> href whose host uses a non-ASCII (punycode) label",
    },
    "deterministic.link_idn_homograph": {
        "where": "mime",
        "site": "html body: an <a> href host that mixes scripts to imitate a Latin domain",
    },
    "deterministic.display_name_address_mismatch": {
        "where": "mime",
        "site": "From header: a display name that is itself an address, disagreeing with the mailbox",
    },
    "deterministic.attachment_type_mismatch": {
        "where": "mime",
        "site": "attachment part: declared Content-Type disagrees with the file extension",
    },
    "deterministic.html_text_disagreement": {
        "where": "mime",
        "site": "multipart/alternative: the text and html parts share too few tokens",
        "constraint": (
            "needs at least MinTokensForComparison (20) tokens in EACH representation, or the row "
            "comes back NotApplicable with a null value"
        ),
    },
    "deterministic.trusted_authentication_failure": {
        "where": "submission",
        "site": "authenticationResults[]: a failing result with fromTrustedVerifier true",
        "requiresFullCoverage": True,
    },
    "deterministic.authentication_provenance": {
        "where": "submission",
        "site": "authenticationResults[]: provenance complete when a trusted verifier reported",
        "requiresFullCoverage": True,
    },
    "semantic.conversational_continuity": {
        "where": "submission",
        "site": "conversationContext: a window supplied, so the dimension is answerable at all",
        "constraint": (
            "NEEDS A WINDOW IN THE REQUEST, not bytes in the message. Without a conversation "
            "context the dimension is NotApplicable and no wording in the MIME changes that. This is "
            "the only semantic dimension this corpus declares, and it declares AVAILABILITY, which "
            "is a consequence of the request this tool writes, never a value: the value is the "
            "model's judgement and quoting it would be a detection claim the corpus cannot make."
        ),
    },
    "envelope.no_recipients": {
        "where": "submission",
        "site": "rcptTo: an empty recipient list",
    },
    "envelope.null_sender_not_permitted": {
        "where": "submission",
        "site": "direction Outbound with no mailFrom",
    },
    "envelope.refusal_attributable_to_the_envelope": {
        "where": "reasons",
        "site": "the decision's reason codes: no policy.risk_above_* code is present",
        "constraint": (
            "a NEGATIVE claim, and it is a control rather than a fact about the bytes. It says the "
            "intake refusal came from the envelope tier and not from the risk score, which is the "
            "only way to check that a fixture reaches tier 2 for the reason it claims to. Planted "
            "facts whose `where` is `mime` or `submission` are NOT checkable in a refused message: "
            "intake is decided before the deterministic layer runs, so a message refused there has "
            "no evidence rows at all. That is measured, not assumed. This control is the one that "
            "IS checkable, because reason codes survive the refusal."
        ),
    },
}

# Facts a benign message plants **as an expected zero**. A benign message is not a message with
# nothing to say: it is one whose checkable properties must come back absent, and a pipeline that
# reports them present on ordinary mail is a false positive. That is the half of the acceptance a
# spam-only corpus cannot reach, and it uses the same mechanism rather than a second one.
BENIGN_FACTS = [
    "deterministic.link_display_mismatch",
    "deterministic.display_name_address_mismatch",
    "deterministic.trusted_authentication_failure",
]

INTENTS = {
    "ordinary": "ordinary correspondence",
    "transactional": "transactional",
    "credential-phishing": "credential phishing",
    "payment-redirection": "payment redirection fraud",
    "bulk-unsolicited": "bulk unsolicited",
}

# Coverage intent, from `overview-`'s measured note: supplying a conversation window removes exactly
# one coverage reduction reason and does not by itself clear reduced coverage when the mail carries
# no authentication provenance. So a batch that needs full coverage must carry the provenance a
# boundary would supply, and a batch that measures the low-coverage path must say it left it out
# deliberately. "unspecified" is refused at generation time rather than defaulted, because a batch
# whose coverage was an accident cannot be told from one whose coverage was planted.
COVERAGE = ("full", "reduced")


# ---------------------------------------------------------------------------------------------
# Deterministic primitives
# ---------------------------------------------------------------------------------------------

def draw(seed: int, index: int, purpose: str) -> int:
    """A deterministic non-negative integer from (seed, index, purpose).

    Not `random`: the point is that the same seed and index give the same bytes in any process, on
    any machine, in any Python. A process-local `hash()` or a counter would break that, and a
    failure that cannot be re-run is not evidence.
    """
    material = f"{seed}:{index}:{purpose}".encode("utf-8")
    return int.from_bytes(hashlib.sha256(material).digest()[:8], "big")


def pick(seed: int, index: int, purpose: str, options: list[str]) -> str:
    return options[draw(seed, index, purpose) % len(options)]


def message_id(seed: int, index: int) -> str:
    return hashlib.sha256(f"{seed}:{index}:message-id".encode("utf-8")).hexdigest()[:16]


# ---------------------------------------------------------------------------------------------
# MIME construction
# ---------------------------------------------------------------------------------------------

def build_mime(
    *,
    sender_name: str,
    sender_address: str,
    subject: str,
    text: str,
    html: str | None,
    facts: list[str],
    seed: int,
    index: int,
) -> bytes:
    """One raw MIME message. The shape follows the facts, never the other way round."""
    headers = [
        f"From: \"{sender_name}\" <{sender_address}>",
        f"To: {RECIPIENT}",
        f"Subject: {subject}",
        "Date: Tue, 30 Sep 2026 09:00:00 +0000",
        f"Message-ID: <{message_id(seed, index)}@{SENDER_DOMAIN}>",
        "MIME-Version: 1.0",
    ]

    want_attachment = "deterministic.attachment_type_mismatch" in facts
    want_html = html is not None

    if not want_attachment and not want_html:
        headers.append('Content-Type: text/plain; charset="utf-8"')
        return ("\r\n".join(headers) + "\r\n\r\n").encode("utf-8") + text.encode("utf-8")

    # The body parts first, then the container, so the boundary is only chosen once the number of
    # parts is known. Declaring multipart/mixed with no parts was a mistake made in measurement and
    # it produced a message that declared link and attachment facts and carried neither.
    if want_html and want_attachment:
        inner = _alternative("alt", text, html)
        parts = [
            ('Content-Type: multipart/alternative; boundary="alt"', inner),
            (_attachment_headers(), _attachment_body()),
        ]
        headers.append('Content-Type: multipart/mixed; boundary="mix"')
        body = _mixed("mix", parts)
    elif want_attachment:
        headers.append('Content-Type: multipart/mixed; boundary="mix"')
        body = _mixed("mix", [(_attachment_headers(), _attachment_body())])
    else:
        headers.append('Content-Type: multipart/alternative; boundary="alt"')
        body = _alternative("alt", text, html)

    return ("\r\n".join(headers) + "\r\n\r\n").encode("utf-8") + body


def _mixed(boundary: str, parts: list[tuple[str, bytes]]) -> bytes:
    out = b""
    for headers, body in parts:
        out += f"--{boundary}\r\n{headers}\r\n\r\n".encode("utf-8") + body + b"\r\n"
    return out + f"--{boundary}--\r\n".encode("utf-8")


def _alternative(boundary: str, text: str, html: str) -> bytes:
    return (
        f"--{boundary}\r\nContent-Type: text/plain; charset=\"utf-8\"\r\n\r\n{text}\r\n"
        f"--{boundary}\r\nContent-Type: text/html; charset=\"utf-8\"\r\n\r\n{html}\r\n"
        f"--{boundary}--\r\n"
    ).encode("utf-8")


def _attachment_headers() -> str:
    # Declared `application/pdf` while the file name says `.exe`. The signal is "declared attachment
    # type versus the type implied by its file name", so the two must actually disagree: a first
    # version of this declared `application/pdf` and named it `statement.pdf`, which agree, and the
    # pipeline correctly reported 0 mismatches against a manifest that claimed one. The fixture was
    # wrong, not the detector: a program named as a document is the evasion the fact exists to catch.
    return (
        'Content-Type: application/pdf; name="statement.exe"\r\n'
        "Content-Transfer-Encoding: base64\r\n"
        'Content-Disposition: attachment; filename="statement.exe"'
    )


def _attachment_body() -> bytes:
    return base64.encodebytes(b"This attachment is not a PDF, whatever the filename says.\r\n")


def _long_enough(text: str) -> str:
    """Pad a body past MinTokensForComparison (20 tokens) in each representation.

    A fact must not be declared that the message cannot carry: below the threshold the row comes
    back NotApplicable with a null value, which would make the manifest claim an unfalsifiable
    thing. Measured, not assumed: that is exactly what happened to this fact the first time.
    """
    filler = (
        "Please review the details in this message at your convenience and contact the sender if "
        "anything appears to be incorrect or requires clarification before you proceed further."
    )
    while len(text.split()) < 24:
        text = text + " " + filler
    return text


# ---------------------------------------------------------------------------------------------
# Profiles: which facts a message carries, and what it then says
# ---------------------------------------------------------------------------------------------

@dataclass
class MessagePlan:
    index: int
    intent: str
    facts: list[str]
    subjects: list[str]
    text: str
    html: str | None
    sender_name: str
    sender_address: str
    auth: list[dict]
    recipients: list[str]
    direction: str
    mail_from: str | None
    note: str
    threshold_targeted: bool = False
    # The prior turns supplied as `conversationContext`. Empty for a single message, and the presence
    # of a window is the ONE thing about continuity this corpus plants, because it is the one thing
    # the request controls. Each entry is the prior turn's RAW TEXT AS THE HOST RECEIVED IT (the
    # whole MIME message, headers included), not the body alone: `conversation-` measured that the
    # rendered turn shape is what the continuity answer turns on, so a bodies-only window would ask
    # a different question from the one the Host asks.
    window: list[str] = field(default_factory=list)
    # Set on a turn whose window is the immediately preceding message in the same batch. The window
    # cannot be known while the plan is built, because the raw bytes of turn 1 do not exist yet, so
    # `generate` fills it from the message it just wrote. A turn marked this way that has no
    # predecessor is REFUSED rather than emitted windowless: a windowless turn 2 would silently
    # measure a different question, which is the failure this field exists to prevent.
    window_from_previous_turn: bool = False
    # Facts that must NOT fire, each mapped to the positive fact it is the control for. Written as a
    # map rather than a list because the relation is directional: the control is meaningless without
    # the claim it protects, and a list would leave a reader to guess which of two fact ids is which.
    controls: dict[str, str] = field(default_factory=dict)
    # Which turn of a pair this message is. Null for an unpaired message.
    turn: int | None = None
    # Described but not declared: a change this corpus plants for a lane whose signal id does not
    # exist yet. Recorded honestly as unverifiable rather than given an invented id.
    undescribed_change: dict | None = None

    @property
    def has_window(self) -> bool:
        return len(self.window) > 0


def _auth_pass() -> list[dict]:
    """Provenance that a boundary would supply, which is what full coverage needs."""
    return [
        {"mechanism": "spf", "result": "pass", "fromTrustedVerifier": True},
        {"mechanism": "dkim", "result": "pass", "fromTrustedVerifier": True},
        {"mechanism": "dmarc", "result": "pass", "fromTrustedVerifier": True},
    ]


def _auth_fail() -> list[dict]:
    """Failing results, BOTH halves: three from a trusted verifier and three from an untrusted one.

    The untrusted half is the control. The evidence value is the count of *trusted* failures, so a
    detector that counted every failure would report 6 where the manifest declares 3. That is a
    same-message control rather than a claim in prose, and it re-measures the property that a message
    cannot assert its own authentication.
    """
    return [
        {"mechanism": "spf", "result": "fail", "fromTrustedVerifier": True},
        {"mechanism": "dkim", "result": "fail", "fromTrustedVerifier": True},
        {"mechanism": "dmarc", "result": "fail", "fromTrustedVerifier": True},
        {"mechanism": "spf", "result": "fail", "fromTrustedVerifier": False},
        {"mechanism": "dkim", "result": "fail", "fromTrustedVerifier": False},
        {"mechanism": "dmarc", "result": "fail", "fromTrustedVerifier": False},
    ]


def plan_benign(seed: int, index: int) -> MessagePlan:
    """Ordinary mail: no risk fact, and the checkable properties must come back absent."""
    subjects = [
        "Notes from the Tuesday review",
        "Re: your order has been dispatched",
        "Coffee machine is fixed",
        "Agenda for Thursday",
    ]
    bodies = [
        "Thanks for the summary. I have read it and I have no changes to suggest. The figures for "
        "the second quarter look consistent with what we discussed, and I am happy for it to go "
        "out as it stands. Let me know if you would rather I circulated it myself.",
        "Your order left our warehouse this morning and should arrive within two working days. No "
        "action is needed from you. If it has not arrived by Friday, reply to this message and we "
        "will look into it for you straight away.",
        "The engineer came this morning and the machine is working again. There is no charge. "
        "Thanks for reporting it so quickly, it would have been much worse by Monday.",
        "Here is the agenda for Thursday. I have kept it short because we have a lot to get "
        "through. Please add anything you want to raise before Wednesday evening so it can be "
        "included in the printed copies.",
    ]
    subject = pick(seed, index, "benign-subject", subjects)
    text = pick(seed, index, "benign-body", bodies)
    # An agreeing link: text and href name the same host, so link_display_mismatch is planted as an
    # expected zero rather than left out. A benign message with no link would not test that at all.
    html = (
        f"<html><body><p>{text}</p>"
        f'<p><a href="https://intranet.{SENDER_DOMAIN}/notes">https://intranet.{SENDER_DOMAIN}/notes</a></p>'
        "</body></html>"
    )
    return MessagePlan(
        index=index,
        intent=INTENTS["ordinary"],
        facts=list(BENIGN_FACTS),
        subjects=[subject],
        text=text,
        html=html,
        sender_name="Colleague",
        sender_address=f"colleague@{SENDER_DOMAIN}",
        auth=_auth_pass(),
        recipients=[RECIPIENT],
        direction="Inbound",
        mail_from=f"colleague@{SENDER_DOMAIN}",
        note="benign control: agreeing anchor, passing authentication, no request for anything",
    )


def plan_phishing(seed: int, index: int) -> MessagePlan:
    """Risk mail carrying the plantable MIME facts, with provenance so coverage is full."""
    facts = [
        "deterministic.link_display_mismatch",
        "deterministic.trusted_authentication_failure",
        "deterministic.attachment_type_mismatch",
        # The control for the failure count above: provenance is complete, so the three trusted
        # failures are attributable. If the trusted filter were broken this row would read 1 and the
        # claim would fail, in the same message as the positive.
        "deterministic.authentication_provenance",
    ]
    want_idn = draw(seed, index, "idn") % 3 == 0
    if want_idn:
        facts += ["deterministic.link_idn", "deterministic.link_idn_homograph"]

    text = _long_enough(
        "Our records show an outstanding balance on your account. To avoid interruption you must "
        "confirm your payment details within 24 hours. Open the secure statement attached to this "
        "message, or follow the link to review the amount payable and authorise the transfer. If "
        "no payment is received the account will be suspended and any pending items returned."
    )
    if want_idn:
        # A homograph host: the first label uses a Cyrillic 'a', which reads as Latin to a person
        # and is a different domain to a resolver. The fact is in the bytes.
        href = "https://аccounts.example-bank.test/login"
    else:
        href = f"http://{CONNECTING_IP}/session"
    html = (
        f"<html><body><p>{text}</p>"
        f'<p><a href="{href}">https://secure.example-bank.test/login</a></p>'
        "</body></html>"
    )
    return MessagePlan(
        index=index,
        intent=INTENTS["credential-phishing"],
        facts=facts,
        subjects=["Outstanding balance: action required within 24 hours"],
        text=text,
        html=html,
        sender_name="Accounts",
        sender_address=f"accounts@{SENDER_DOMAIN}",
        auth=_auth_fail(),
        recipients=[RECIPIENT],
        direction="Inbound",
        mail_from=f"accounts@{SENDER_DOMAIN}",
        note="credential request, failing authentication from a trusted verifier, mismatched anchor",
        # Provenance present and not firing (value 0, origin Deterministic) is the control for the
        # authentication count: a trusted failure is only attributable as *trusted* because the
        # provenance row is complete and says so. Without it, "3 trusted failures" would be a number
        # with nothing standing behind it.
        controls={"deterministic.authentication_provenance":
                  "deterministic.trusted_authentication_failure"},
    )


def plan_quarantine(seed: int, index: int) -> MessagePlan:
    """A threshold-targeted fixture, which is labelled as such and must never be an accuracy claim.

    Measured: nine weighted semantic dimensions answered positively reaches index 0.9315 and the
    quarantine state. The wording here was arrived at by looking at the local model's answers, so it
    reaches the threshold partly by fitting that model. That makes it a valid way to populate the
    state for a harness, and it measures the model's agreement with this wording rather than
    detection. `thresholdTargeted: true` is what carries that into the manifest.
    """
    text = _long_enough(
        "Your tax account shows an outstanding balance that must be settled today. Our bank details "
        "have changed, so payments sent to the old account will be returned: send the payment to "
        "the new account, sort code 04-00-04, account number 12345678, held at Example Bank. You "
        "may also confirm the card details we hold on file so the payment can be taken "
        "automatically, and your account password is required to authorise the transfer. We also "
        "require a copy of your passport and your national insurance number to verify your identity "
        "before the account can be cleared. Keep this notice confidential and do not discuss it "
        "with anyone else, including your accountant. This must be completed within 24 hours or "
        "enforcement action will begin. Pay securely through the link below if you prefer not to "
        "use the bank transfer."
    )
    # The anchor text names a host and the href names a different one. Prose anchor text here would
    # leave `link_display_mismatch` with nothing to compare, and the manifest declares it.
    html = (
        f"<html><body><p>{text}</p>"
        f'<p><a href="http://{CONNECTING_IP}/pay">https://secure.example-bank.test/pay</a></p>'
        "</body></html>"
    )
    return MessagePlan(
        index=index,
        intent=INTENTS["payment-redirection"],
        facts=[
            "deterministic.link_display_mismatch",
            "deterministic.attachment_type_mismatch",
            "deterministic.trusted_authentication_failure",
        ],
        subjects=["Outstanding balance: action required today"],
        text=text,
        html=html,
        sender_name="Revenue Service",
        sender_address=f"refunds@{SENDER_DOMAIN}",
        auth=_auth_fail(),
        recipients=[RECIPIENT],
        direction="Inbound",
        mail_from=f"refunds@{SENDER_DOMAIN}",
        note="threshold-targeted: authored against the measured weighted-dimension ladder",
        threshold_targeted=True,
    )


def plan_envelope_violation(seed: int, index: int) -> MessagePlan:
    """A tier 2 violation, which is refused before the semantic provider is ever called.

    The MIME here is deliberately plain rather than the phishing body with the declared facts
    swapped out. The envelope tier is decided before the risk score is consulted, but the
    deterministic layer still runs and still reports what it finds, so reusing a body that carries a
    mismatched link and an attachment would put facts in the message that the manifest never
    declared. A manifest is a description of the message: what it does not list, the message must
    not contain.
    """
    subject = pick(seed, index, "envelope-subject", ["Weekly bulletin", "Service notice"])
    text = _long_enough(
        "This is the weekly bulletin. Nothing in it requires a reply, a payment or a link, and no "
        "details are requested from you. It is included here only so that the message has a body "
        "long enough to be analysed like any other."
    )
    return MessagePlan(
        index=index,
        intent=INTENTS["bulk-unsolicited"],
        # The refusal must be attributable to the envelope and to nothing else. Provenance is
        # complete and no risk fact is planted, so "verified violation" is the only remaining
        # explanation for the 422 rather than an asserted one.
        facts=["envelope.no_recipients", "envelope.refusal_attributable_to_the_envelope"],
        subjects=[subject],
        text=text,
        html=None,
        sender_name="Bulletin",
        sender_address=f"bulletin@{SENDER_DOMAIN}",
        auth=_auth_pass(),
        # The fact itself: a recipient list with nothing in it.
        recipients=[],
        direction="Inbound",
        mail_from=f"bulletin@{SENDER_DOMAIN}",
        note="envelope violation: no recipients, which is verified rather than judged",
        # The control: no risk-threshold reason fires, so the refusal is attributable to the
        # envelope and to nothing else. An authentication control was tried here first and had to be
        # removed: a message refused at intake never reaches the deterministic layer, so a fact
        # planted in its authentication results has no evidence row to be checked against and `check`
        # correctly reported it as uncheckable. The control has to be something the refusal leaves
        # behind, and reason codes are that.
        controls={"envelope.refusal_attributable_to_the_envelope": "envelope.no_recipients"},
    )


# The paired temporal fixture. Constants rather than seeded draws, because the second turn's window
# is the first turn's own bytes and a seeded draw would make the pair depend on the order the two
# indices are planned in.
#
# BOTH second turns are ordinary FORWARD-MOVING messages, and that is a measured requirement rather
# than a style choice. `conversation-` measured the continuity axis as answering A when the body's
# content is CONTAINED in the supplied window and B when it ADVANCES past it, regardless of length.
# So a second turn that restates the first would answer A for a structural reason that has nothing to
# do with the change under test, and the pair would measure the axis instead of the change. The
# control here previously restated the window's own bank details and was rewritten for exactly that
# reason; it now advances (approval, payment run, remittance advice) while touching the payment
# destination not at all.
PAIR_TURN1 = (
    "Please arrange payment of invoice 4471, which falls due on the fourteenth. Our bank details are "
    "unchanged: sort code 20-00-00, account number 11112222, held at Example Bank. Let me know once "
    "it has gone out and I will mark the invoice as settled on our side and file the paperwork."
)
PAIR_TURN2_CHANGED = (
    "Following my earlier message, please disregard the account details in it. Our bank details have "
    "changed and payments sent to the old account will be returned to you by the receiving bank. "
    "Use sort code 04-00-04, account number 12345678 from now on for invoice 4471 and for anything "
    "else outstanding. I appreciate the details in my first message are now wrong and that this is "
    "inconvenient, so please confirm when you have made the change at your end."
)
PAIR_TURN2_CONTROL = (
    "Following my earlier message about invoice 4471, a short update from our side: it has now "
    "cleared our internal approval and is queued for the next payment run. Our finance team has "
    "logged the reference for their own records, and I will send the remittance advice as soon as "
    "it clears. Nothing is needed from you before then."
)


def plan_pair_control(seed: int, index: int) -> MessagePlan:
    """The same pair with the change removed. A detector that fires on both is firing on the window.

    This is the negative half of the pair and it is a separate profile rather than a coin flip,
    because a consumer asking for a control should get one every time rather than half the time.
    """
    return _plan_pair(seed, index, control=True)


def plan_pair(seed: int, index: int) -> MessagePlan:
    """One turn of a paired fixture: a baseline, then a second turn carrying it as a window.

    Even indices are turn 1 and odd indices are turn 2, so a pair is two consecutive messages from
    one batch. Turn 2's window is turn 1's own raw bytes, which is what makes the pair a temporal
    fixture rather than two unrelated messages, and it is resolved by `generate` from the message it
    just wrote (`window_from_previous_turn`) rather than being carried in the plan, because turn 1's
    raw text does not exist while the plan is being built.

    **What is declared and what is not.** `semantic.conversational_continuity` is declared as
    AVAILABILITY, because supplying the window is something this tool does and the availability is
    its direct consequence. The *change* itself is recorded in `undescribedChange` and NOT declared
    as a planted fact: the signal id a change surfaces as belongs to the conversation lane and does
    not exist in the tree yet, and inventing one would put a claim in the manifest that no pipeline
    could report and that `check` would fail a correct pipeline for. The change is real in the bytes
    (different account number); it is simply not yet assertable, and the manifest says which.
    """
    return _plan_pair(seed, index, control=False)


def _plan_pair(seed: int, index: int, *, control: bool) -> MessagePlan:
    if index % 2 == 0:
        return MessagePlan(
            index=index,
            intent=INTENTS["transactional"],
            # No window: the dimension is unanswerable, and saying so is the claim. It is the
            # contrast that makes turn 2's availability mean something.
            facts=["semantic.conversational_continuity"],
            subjects=["Invoice 4471 due on the fourteenth"],
            text=PAIR_TURN1,
            html=None,
            sender_name="Supplier Accounts",
            sender_address=f"ap@{SENDER_DOMAIN}",
            auth=_auth_pass(),
            recipients=[RECIPIENT],
            direction="Inbound",
            mail_from=f"ap@{SENDER_DOMAIN}",
            note="turn 1 of a pair: the baseline, submitted with no window",
            turn=1,
        )

    second = PAIR_TURN2_CONTROL if control else PAIR_TURN2_CHANGED
    change = None
    if not control:
        change = {
            "signalId": None,
            "turn": 2,
            "site": "body: the payment destination differs from the destination in turn 1",
            "from": "sort code 20-00-00, account number 11112222",
            "to": "sort code 04-00-04, account number 12345678",
            "note": (
                "recorded but NOT declared as a planted fact: no conversation signal id exists in "
                "the tree yet, and a manifest that named one would be asserting something nothing "
                "can report. The change is real in the bytes; it is not yet assertable."
            ),
        }
    return MessagePlan(
        index=index,
        intent=INTENTS["transactional"],
        facts=["semantic.conversational_continuity"],
        subjects=["Re: Invoice 4471 due on the fourteenth"],
        text=second,
        html=None,
        sender_name="Supplier Accounts",
        sender_address=f"ap@{SENDER_DOMAIN}",
        auth=_auth_pass(),
        recipients=[RECIPIENT],
        direction="Inbound",
        mail_from=f"ap@{SENDER_DOMAIN}",
        note=(
            "turn 2 control of a pair: the window is supplied and nothing about the payment changed, "
            "so a change detector firing here is a false positive"
            if control
            else "turn 2 of a pair: the window is supplied and the payment destination changed"
        ),
        window_from_previous_turn=True,
        turn=2,
        undescribed_change=change,
    )


PROFILES = {
    # name -> the plan builder for each index, cycled
    "benign": [plan_benign],
    "phishing": [plan_phishing],
    "mixed": [plan_benign, plan_benign, plan_phishing, plan_envelope_violation],
    "quarantine": [plan_quarantine],
    # Two consecutive messages per pair, so an even count is two complete pairs and an odd count ends
    # on a turn 2 whose baseline is in the same batch rather than in the window.
    "pair": [plan_pair],
    "pair-control": [plan_pair_control],
}


# ---------------------------------------------------------------------------------------------
# generate
# ---------------------------------------------------------------------------------------------

def to_request(plan: MessagePlan, raw: bytes, coverage: str) -> dict:
    """The submission body. Coverage is carried here or deliberately left out, never by accident."""
    body = {
        "direction": plan.direction,
        "mailFrom": plan.mail_from,
        "rcptTo": plan.recipients,
        "rawMime": base64.b64encode(raw).decode("ascii"),
    }
    if coverage == "full":
        body["connectingIp"] = CONNECTING_IP
        body["authenticationResults"] = plan.auth
    # coverage == "reduced": no connectingIp and no authenticationResults, on purpose. The manifest
    # records that as the planted reason rather than leaving a reader to infer it.
    return body


FAILING_AUTH = {"fail", "softfail", "permerror", "hardfail", "temperror", "policy"}


def expected_predicate(fact_id: str, plan: MessagePlan) -> dict:
    """The measured predicate the finding must satisfy, derived from the plan rather than assumed.

    Three kinds, because three kinds of claim are real and an integer can express only one:

      {"value": 1}                          a firing dimension
      {"available": true, "value": 0}       present and NOT firing
      {"available": true, "origin": "..."}  a claim about provenance rather than a value

    `deterministic.trusted_authentication_failure` counts the failing mechanisms a **trusted**
    verifier reported, so three trusted failures report 3 and not 1: a first version of this manifest
    wrote `expected: 1` for every risk fact and `check` then failed a message that had correctly
    reported all three. The expectation was wrong, not the pipeline.

    The count is also where the control lives. The phishing fixture now supplies three *untrusted*
    failing results alongside the three trusted ones, so a detector that counted every failure would
    report 6. Declaring 3 asserts the untrusted half did not count, in the same message, which is
    what `controlFor` names.
    """
    if fact_id == "deterministic.trusted_authentication_failure":
        trusted_failures = sum(
            1
            for result in plan.auth
            if result.get("fromTrustedVerifier") and result["result"].lower() in FAILING_AUTH
        )
        return {"value": trusted_failures}
    if fact_id == "deterministic.authentication_provenance":
        # Provenance is incomplete exactly when no trusted verifier reported and there is no
        # connecting IP. A full-coverage message has both, so the honest expectation is present and
        # zero: this IS the control row, and saying so is the point of the origin field.
        return {"available": True, "value": 0, "origin": "Deterministic"}
    if fact_id == "semantic.conversational_continuity":
        # Availability only. The value is the model's judgement; the corpus owns the presence of
        # the window and nothing else, so that is all it declares.
        return {"available": plan.has_window}
    if fact_id == "envelope.refusal_attributable_to_the_envelope":
        # A count, so it reads like every other predicate: zero risk-threshold reasons. Not
        # `{"absent": true}` and not a boolean, because the whole point of this schema is that one
        # shape of claim does not get a private vocabulary.
        return {"value": 0}
    if fact_id.startswith("envelope."):
        return {"value": 1}
    # A benign message plants its facts as present-and-not-firing: the assertion is that the property
    # is absent and is reported absent, which is the half of acceptance a risk-only corpus cannot
    # reach. `available: true` and not merely `value: 0`, because those are different claims: a row
    # that came back NotApplicable would otherwise satisfy an expected zero.
    if plan.intent == INTENTS["ordinary"]:
        return {"available": True, "value": 0}
    return {"value": 1}


def manifest_entry(plan: MessagePlan, raw: bytes, coverage: str) -> dict:
    # The envelope is recorded in the manifest, not re-derived by `seed`. This is load-bearing: a
    # manifest that declared `envelope.no_recipients` while `seed` rebuilt a body with one recipient
    # would be a manifest that does not describe the message it is the manifest for. That happened,
    # `check` caught it, and the fix is to make the manifest the complete description it claims to
    # be: what `seed` sends is what the manifest says and nothing else.
    envelope: dict = {
        "direction": plan.direction,
        "mailFrom": plan.mail_from,
        "rcptTo": plan.recipients,
    }
    if coverage == "full":
        envelope["connectingIp"] = CONNECTING_IP
        envelope["authenticationResults"] = plan.auth
    # The window is part of the request, not part of the bytes. Two messages whose MIME hashes
    # identically can differ in whether a window was supplied, so the manifest carries it beside the
    # envelope: it is what `seed` replays, and it is the reason a continuity row can be answerable
    # for one message and unanswerable for another. Omitted rather than null when there is no window,
    # so a consumer reading the field is reading a request that was made.
    #
    # The shape is the wire shape: `MessageSubmissionRequest.ConversationContext` is a flat list of
    # prior turns (`src/StyloMail.Host/Contracts/AssessmentRequest.cs:44`), not an object wrapping
    # one. A wrapper here would deserialise to nothing and the manifest would describe a request the
    # Host never saw.
    if plan.window:
        envelope["conversationContext"] = list(plan.window)

    planted = []
    # A fact that needs authentication provenance is dropped when the batch deliberately carries
    # none, and the drop is recorded. Declaring it anyway would put an unverifiable claim in the
    # manifest: the row would come back NotApplicable because there were no results to judge, and
    # the manifest would be asserting something about a message it never sent.
    facts = [
        fact_id
        for fact_id in plan.facts
        if coverage == "full" or not FACTS[fact_id].get("requiresFullCoverage")
    ]
    dropped = [fact_id for fact_id in plan.facts if fact_id not in facts]

    planted = []
    for fact_id in facts:
        spec = FACTS[fact_id]
        entry = {
            "id": fact_id,
            "where": spec["where"],
            "site": spec["site"],
            "turn": plan.turn,
            "expected": expected_predicate(fact_id, plan),
        }
        if "constraint" in spec:
            entry["constraint"] = spec["constraint"]
        # `controlFor` names the fact this one is the control for. A control is not a second opinion
        # about the same property: it is a fact that must come back NEGATIVE, planted in the same
        # message, so that the expected positive cannot be satisfied by a detector that fires on
        # everything. The envelope fixture's refusal is only attributable to the envelope because
        # authentication is present and NOT firing beside it; declaring that in the manifest makes it
        # machine-checked rather than asserted in prose.
        if fact_id in plan.controls:
            entry["controlFor"] = plan.controls[fact_id]
        planted.append(entry)
    entry = {
        "index": plan.index,
        "file": f"{plan.index:03d}.eml",
        "sha256": hashlib.sha256(raw).hexdigest(),
        "coverage": coverage,
        "coverageReason": (
            "planted: no authentication provenance and no connecting IP"
            if coverage == "reduced"
            else None
        ),
        "submission": envelope,
        "thresholdTargeted": plan.threshold_targeted,
        "turn": plan.turn,
        # Character counts, so that a provider refusal is attributable to the FIXTURE rather than to
        # the pipeline. The adapter truncates each turn at 2,000 characters and the whole rendered
        # prompt is checked against NumCtx as UTF-8 bytes before the provider refuses outright rather
        # than truncating, so a window that is too long is a defect in the corpus and has to be
        # visible in the manifest. Counts are of the raw strings sent, not of the rendered prompt.
        "turnCharacters": len(plan.text),
        "windowCharacters": sum(len(entry) for entry in plan.window),
        "intent": {"label": plan.intent, "note": plan.note},
        "planted": planted,
        # A change that is real in the bytes but that no pipeline signal can yet be asked about.
        # Recorded rather than declared: `planted` is a list of claims `check` will hold the pipeline
        # to, and putting an unassertable one there would make a correct pipeline fail. Field present
        # only when there is such a change, so absence means "nothing was withheld".
        **({"undescribedChange": plan.undescribed_change} if plan.undescribed_change else {}),
        # Named rather than silently absent: a consumer comparing this batch against a full-coverage
        # one needs to know which facts were dropped for the coverage mode, not just how many
        # survived.
        "coverageDropped": dropped,
    }
    return entry


def cmd_generate(args: argparse.Namespace) -> int:
    if args.coverage not in COVERAGE:
        print(
            f"refusing: --coverage must be one of {COVERAGE}, and 'unspecified' is not a value "
            "this tool will invent. A batch whose coverage was an accident cannot be told from one "
            "whose coverage was planted, and only one of those is checkable.",
            file=sys.stderr,
        )
        return 2
    builders = PROFILES.get(args.profile)
    if builders is None:
        print(f"refusing: unknown profile {args.profile!r}; have {sorted(PROFILES)}", file=sys.stderr)
        return 2
    if args.count < 1:
        print("refusing: --count must be at least 1", file=sys.stderr)
        return 2

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    messages = []
    previous_raw: bytes | None = None
    for index in range(args.count):
        builder = builders[index % len(builders)]
        plan = builder(args.seed, index)
        raw = build_mime(
            sender_name=plan.sender_name,
            sender_address=plan.sender_address,
            subject=plan.subjects[0],
            text=plan.text,
            html=plan.html,
            facts=plan.facts,
            seed=args.seed,
            index=index,
        )
        # A turn whose window is the preceding message takes that message's RAW bytes, exactly as the
        # Host received them, because that is the text a real prior turn would have arrived as. It is
        # resolved here rather than in the plan builder because the bytes do not exist until the
        # previous iteration has built them.
        if plan.window_from_previous_turn:
            if previous_raw is None:
                print(
                    f"refusing: message {index} declares its window as the preceding turn, but this "
                    "batch has no preceding message. A windowless turn 2 would silently measure a "
                    "different question from the one it claims to, so it is not written.",
                    file=sys.stderr,
                )
                return 2
            plan.window = [previous_raw.decode("utf-8")]
        (out / f"{index:03d}.eml").write_bytes(raw)
        messages.append(manifest_entry(plan, raw, args.coverage))
        previous_raw = raw

    manifest = {
        "corpusVersion": CORPUS_VERSION,
        "generatedBy": "tools/corpus/corpus.py",
        "seed": args.seed,
        "profile": args.profile,
        "coverage": args.coverage,
        # Stated rather than omitted: nothing here lets a model write the corpus it is measured on,
        # and a batch that did would have to say so here so it could be reported apart.
        "authoredByModel": False,
        # Set by `ingest` when a batch is reconstituted from an operator dataset. A reconstituted
        # batch has NO planted facts, and it says so explicitly rather than leaving the field out,
        # because an absent field and an empty list read the same to a consumer and mean different
        # things.
        "source": None,
        "batchNote": (
            "Seeded, deterministic and self-authored. Every message is reproducible from "
            "(seed, index); no clock, no counter and no process randomness is involved."
        ),
        "messages": messages,
    }
    write_manifest(out / "manifest.json", manifest)

    counts: dict[str, int] = {}
    for message in messages:
        counts[message["coverage"]] = counts.get(message["coverage"], 0) + 1
    print(f"wrote {len(messages)} message(s) to {out}  profile={args.profile} coverage={counts}")
    print(f"manifest: {out / 'manifest.json'}  corpusVersion={CORPUS_VERSION}")
    return 0


def write_manifest(path: Path, manifest: dict) -> None:
    path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


# ---------------------------------------------------------------------------------------------
# HTTP, and the one thing that must never appear in it
# ---------------------------------------------------------------------------------------------

def read_key_file(path: str) -> str:
    """A key is a **path** to a file read in-process. Never an argument, never printed."""
    key_path = Path(path)
    if not key_path.is_file():
        raise SystemExit(
            f"refusing: --key-file {path!r} is not a readable file. The key is a path to a file and "
            "is never passed as a value, so this tool will not accept a key another way."
        )
    value = key_path.read_text(encoding="utf-8").strip()
    if not value:
        raise SystemExit(f"refusing: --key-file {path!r} is empty")
    return value


def call(method: str, url: str, key: str, body: dict | None = None, extra: dict | None = None):
    """One request. Returns (status, parsed body). Never raises on an HTTP error status."""
    headers = {"X-StyloMail-Key": key}
    headers.update(extra or {})
    data = None
    if body is not None:
        data = json.dumps(body).encode("utf-8")
        headers["Content-Type"] = "application/json"
    request = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            payload = response.read().decode("utf-8", errors="replace")
            return response.status, _parse(payload)
    except urllib.error.HTTPError as error:
        payload = error.read().decode("utf-8", errors="replace")
        return error.code, _parse(payload)
    except urllib.error.URLError as error:
        return 0, {"error": f"could not reach {url}", "detail": str(error.reason)}


def _parse(payload: str):
    try:
        return json.loads(payload)
    except json.JSONDecodeError:
        return payload


# ---------------------------------------------------------------------------------------------
# seed
# ---------------------------------------------------------------------------------------------

def batch_file(batch: Path, message: dict) -> Path | None:
    """Resolve a manifest's `file` INSIDE the batch directory, or refuse the whole batch.

    `message["file"]` arrives from the manifest, and a manifest can come from outside the operator's
    control: `ingest` reconstitutes a batch from an operator dataset, a batch can be carried between
    machines, and a manifest is a text file anyone can edit. Joined with no containment check, an
    entry of `../../something` resolves outside the batch, and its bytes are then read and POSTed to
    the Host. That is a narrow path but a real one, and it costs one `resolve()` to close.

    Two different failures, deliberately not collapsed:

      * **outside the batch** refuses the batch. A manifest that names a file outside itself is
        malformed rather than incomplete, and seeding the rest would report success for a batch that
        is not the batch described.
      * **absent inside the batch** returns None so the caller can skip that one message, which is
        the behaviour a partially-materialised batch needs.

    Symlinks are resolved too, so a link inside the batch pointing outside it is the first case and
    not the second.
    """
    name = message.get("file")
    if not isinstance(name, str) or not name.strip():
        raise SystemExit(f"refusing: message {message.get('index')!r} carries no `file` to read")
    batch_root = Path(batch).resolve()
    resolved = (batch_root / name).resolve()
    if resolved != batch_root and batch_root not in resolved.parents:
        raise SystemExit(
            f"refusing: {name!r} resolves outside the batch directory ({batch_root}). A manifest "
            "entry that names a file outside its own batch is malformed, and seeding the remainder "
            "would report success for a batch that is not the one described."
        )
    return resolved if resolved.is_file() else None


def cmd_seed(args: argparse.Namespace) -> int:
    key = read_key_file(args.key_file)
    batch = Path(args.batch)
    manifest_path = batch / "manifest.json"
    if not manifest_path.is_file():
        raise SystemExit(f"refusing: {manifest_path} does not exist; run `generate` first")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))

    base = args.base_url.rstrip("/")
    limit = args.limit if args.limit is not None else len(manifest["messages"])

    # The durable route, and this is load-bearing. POST /v1/assessments is assessment-only: it hands
    # the pipeline an ephemeral payload, so the MIME adapter never runs and a planted MIME fact can
    # never appear as a finding. Measured; the first measurement run was reduced to that one row.
    accepted, replayed, refused, unreachable = 0, 0, 0, 0
    for message in manifest["messages"][:limit]:
        raw_path = batch_file(batch, message)
        if raw_path is None:
            print(f"  {message['file']}: missing, skipped")
            continue
        payload = submission_body(message, raw_path)
        status, body = call(
            "POST",
            f"{base}/v1/submissions",
            key,
            payload,
            # Deterministic and stable across runs, so re-seeding a batch is idempotent rather than
            # producing a second row for the same bytes.
            {"Idempotency-Key": f"corpus-{manifest['seed']}-{message['index']}-{message['sha256'][:12]}"},
        )
        record = {"httpStatus": status, "queueId": None, "internalMessageId": None, "action": None}
        if isinstance(body, dict):
            record["queueId"] = body.get("queueId")
            recipients = body.get("recipients") or []
            if recipients and isinstance(recipients[0], dict):
                record["action"] = recipients[0].get("action")
            if not record["queueId"] and "queueId" in body:
                record["queueId"] = body["queueId"]
        # 202 is a new acceptance. A 200 carrying a queue id is the Host honouring the idempotency
        # promise: it already holds this submission and has returned the existing one. Counting
        # that as a refusal is how a re-seed of an unchanged batch reads as total failure, and
        # worse, the ledger it is checked against is then the earlier run's rather than this one's.
        # Measured: a second run against the same database returned 200 eight times out of eight.
        if status == 202:
            accepted += 1
        elif status == 200 and isinstance(body, dict) and body.get("queueId"):
            replayed += 1
            record["replayed"] = True
        elif status == 0:
            unreachable += 1
            record["error"] = body.get("error") if isinstance(body, dict) else str(body)[:200]
        else:
            refused += 1
            record["error"] = body if isinstance(body, str) else _error_code(body)
            # The route's refusal carries its reason. Kept because a refusal is the *only* evidence
            # an envelope fact leaves: it is declined at intake, so it never reaches the queue and
            # there is no ledger row to read it back from.
            if isinstance(body, dict) and body.get("detail"):
                record["refusalDetail"] = str(body["detail"])[:300]
        message["seeded"] = record

        # The join key, fetched while the row is new rather than re-derived later. The queue item
        # carries internalMessageId and so does the ledger, and that is how a message is joined to
        # its decision: the decision *listing* carries no queue id.
        if record["queueId"] and args.resolve_join:
            status, item = call("GET", f"{base}/v1/submissions/{record['queueId']}", key)
            if status == 200 and isinstance(item, dict):
                record["internalMessageId"] = item.get("internalMessageId")
                record["state"] = item.get("state")
        # `status` has been overwritten by the join lookup above when --resolve-join is on, so the
        # recorded status is printed rather than the variable. Printing the variable reported 200
        # for a submission that the manifest correctly recorded as 202.
        print(
            f"  {message['file']}: HTTP {record['httpStatus']} "
            f"queue={record['queueId'] or '-'} state={record.get('state') or '-'}"
        )

    # Only `seed` writes the manifest, and it writes only the `seeded` records it just produced.
    write_manifest(manifest_path, manifest)
    print(f"\naccepted {accepted}, replayed {replayed}, refused {refused}, unreachable {unreachable}")
    if replayed:
        print(
            "note: 'replayed' means the Host already held this submission under the same "
            "idempotency key and returned it rather than creating a second row. The decision "
            "checked back is the one from the earlier run, so a batch that is meant to measure "
            "new behaviour needs a Host with an empty database."
        )
    if unreachable:
        print(
            "note: 'unreachable' means no Host answered. A Host with no assessor declines the "
            "submission route with 503, which is correct behaviour rather than a data gap."
        )
    if accepted == 0 and limit and not replayed:
        print("refusing to report success: nothing was accepted, so no state was populated", file=sys.stderr)
        return 1
    return 0


def submission_body(message: dict, raw_path: Path) -> dict:
    """The submission body, taken from the manifest and the bytes. Nothing is rebuilt here.

    Every envelope field comes from `message["submission"]`, so what is sent is what the manifest
    says was planted. Rebuilding `rcptTo` here instead would let a manifest declare an empty
    recipient list while the Host saw one, which is precisely the class of claim this tool exists to
    refuse.
    """
    raw = raw_path.read_bytes()
    envelope = message.get("submission")
    if not isinstance(envelope, dict):
        raise SystemExit(
            f"refusing: {message.get('file')!r} has no `submission` block in the manifest. A "
            "manifest written before the envelope was recorded cannot be replayed, because seed "
            "would have to invent the envelope and the manifest would stop describing the message."
        )
    body = dict(envelope)
    body["rawMime"] = base64.b64encode(raw).decode("ascii")
    return body


def _error_code(body) -> str:
    if isinstance(body, dict):
        return str(body.get("error") or body.get("detail") or "")[:200]
    return str(body)[:200]


# ---------------------------------------------------------------------------------------------
# check
# ---------------------------------------------------------------------------------------------

def cmd_check(args: argparse.Namespace) -> int:
    key = read_key_file(args.key_file)
    manifest = json.loads(Path(args.manifest).read_text(encoding="utf-8"))
    base = args.base_url.rstrip("/")

    missing: list[str] = []
    unseeded: list[str] = []
    states: dict[str, int] = {}
    checked = 0

    for message in manifest["messages"]:
        seeded = message.get("seeded")
        if not seeded:
            unseeded.append(message["file"])
            continue

        # Refused at intake: an envelope fact is verified rather than judged, so the message is
        # declined before a queue row exists and there is nothing in the ledger to read. The refusal
        # is the observation, and it is checked as one rather than skipped, because skipping it is
        # how a planted fact goes unverified while the run reports success.
        if not seeded.get("queueId") and not seeded.get("internalMessageId"):
            if seeded.get("httpStatus") == 422:
                checked += 1
                states["RejectedAtIntake"] = states.get("RejectedAtIntake", 0) + 1
                for fact in message.get("planted", []):
                    if not fact["id"].startswith("envelope."):
                        missing.append(
                            f"{message['file']}: {fact['id']} ({fact['where']}) at {fact['site']} "
                            "could not be checked: the message was refused at intake, so the "
                            "deterministic layer never assessed its bytes"
                        )
                continue
            unseeded.append(message["file"])
            continue

        decision = fetch_decision(base, key, seeded)
        if decision is None:
            missing.append(f"{message['file']}: no decision could be read back from the ledger")
            continue
        checked += 1
        action = decision.get("action")
        states[action] = states.get(action, 0) + 1

        for fact in message.get("planted", []):
            ok, detail = fact_present(fact, decision)
            if not ok:
                missing.append(
                    f"{message['file']}: {fact['id']} expected {fact['expected']} "
                    f"({fact['where']}) at {fact['site']}, {detail}"
                )

    declared = sum(len(message.get("planted", [])) for message in manifest["messages"])
    print(f"checked {checked} message(s) against the ledger")
    print(f"states reached: {states or 'none'}")
    if unseeded:
        print(f"not seeded yet, so not checked: {len(unseeded)} message(s)")

    # A third outcome, distinct from pass and fail. A reconstituted batch declares no planted facts
    # on purpose, so there is nothing here to verify: reporting success would claim a verification
    # that did not happen, and reporting failure would call a correct batch broken. Exit 2 is the
    # same "skipped honestly" the ingest uses.
    if declared == 0:
        print(
            "\nSKIPPED: this batch declares no planted facts, so there is nothing to verify. Its "
            "states are reported above; that is the measurement it can support.",
            file=sys.stderr,
        )
        return 2

    if missing:
        print(f"\nFAIL: {len(missing)} planted fact(s) missing from the findings", file=sys.stderr)
        for line in missing:
            print(f"  {line}", file=sys.stderr)
        print(
            "\nA planted fact is in the message by construction. If the findings do not report it, "
            "that is a defect and this is the evidence, not a tolerance.",
            file=sys.stderr,
        )
        return 1
    if checked == 0:
        print(
            f"refusing to report success: {declared} fact(s) are declared but no message could be "
            "checked, which is not the same as passing",
            file=sys.stderr,
        )
        return 1
    print("\nOK: every planted fact was reported as planted")
    return 0


def fetch_decision(base: str, key: str, seeded: dict):
    """Read the full decision for a message back from the ledger.

    Two requests, and the second is not optional. `GET /v1/decisions?messageId=` returns *summaries*
    on purpose ("Rows are summaries; the evidence is one request away"), so a reader that stops at
    the listing finds an action and a risk index but no evidence at all, and every planted MIME fact
    looks missing. That produced a false FAIL on the first run of this checker: the facts were
    reported, the checker was reading the wrong document. The evidence is at
    `GET /v1/decisions/{assessmentId}`.

    The join is `internalMessageId`, which both the queue item and the ledger carry. The listing is
    newest first, so `[0]` is the newest assessment, which is the one the last seeding produced.
    """
    internal = seeded.get("internalMessageId")
    if not internal:
        queue_id = seeded.get("queueId")
        if not queue_id:
            return None
        status, item = call("GET", f"{base}/v1/submissions/{queue_id}", key)
        if status == 200 and isinstance(item, dict):
            internal = item.get("internalMessageId")
    if not internal:
        return None

    status, listing = call("GET", f"{base}/v1/decisions?messageId={internal}", key)
    if status != 200 or not isinstance(listing, dict):
        return None
    summaries = listing.get("decisions") or []
    if not summaries:
        return None
    assessment_id = summaries[0].get("assessmentId")

    status, detail = call("GET", f"{base}/v1/decisions/{assessment_id}", key)
    if status == 200 and isinstance(detail, dict):
        return detail
    # Falling back to the summary would silently drop the evidence and read as "every fact is
    # missing", so say so instead of returning a document that cannot answer the question.
    return {"action": summaries[0].get("action"), "evidence": None,
            "reasons": summaries[0].get("reasons"), "_detailUnavailable": status}


def fact_present(fact: dict, decision: dict) -> tuple[bool, str]:
    """Does what the pipeline reported satisfy the planted fact's PREDICATE, not merely its presence?

    The predicate is a dict rather than an integer because three kinds of claim are real and one
    integer can carry only the first:
      * `{"value": N}`                     a firing dimension, or a count such as trusted failures;
      * `{"available": true, "value": 0}`  present and NOT firing (a benign message's whole claim,
                                           and the shape of a control);
      * `{"available": true, "origin": X}` a claim about provenance rather than about a value;
      * `{"available": true|false}`        a claim about whether the signal was computable at all,
                                           which is what a conversation window decides.

    A control is compared by the same code as any other fact. That is deliberate: if the control
    could only be checked by a separate path, a detector firing on everything would still pass the
    claim it was supposed to make honest.

    One measured trap is handled here rather than rediscovered by a consumer: an envelope fact is
    not a risk finding at all. It is verified before the semantic provider runs, so it is checked
    through the action and the reason code instead of through an evidence row.
    """
    fact_id = fact["id"]
    expected = fact.get("expected") or {}
    control_for = fact.get("controlFor")
    where = fact.get("where")

    if where == "reasons":
        # Checked against the reason codes rather than the evidence, and deliberately BEFORE the
        # evidence check below: a message refused at intake has no evidence rows at all, which is
        # exactly the message this claim is about.
        codes = _reason_codes(decision)
        fired = sorted(code for code in codes if code.startswith("policy.risk_above"))
        ok = len(fired) == expected.get("value", 0)
        return ok, f"risk-threshold reason codes: {fired or 'none'}"

    if decision.get("evidence") is None:
        return False, (
            "the decision detail could not be read (HTTP "
            f"{decision.get('_detailUnavailable')}), so the evidence was never inspected"
        )

    if fact_id.startswith("envelope."):
        codes = _reason_codes(decision)
        ok = decision.get("action") == "Reject" and "policy.verified_violation" in codes
        return ok, f"action={decision.get('action')} reasons={sorted(codes)}"

    rows = {}
    for row in decision.get("evidence") or []:
        if isinstance(row, dict) and row.get("signalId"):
            rows[row["signalId"]] = row
    row = rows.get(fact_id)
    if row is None:
        return False, "the signal is not in the evidence list at all"

    availability = row.get("availability")
    value = row.get("value")
    origin = row.get("origin")

    if "available" in expected:
        # An availability claim is satisfied by the availability the pipeline reported, either way
        # round. `false` is a real claim ("this could not be computed, and the manifest says so")
        # rather than a missing one, which is why an absent predicate is refused rather than assumed.
        want_available = bool(expected["available"])
        got_available = availability == "Available"
        if want_available != got_available:
            return False, (
                f"availability={availability} (value={value}) but the manifest expects "
                f"{'Available' if want_available else 'not Available'}"
            )
        if not want_available:
            return True, f"availability={availability}, value={value} as planted"
    elif availability != "Available":
        return False, f"signal present but availability={availability}, value={value}"

    if "value" in expected and value != expected["value"]:
        suffix = f" (this fact is the control for {control_for})" if control_for else ""
        return False, f"available but value={value}, expected {expected['value']}{suffix}"
    if "origin" in expected and origin != expected["origin"]:
        return False, f"origin={origin}, expected {expected['origin']}"
    return True, f"value={value} origin={origin}"


def _reason_codes(decision: dict) -> set[str]:
    codes = set()
    for reason in decision.get("reasons") or []:
        if isinstance(reason, dict) and reason.get("code"):
            codes.add(reason["code"])
        elif isinstance(reason, str):
            codes.add(reason)
    return codes


# ---------------------------------------------------------------------------------------------
# ingest: the operator datasets, reconstituted and labelled as such
# ---------------------------------------------------------------------------------------------

EMAIL = re.compile(r"[\w.+-]+@[\w-]+\.[\w.-]+")


def cmd_ingest(args: argparse.Namespace) -> int:
    root = os.environ.get("STYLOMAIL_CORPUS_DIR")
    if not root:
        print(
            "skipped: STYLOMAIL_CORPUS_DIR is not set. The datasets live outside the repository "
            "and this tool has no default path into a home directory.",
            file=sys.stderr,
        )
        return 2
    # The category refusal comes first, before the file lookup, because adopting spamham.zip is
    # wrong whether or not the file is present: it is a short-text corpus with no mail headers, and
    # a run that reported "not a file" would imply the only problem was the path.
    if args.source_archive != "emails.zip":
        print(
            f"refusing: {args.source_archive} is not reconstituted into MIME. Only emails.zip has "
            "body and subject columns. spamham.zip is short-text shaped with no mail headers and "
            "cannot stand in for mail; adopting it would produce a corpus whose shape is not the "
            "shape it claims to be.",
            file=sys.stderr,
        )
        return 2
    archive = Path(root) / args.source_archive
    if not archive.is_file():
        print(f"skipped: {archive} is not a file", file=sys.stderr)
        return 2

    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    with zipfile.ZipFile(archive) as zf:
        member = next((n for n in zf.namelist() if not n.endswith("/")), None)
        if member is None:
            raise SystemExit(f"refusing: {archive.name} has no member files")
        raw_csv = zf.read(member)

    reader = csv.reader(io.StringIO(raw_csv.decode("utf-8", errors="replace")))
    columns = next(reader)
    required = ["subject", "email_body", "label"]
    absent = [name for name in required if name not in columns]
    if absent:
        raise SystemExit(f"refusing: the dataset has no {absent} column; its shape has changed")

    body_at, subject_at, label_at = (columns.index(n) for n in ("email_body", "subject", "label"))

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    messages, dropped = [], 0
    for row in reader:
        if len(row) != len(columns):
            dropped += 1
            continue
        if len(messages) >= args.limit:
            break
        index = len(messages)
        body_text = row[body_at].strip()
        subject = row[subject_at].strip() or "(no subject)"
        if not body_text:
            dropped += 1
            continue
        raw = build_mime(
            sender_name="Corpus Source",
            sender_address=f"source@{SENDER_DOMAIN}",
            subject=subject,
            text=body_text,
            html=None,
            facts=[],
            seed=args.seed,
            index=index,
        )
        (out / f"{index:03d}.eml").write_bytes(raw)
        messages.append(
            {
                "index": index,
                "file": f"{index:03d}.eml",
                "sha256": hashlib.sha256(raw).hexdigest(),
                # A reconstituted batch measures the low-coverage path on purpose: the dataset has
                # no authentication results, and synthesising them would smuggle its spf_status and
                # dkim_status columns into the pipeline through this tool's own template.
                "coverage": "reduced",
                "coverageReason": (
                    "planted: the source corpus carries no authentication results, and this tool "
                    "will not synthesise them, because doing so would assert the dataset's own "
                    "derived columns back to the pipeline"
                ),
                "thresholdTargeted": False,
                # Recorded here as well, for the same reason the generated path records it: `seed`
                # replays what the manifest says, and a manifest with no envelope would make `seed`
                # invent one. That is exactly the drift the envelope field was added to remove.
                "submission": {
                    "direction": "Inbound",
                    "mailFrom": f"source@{SENDER_DOMAIN}",
                    "rcptTo": [RECIPIENT],
                },
                "intent": {
                    "label": row[label_at].strip(),
                    "note": (
                        "the source corpus's own label, carried as corpus intent only: a "
                        "denominator, never evidence and never a policy input"
                    ),
                },
                # Explicit, not omitted: the envelope is authored by this tool, so a header-derived
                # finding would be a finding about the template rather than about the source, and
                # there are no deterministic facts to plant from a body-and-subject-only row.
                "planted": [],
                "plantedNote": (
                    "reconstituted, so no planted facts: the body and subject bytes are the "
                    "dataset's and everything else in the message is this tool's template"
                ),
            }
        )

    manifest = {
        "corpusVersion": CORPUS_VERSION,
        "generatedBy": "tools/corpus/corpus.py",
        "seed": args.seed,
        "profile": "reconstituted",
        "coverage": "reduced",
        "authoredByModel": False,
        "source": {"name": args.source_archive, "sha256": digest, "member": member},
        "batchNote": (
            "Reconstituted from an operator dataset. The body and subject are the source's; the "
            "envelope, headers and MIME structure are this tool's, so a header-derived finding is a "
            "finding about this template rather than about the source. No derived column from the "
            "source (label, phishing_probability, spf_status, dkim_status, dmarc_status, "
            "urgency_score) is read into any pipeline input."
        ),
        "messages": messages,
    }
    write_manifest(out / "manifest.json", manifest)
    print(f"reconstituted {len(messages)} row(s) into {out}, dropped {dropped} malformed row(s)")
    print(f"source {args.source_archive} sha256 {digest[:16]}...  corpusVersion={CORPUS_VERSION}")
    return 0


# ---------------------------------------------------------------------------------------------

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    gen = sub.add_parser("generate", help="write NNN.eml plus a versioned manifest.json")
    gen.add_argument("--seed", type=int, required=True)
    gen.add_argument("--count", type=int, required=True)
    gen.add_argument("--out", required=True)
    gen.add_argument("--profile", default="mixed", choices=sorted(PROFILES))
    gen.add_argument(
        "--coverage",
        default="full",
        choices=list(COVERAGE),
        help="full carries authentication provenance and a connecting IP; reduced omits both on purpose",
    )
    gen.set_defaults(func=cmd_generate)

    seed_cmd = sub.add_parser("seed", help="post a batch through the authenticated routes")
    seed_cmd.add_argument("--base-url", required=True)
    seed_cmd.add_argument("--key-file", required=True, help="a PATH to a file, never a key value")
    seed_cmd.add_argument("--batch", required=True)
    seed_cmd.add_argument("--limit", type=int, default=None)
    seed_cmd.add_argument(
        "--resolve-join",
        action="store_true",
        help="also fetch each queue item's internalMessageId, the join key for its decision",
    )
    seed_cmd.set_defaults(func=cmd_seed)

    check = sub.add_parser("check", help="read the decisions back and compare the planted facts")
    check.add_argument("--base-url", required=True)
    check.add_argument("--key-file", required=True, help="a PATH to a file, never a key value")
    check.add_argument("--manifest", required=True)
    check.set_defaults(func=cmd_check)

    ingest = sub.add_parser("ingest", help="reconstitute an operator dataset into raw MIME")
    ingest.add_argument("--source-archive", default="emails.zip")
    ingest.add_argument("--limit", type=int, default=500)
    ingest.add_argument("--seed", type=int, default=0)
    ingest.add_argument("--out", required=True)
    ingest.set_defaults(func=cmd_ingest)

    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
