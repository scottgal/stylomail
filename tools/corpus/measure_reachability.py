#!/usr/bin/env python3
"""Measure which states a throwaway Host can actually reach, before any corpus is built.

This is the corpus lane's first deliverable: four questions answered against a real Host rather
than argued from the source. It builds nothing and
it writes no message the operator could mistake for mail. It plants deterministic facts in a
handful of messages, posts them through the authenticated routes on loopback, and records what
the pipeline actually decided and what the console's own listings then show.

    python3 tools/corpus/measure_reachability.py --mode jev   --out .styloagent/scratch/corpus/jev.json
    python3 tools/corpus/measure_reachability.py --mode nimble --out .styloagent/scratch/corpus/nimble.json

Two modes, because the answer to "is a quarantined row reachable with no provider credential?"
turns on which provider is composed:

  * ``jev``    - the console harness's own wiring: a placeholder key and an endpoint that cannot
                 answer, so every semantic dimension comes back Unavailable. This is the state the
                 console cannot currently show, and the measurement of what it can reach.
  * ``nimble`` - the local provider, which holds no credential at all (decisions 17 and 18). If a
                 quarantined or held row is reachable here, it is reachable with no provider key.

NO TRANSPORT. The Host is started with no ``StyloMail:Transport:Upstream:Host``, which is the
switch ``QueueDeliveryHostedService`` reads before it builds a port at all: with no upstream
configured the delivery worker does not run and nothing can dial out. The script checks that
before it starts the Host and reports it in the result rather than assuming it.

NO SECRET VALUES. Both the principal key and the profile master key are generated per run into
the run directory and never printed, logged or written to the result. The Jev placeholder is the
same literal the console harness uses because these runs make no provider call: it is not a
credential and never was one.

NOT A GENERATOR. The messages below are fixed literals so this measurement is reproducible, not a
seeded batch with a manifest. That is the next deliverable, once the manifest shape is confirmed.

EXIT CODES, because a non-zero exit here does not mean what it usually means. ``0`` means the run
measured. ``2`` means the instrument REFUSED to measure: it printed ``REFUSING: <reason>`` on
stderr because the Host never became live, or an input it needed was absent. A refusal is a
statement about this instrument's ability to run, NEVER about the pipeline: do not read exit 2 as
"the property failed", and do not quote it as a reachability result. A traceback (exit 1) is a bug
in this file, which is also not a verdict about the pipeline.

WHERE IT WRITES. ``--out`` is the JSON result. ``--run-dir`` holds the Host log, the per-decision
JSON dumps and the run's generated key files. Both default under ``.styloagent/scratch/corpus/``
(``--run-dir`` defaults to ``.styloagent/scratch/corpus/measure``), and everything this tool writes
stays there: never ``/tmp``, and never in the repo outside scratch.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import secrets
import shutil
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
HOST_BINARY = REPO / "src/StyloMail.Host/bin/Debug/net10.0/StyloMail.Host"

# The states GET /v1/messages accepts, from ListingEndpoints.Filters. The console requests exactly
# these three (ShellModel.cs), so they are what "a populated queue" means to the console.
QUEUE_STATES = ["awaiting_decision", "held", "quarantined"]

# A link label disagreeing with its href, an IDN homograph, a display name that is itself an
# address, an HTML body and a text part that share nothing, and a declared type that disagrees
# with the file name. Each is a fact that is really in the MIME, so the pipeline reporting no
# finding for it names a defect rather than a low score.
_MISMATCH_LINK = (
    '<html><body><p>Verify your account.</p>'
    '<p><a href="http://198.51.100.9/v">https://accounts.example.test/login</a></p>'
    "</body></html>"
)
_HOMOGRAPH_LINK = (
    '<html><body><p>Your invoice is ready.</p>'
    '<p><a href="https://аccounts.example.test/inv">https://accounts.example.test/inv</a></p>'
    "</body></html>"
)


def _message(
    *,
    subject: str,
    sender: str,
    body: bytes,
    content_type: str = 'text/plain; charset="utf-8"',
    extra_headers: list[tuple[str, str]] | None = None,
) -> bytes:
    """One raw MIME message, deterministically: no clock, no random, no counter."""
    headers = [
        f"From: {sender}",
        "To: operator@example.test",
        f"Subject: {subject}",
        "Date: Tue, 30 Sep 2026 09:00:00 +0000",
        f"Message-ID: <{hashlib.sha256(subject.encode()).hexdigest()[:16]}@example.test>",
        "MIME-Version: 1.0",
        f"Content-Type: {content_type}",
    ]
    headers.extend(f"{name}: {value}" for name, value in (extra_headers or []))
    return ("\r\n".join(headers) + "\r\n\r\n").encode("utf-8") + body


def _alternative(boundary: str, text: str, html: str) -> bytes:
    """A multipart/alternative whose two representations disagree."""
    return (
        f"--{boundary}\r\nContent-Type: text/plain; charset=\"utf-8\"\r\n\r\n{text}\r\n"
        f"--{boundary}\r\nContent-Type: text/html; charset=\"utf-8\"\r\n\r\n{html}\r\n"
        f"--{boundary}--\r\n"
    ).encode("utf-8")


def hard_cases() -> list[dict]:
    """Messages built to trip as many *weighted* dimensions as the traffic can carry.

    The core suite answers "which state can this traffic reach"; this one answers whether any
    state above the hold threshold is reachable at all with the credential-free local provider.
    Weighted dimensions and their weights (PolicyOptions.DimensionWeights): credential_request and
    payment_redirection 1.0, link_lure, attachment_lure and sensitive_data_request 0.8,
    secrecy_bypass and threat_reward_inducement 0.7, identity_authority_claim 0.6, urgency 0.4,
    unsolicited_solicitation 0.3, transactional_character 0.2. The total is 7.8, but the denominator
    is the *covered* weight and conversational continuity reads NotApplicable on a single message,
    so in practice it is 7.3 and QuarantineThreshold 0.80 needs 5.84 rather than 6.24. Measured, not
    computed: the best message here scored 4.6/7.3 = 0.6301 exactly.

    These are authored to be as close to unambiguously malicious as authored text can be, which is
    the point: if the local provider cannot reach the hold threshold on text this explicit, then a
    corpus that reached it would have to have been written to fit the model, and a rate measured
    over such a corpus measures self-consistency rather than detection (decision 27).
    """
    long_plain = (
        "Dear customer, we detected unusual sign-in activity on your account and we have "
        "temporarily limited access to protect your funds. To restore full access you must confirm "
        "your identity today. Open the secure portal below, sign in with your online banking "
        "username and password, and confirm the one time code we send to your registered phone. "
        "If you do not confirm within twenty four hours your account will be suspended and any "
        "pending payments will be returned unpaid. Our security team never asks for your password "
        "by email, so please complete this only through the secure portal linked below."
    )
    credential_html = (
        "<html><body><p>Dear customer, we detected unusual sign-in activity on your account.</p>"
        "<p>Confirm your identity today or your account will be suspended within 24 hours.</p>"
        '<form action="http://198.51.100.9/session" method="post">'
        '<input type="text" name="username" placeholder="Online banking username">'
        '<input type="password" name="password" placeholder="Password">'
        "</form>"
        '<p><a href="http://198.51.100.9/session">https://secure.example-bank.test/login</a></p>'
        "</body></html>"
    )

    return [
        {
            "name": "hard-credential-phishing",
            "planted": ["deterministic.link_display_mismatch", "deterministic.trusted_authentication_failure"],
            "intent": "credential phishing",
            "note": "password form posting off-domain, mismatched anchor, urgency, suspension threat",
            "mime": _message(
                subject="Action required: your account has been limited",
                sender='"Example Bank Security" <security@example-bank.test>',
                body=_alternative("h1", long_plain, credential_html),
                content_type='multipart/alternative; boundary="h1"',
            ),
            "request": {
                "authenticationResults": [
                    {"mechanism": "dmarc", "result": "fail", "fromTrustedVerifier": True},
                    {"mechanism": "spf", "result": "fail", "fromTrustedVerifier": True},
                ]
            },
        },
        {
            "name": "hard-payment-redirection",
            "planted": [],
            "intent": "credential phishing",
            "note": "a changed bank account, a confidentiality instruction and a new domain",
            "mime": _message(
                subject="Updated remittance details for invoice 88213",
                sender='"Accounts Payable" <payments@example-supplier.test>',
                body=(
                    "Hello, our banking arrangements have changed and all future payments must go "
                    "to the new account shown below. Please update your records before the next "
                    "run and treat this message as confidential: do not discuss it with our "
                    "account manager, who is on leave until the change is complete. The new "
                    "account details are held in the attached remittance advice. Please confirm "
                    "that the change has been made and that the next payment will be redirected.\r\n"
                ).encode("utf-8"),
            ),
            "request": {},
        },
        {
            "name": "hard-ceo-wire-secrecy",
            "planted": [],
            "intent": "credential phishing",
            "note": "authority claim, secrecy instruction, urgency and a wire request",
            "mime": _message(
                subject="Quick favour",
                sender='"Chief Executive" <ceo@gmail.com>',
                body=(
                    "I am in a board meeting and cannot take calls. I need you to arrange an "
                    "urgent payment to a new supplier today, before the window closes. Do not "
                    "discuss this with anyone, and do not mention it to the finance team until it "
                    "has cleared, it is commercially sensitive. Reply with confirmation that the "
                    "transfer has been made and keep this between us.\r\n"
                ).encode("utf-8"),
            ),
            "request": {},
        },
        {
            "name": "hard-sensitive-data-request",
            "planted": [],
            "intent": "credential phishing",
            "note": "asks for identity documents and national identifiers by reply",
            "mime": _message(
                subject="Payroll activation: documents required",
                sender='"Payroll Team" <payroll@example-hr.test>',
                body=(
                    "To complete your payroll activation we need you to reply with your full "
                    "national insurance number, your date of birth, and a scanned copy of your "
                    "passport or driving licence. We also need your bank account number and sort "
                    "code so that salary payments can be released this month. Please send these "
                    "documents by reply to this message as soon as possible so that your first "
                    "payment is not delayed.\r\n"
                ).encode("utf-8"),
            ),
            "request": {},
        },
        {
            "name": "hard-bulk-unsolicited",
            "planted": ["deterministic.link_display_mismatch"],
            "intent": "bulk unsolicited",
            "note": "a promotional blast with a tracking pixel and an off-domain offer link",
            "mime": _message(
                subject="Your exclusive 90% discount ends tonight",
                sender='"Deals" <offers@example-promo.test>',
                body=(
                    '<html><body><p>Act now: ninety percent off everything, today only.</p>'
                    '<p>Thousands of customers have already claimed this limited time offer.</p>'
                    '<img src="http://198.51.100.9/pixel.gif" width="1" height="1">'
                    '<p><a href="http://198.51.100.9/offer">https://www.example-promo.test/offer</a></p>'
                    "</body></html>"
                ).encode("utf-8"),
                content_type='text/html; charset="utf-8"',
            ),
            "request": {},
        },
        {
            "name": "hard-everything-weighted",
            "planted": ["deterministic.link_display_mismatch", "deterministic.attachment_type_mismatch",
                        "deterministic.trusted_authentication_failure"],
            "intent": "credential phishing",
            "note": "every weighted dimension the text can carry at once, to find the ceiling",
            "mime": _message(
                subject="Confidential: urgent bank change and identity check",
                sender='"Chief Executive" <ceo@example-corp.test>',
                body=(
                    "This is urgent and strictly confidential, please do not discuss it with "
                    "anyone until I say otherwise, not even the finance team.\r\n"
                    "Our bank details have changed and payment must be redirected to the new "
                    "account in the attached remittance advice today, before the deadline. "
                    "Please keep this between us.\r\n"
                    "You must also confirm your identity on the secure portal below by signing in "
                    "with your online banking username and password and entering the one time code "
                    "and your national insurance number, your date of birth and your account "
                    "number, or your access and your salary payment will be suspended within "
                    "twenty four hours and you may face disciplinary action.\r\n"
                    '<div style="display:none">hidden text</div>'
                    '<img src="http://198.51.100.9/pixel.gif" width="1" height="1">\r\n'
                ).encode("utf-8"),
                content_type='multipart/mixed; boundary="m1"',
                extra_headers=[("X-Priority", "1")],
            ),
            "request": {
                "authenticationResults": [
                    {"mechanism": "dmarc", "result": "fail", "fromTrustedVerifier": True}
                ]
            },
        },
        {
            "name": "control-ordinary-correspondence",
            "planted": [],
            "intent": "ordinary correspondence",
            "note": "the opposite control: an ordinary message from a known sender, nothing planted",
            "mime": _message(
                subject="Notes from this morning's review",
                sender="Alice Example <alice@example.test>",
                body=(
                    "Thanks for the review this morning. I have attached the notes we agreed and "
                    "the updated schedule for the next two weeks. Nothing here needs a decision "
                    "from you before Thursday, so please just confirm that the dates work and I "
                    "will circulate them to the others. Speak tomorrow.\r\n"
                ).encode("utf-8"),
            ),
            "request": {},
        },
    ]


def _mixed(boundary: str, parts: list[tuple[str, bytes]]) -> bytes:
    """A multipart/mixed with real parts: without parts there is no link and no attachment to read."""
    out = b""
    for headers, body in parts:
        out += f"--{boundary}\r\n{headers}\r\n\r\n".encode("utf-8") + body + b"\r\n"
    return out + f"--{boundary}--\r\n".encode("utf-8")


def _invoice_pdf() -> bytes:
    """Bytes that are not a PDF, declared as one, so the type mismatch is real rather than declared."""
    return b"Invoice attachment placeholder. This is not a PDF file at all.\r\n"


def cases_quarantine() -> list[dict]:
    """Aim at the quarantine threshold on purpose, from the weights rather than from vehemence.

    The index is a weighted mean over *semantic* weight only: `PolicyOptions.DimensionWeights`
    (`src/StyloMail.Policy/PolicyContracts.cs:119`) lists eleven semantic dimensions and nothing
    else, so `campaign.*` and `behavioural.*` carry no weight however Available they become. The
    covered denominator is 7.3 (conversational continuity is NotApplicable on single messages) and
    0.80 therefore needs a weighted sum of 5.84.

    That reproduces the measured 0.6301 exactly: the best message so far tripped credential_request
    1.0, payment_redirection 1.0, sensitive_data_request 0.8, secrecy_bypass 0.7,
    threat_reward_inducement 0.7 and urgency_pressure 0.4 for 4.6/7.3. Adding link_lure 0.8 and
    identity_authority_claim 0.6 for 6.0/7.3 = 0.822 crosses it.

    The variants run focused to full because the local model's answers are not monotonic in how
    malicious the text looks: the earlier "everything weighted at once" message scored *lower* (0.37)
    than a narrower one did. So the point of the suite is to find the wording that holds eight
    semantic answers at once, not to pile on more.
    """
    frauds = [
        (
            "q1-authority-payment",
            "payment_redirection, credential_request, authority claim, urgency: the narrowest set that can cross",
            "HM Revenue & Customs",
            "Your tax account shows an outstanding balance of GBP 2,847.60 that must be settled today. "
            "Pay the balance to the account below, or confirm the card details we hold on file so the "
            "payment can be taken automatically. Your account password is required to authorise the "
            "transfer. This must be completed within 24 hours or enforcement action will begin.",
            "Pay the outstanding balance immediately",
        ),
        (
            "q2-authority-plus-data",
            "q1 plus a request for identity documents, which is the sensitive_data_request dimension",
            "HM Revenue & Customs",
            "Your tax account shows an outstanding balance of GBP 2,847.60 that must be settled today. "
            "Pay the balance to the account below, or confirm the card details we hold on file so the "
            "payment can be taken automatically. Your account password is required to authorise the "
            "transfer. We also require a copy of your passport and your national insurance number to "
            "verify your identity before the account can be cleared. This must be completed within 24 "
            "hours or enforcement action will begin.",
            "Pay the outstanding balance immediately",
        ),
        (
            "q3-plus-secrecy",
            "q2 plus a confidentiality instruction, which is the secrecy_bypass dimension",
            "HM Revenue & Customs",
            "Your tax account shows an outstanding balance of GBP 2,847.60 that must be settled today. "
            "Pay the balance to the account below, or confirm the card details we hold on file so the "
            "payment can be taken automatically. Your account password is required to authorise the "
            "transfer. We also require a copy of your passport and your national insurance number to "
            "verify your identity before the account can be cleared. Keep this notice confidential and "
            "do not discuss it with anyone else, including your accountant. This must be completed "
            "within 24 hours or enforcement action will begin.",
            "Pay the outstanding balance immediately",
        ),
        (
            "q4-with-attachment",
            "q3 plus a real attachment part, for the attachment_lure dimension",
            "HM Revenue & Customs",
            "Your tax account shows an outstanding balance of GBP 2,847.60 that must be settled today. "
            "The attached invoice gives the full breakdown. Pay the balance to the account below, or "
            "confirm the card details we hold on file so the payment can be taken automatically. Your "
            "account password is required to authorise the transfer. We also require a copy of your "
            "passport and your national insurance number to verify your identity before the account can "
            "be cleared. Keep this notice confidential and do not discuss it with anyone else, "
            "including your accountant. This must be completed within 24 hours or enforcement action "
            "will begin. Open the attached invoice to review the amount payable.",
            "Pay the outstanding balance immediately",
        ),
    ]

    out = []
    for name, note, authority, body, link_text in frauds:
        html = (
            f"<html><body><p>{body}</p>"
            f'<p><a href="http://203.0.113.9/portal/settle">https://www.gov-gateway.test/portal/settle</a></p>'
            f"<p>{link_text}. {authority}.</p></body></html>"
        )
        parts = [
            (
                'Content-Type: multipart/alternative; boundary="alt"',
                _alternative("alt", body, html),
            )
        ]
        if name == "q4-with-attachment":
            parts.append(
                (
                    'Content-Type: application/pdf; name="invoice-284760.pdf"\r\n'
                    'Content-Transfer-Encoding: base64\r\n'
                    'Content-Disposition: attachment; filename="invoice-284760.pdf"',
                    base64.encodebytes(_invoice_pdf()),
                )
            )
        mime = _message(
            sender=f'"{authority}" <refunds@gov-gateway.test>',
            subject=f"Outstanding balance GBP 2,847.60: action required today ({name})",
            body=_mixed("mix", parts),
            content_type='multipart/mixed; boundary="mix"',
        )
        out.append(
            {
                "name": name,
                "planted": ["deterministic.link_display_mismatch"],
                "intent": "credential phishing",
                "note": note,
                "mime": mime,
                "request": {
                    "direction": "Inbound",
                    "mailFrom": "refunds@gov-gateway.test",
                    "rcptTo": ["operator@example.test"],
                    "connectingIp": "198.51.100.9",
                    "authenticationResults": [
                        {"mechanism": "spf", "result": "fail", "fromTrustedVerifier": True},
                        {"mechanism": "dkim", "result": "fail", "fromTrustedVerifier": True},
                        {"mechanism": "dmarc", "result": "fail", "fromTrustedVerifier": True},
                    ],
                },
            }
        )
    return out


def cases_quarantine_stage2() -> list[dict]:
    """One dimension short of quarantine, so the next message adds exactly that dimension.

    Measured on the first stage (`.styloagent/scratch/corpus/nimble-quarantine.json`), and the index
    matched the weighted arithmetic to four decimals each time:

        q1  5 dimensions -> 2.9/7.3 = 0.3973  Allow
        q2  5 dimensions -> 3.5/7.3 = 0.4795  Allow
        q3  6 dimensions -> 4.2/7.3 = 0.5753  Hold
        q4  7 dimensions -> 5.0/7.3 = 0.6849  Hold

    Quarantine needs 5.84. q4 is at 5.0 and `payment_redirection` alone is worth 1.0, so q5 puts the
    arithmetic at 6.0/7.3 = 0.8219 and past the threshold. q6 adds `link_lure` (0.8) for margin, in
    case one of the seven q4 answers drops.

    The reason q5 is not just q4 with a sentence bolted on: `payment_redirection` is the one
    dimension stage 1 never got, on wording that said "pay the balance to the account below". The
    canonical shape of that fraud is a **changed** destination, which is what q5 supplies.
    """
    base_body = (
        "Your tax account shows an outstanding balance of GBP 2,847.60 that must be settled today. "
        "The attached invoice gives the full breakdown. Our bank details have changed, so payments "
        "sent to the old account will be returned: send the payment to the new account, sort code "
        "04-00-04, account number 12345678, held at Example Bank. You may also confirm the card "
        "details we hold on file so the payment can be taken automatically, and your account password "
        "is required to authorise the transfer. We also require a copy of your passport and your "
        "national insurance number to verify your identity before the account can be cleared. Keep "
        "this notice confidential and do not discuss it with anyone else, including your accountant. "
        "This must be completed within 24 hours or enforcement action will begin. Open the attached "
        "invoice to review the amount payable."
    )
    variants = [
        ("q5-changed-account", "q4 plus an explicitly changed payment destination", base_body),
        (
            "q6-plus-link-lure",
            "q5 plus the link presented as the way to act, for margin on the eighth dimension",
            base_body + " Pay securely through the portal linked below if you prefer not to use the "
            "bank transfer.",
        ),
    ]

    out = []
    for name, note, body in variants:
        html = (
            f"<html><body><p>{body}</p>"
            f'<p><a href="http://203.0.113.9/pay">Click here to pay the outstanding balance securely</a></p>'
            f"</body></html>"
        )
        parts = [
            ('Content-Type: multipart/alternative; boundary="alt"', _alternative("alt", body, html)),
            (
                'Content-Type: application/pdf; name="invoice-284760.pdf"\r\n'
                'Content-Transfer-Encoding: base64\r\n'
                'Content-Disposition: attachment; filename="invoice-284760.pdf"',
                base64.encodebytes(_invoice_pdf()),
            ),
        ]
        mime = _message(
            sender='"HM Revenue & Customs" <refunds@gov-gateway.test>',
            subject=f"Outstanding balance GBP 2,847.60: action required today ({name})",
            body=_mixed("mix", parts),
            content_type='multipart/mixed; boundary="mix"',
        )
        out.append(
            {
                "name": name,
                "planted": ["deterministic.link_display_mismatch"],
                "intent": "payment redirection fraud",
                "note": note,
                "mime": mime,
                "request": {
                    "direction": "Inbound",
                    "mailFrom": "refunds@gov-gateway.test",
                    "rcptTo": ["operator@example.test"],
                    "connectingIp": "198.51.100.9",
                    "authenticationResults": [
                        {"mechanism": "spf", "result": "fail", "fromTrustedVerifier": True},
                        {"mechanism": "dkim", "result": "fail", "fromTrustedVerifier": True},
                        {"mechanism": "dmarc", "result": "fail", "fromTrustedVerifier": True},
                    ],
                },
            }
        )
    return out


def cases_repeat(count: int = 8) -> list[dict]:
    """One sender, many similar messages, to test whether history is what quarantine needs.

    `desktop-` measured single messages at 0.48, 0.55 and 0.58 against a 0.80 threshold and observed
    that every dimension which could carry a message higher (`behavioural.*`, `campaign.near_duplicate`)
    came back `Unavailable` with `sampleSupport: 0`. Its leading hypothesis is that a repeated sender
    is what makes those dimensions available. That is testable only by sending the same sender a batch
    and reading the index of each message in order, which is what this suite is for.

    The bodies are near-duplicates rather than identical: a campaign template with a per-message token.
    Each carries the same credential request, so the semantic rows should stay flat while any campaign
    or behavioural row climbs, and the two effects are separable in the result.
    """
    out = []
    for index in range(count):
        token = f"ref-{index:04d}-{hashlib.sha256(str(index).encode()).hexdigest()[:8]}"
        body = (
            f"Dear customer, our billing system could not process your last payment. "
            f"To avoid suspension of your account, confirm your card details and your account "
            f"password at the link below within 24 hours. Reference {token}. "
            f"Failure to respond will result in the closure of your account. "
            f"Regards, Billing Department, accounts-payments.test "
        )
        html = (
            f"<html><body><p>Dear customer, our billing system could not process your last payment. "
            f"To avoid suspension of your account, confirm your card details and your account "
            f"password at the link below within 24 hours. Reference {token}. "
            f"Failure to respond will result in the closure of your account.</p>"
            f'<p><a href="http://203.0.113.7/pay/{token}">https://accounts-payments.test/login</a></p>'
            f"<p>Regards, Billing Department, accounts-payments.test</p></body></html>"
        )
        mime = _message(
            sender='"Billing" <billing@accounts-payments.test>',
            subject=f"Payment failed: action required ({token})",
            body=_alternative(f"repeat{index}", body, html),
            content_type='multipart/alternative; boundary="repeat%d"' % index,
        )
        out.append(
            {
                "name": f"repeat-{index:02d}",
                "planted": ["deterministic.link_display_mismatch"],
                "intent": "credential phishing",
                "note": "campaign probe: same sender, near-duplicate bodies, per-message token",
                "mime": mime,
                "request": {
                    "direction": "Inbound",
                    "mailFrom": "billing@accounts-payments.test",
                    "rcptTo": ["operator@example.test"],
                    "connectingIp": "198.51.100.9",
                    "authenticationResults": [
                        {"mechanism": "spf", "result": "fail", "fromTrustedVerifier": True},
                        {"mechanism": "dkim", "result": "fail", "fromTrustedVerifier": True},
                    ],
                },
                # The durable route only: the assessment-only call spools nothing and would add
                # ledger noise to a measurement about accumulated history.
                "routes": ["submissions"],
            }
        )
    return out


def cases(suite: str = "core", repeat_count: int = 8) -> list[dict]:
    if suite == "core":
        return cases_core()
    if suite == "repeat":
        return cases_repeat(repeat_count)
    if suite == "quarantine":
        return cases_quarantine()
    if suite == "quarantine2":
        return cases_quarantine_stage2()
    if suite == "max":
        # The ceiling probe on its own, so answering it costs one message rather than a whole
        # suite: whether any weighted-dimension combination reaches the quarantine threshold.
        return [case for case in hard_cases() if case["name"] == "hard-everything-weighted"]
    if suite == "hold":
        # The one message measured to cross the hold threshold with the credential-free local
        # provider, on its own so the join from its queue row to its decision can be checked.
        return [case for case in hard_cases() if case["name"] == "hard-sensitive-data-request"]
    return cases_core() + hard_cases()


def cases_core() -> list[dict]:
    """One message per planted fact, plus the controls that separate the layers."""
    pdf_bytes = b"%PDF-1.4\r\nnot really a pdf\r\n"

    return [
        {
            "name": "benign-plain",
            "planted": [],
            "intent": "ordinary correspondence",
            "note": "the control: a plain message carrying nothing planted",
            "mime": _message(
                subject="Lunch on Thursday",
                sender="Alice Example <alice@example.test>",
                body=b"Are we still on for lunch on Thursday?\r\n",
            ),
            "request": {},
        },
        {
            "name": "link-display-mismatch",
            "planted": ["deterministic.link_display_mismatch"],
            "intent": "credential phishing",
            "note": "anchor text says accounts.example.test, href says 198.51.100.9",
            "mime": _message(
                subject="Urgent: verify your account",
                sender="Accounts <security@example.test>",
                body=_MISMATCH_LINK.encode("utf-8"),
                content_type='text/html; charset="utf-8"',
            ),
            "request": {},
        },
        {
            "name": "link-idn-homograph",
            "planted": ["deterministic.link_idn", "deterministic.link_idn_homograph"],
            "intent": "credential phishing",
            "note": "the href host is a Cyrillic-a confusable of the label's ASCII host",
            "mime": _message(
                subject="Your invoice is ready",
                sender="Billing <billing@example.test>",
                body=_HOMOGRAPH_LINK.encode("utf-8"),
                content_type='text/html; charset="utf-8"',
            ),
            "request": {},
        },
        {
            "name": "display-name-address-mismatch",
            "planted": ["deterministic.display_name_address_mismatch"],
            "intent": "ordinary correspondence",
            "note": "the display name is itself an address that is not the From address",
            "mime": _message(
                subject="Re: your request",
                sender='"alice@example.test" <bob@example.test>',
                body=b"Following up on your request.\r\n",
            ),
            "request": {},
        },
        {
            "name": "html-text-disagreement",
            "planted": ["deterministic.html_text_disagreement"],
            "intent": "credential phishing",
            "note": "the text part and the HTML part share no tokens",
            "mime": _message(
                subject="Notice",
                sender="Notice <notice@example.test>",
                body=_alternative(
                    "b0undary",
                    "Your parcel was delivered on Tuesday and signed for at the door.",
                    _MISMATCH_LINK,
                ),
                content_type='multipart/alternative; boundary="b0undary"',
            ),
            "request": {},
        },
        {
            "name": "attachment-type-mismatch",
            "planted": ["deterministic.attachment_type_mismatch"],
            "intent": "bulk unsolicited",
            "note": "declared application/pdf, file name says .exe",
            "mime": _message(
                subject="Invoice attached",
                sender="Billing <billing@example.test>",
                body=(
                    b'--b1\r\nContent-Type: text/plain; charset="utf-8"\r\n\r\n'
                    b"Please find your invoice attached.\r\n"
                    b'--b1\r\nContent-Type: application/pdf; name="invoice.exe"\r\n'
                    b"Content-Transfer-Encoding: base64\r\n"
                    b'Content-Disposition: attachment; filename="invoice.exe"\r\n\r\n'
                    + base64.encodebytes(pdf_bytes)
                    + b"--b1--\r\n"
                ),
                content_type='multipart/mixed; boundary="b1"',
            ),
            "request": {},
        },
        {
            "name": "auth-trusted-failure",
            "planted": ["deterministic.trusted_authentication_failure"],
            "intent": "bulk unsolicited",
            "note": "a failing DMARC result from a trusted boundary",
            "mime": _message(
                subject="Your statement",
                sender="Bank <alerts@example.test>",
                body=b"Your statement is attached.\r\n",
            ),
            "request": {
                "authenticationResults": [
                    {"mechanism": "dmarc", "result": "fail", "fromTrustedVerifier": True}
                ]
            },
        },
        {
            "name": "auth-untrusted-failure",
            "planted": [],
            "intent": "ordinary correspondence",
            "note": "the same failure, self-asserted: nothing trusts the verifier, so nothing "
            "deterministic may be claimed from it",
            "mime": _message(
                subject="Your statement",
                sender="Bank <alerts@example.test>",
                body=b"Your statement is attached.\r\n",
            ),
            "request": {
                "authenticationResults": [
                    {"mechanism": "dmarc", "result": "fail", "fromTrustedVerifier": False}
                ]
            },
        },
        {
            "name": "envelope-no-recipients",
            "planted": ["envelope.no_recipients"],
            "intent": "ordinary correspondence",
            "note": "an envelope violation, which is established rather than inferred",
            "mime": _message(
                subject="Lunch on Thursday",
                sender="Alice Example <alice@example.test>",
                body=b"Are we still on for lunch on Thursday?\r\n",
            ),
            "request": {"rcptTo": []},
        },
        {
            "name": "envelope-null-sender-outbound",
            "planted": ["envelope.null_sender_not_permitted"],
            "intent": "ordinary correspondence",
            "note": "a null sender on the outbound path, refused regardless of any approved list",
            "mime": _message(
                subject="Bounce",
                sender="Mailer Daemon <mailer-daemon@example.test>",
                body=b"Delivery failed.\r\n",
            ),
            "request": {"direction": "Outbound", "mailFrom": ""},
        },
    ]


# ---------------------------------------------------------------------------------------------
# Host lifecycle
# ---------------------------------------------------------------------------------------------


def free_port() -> int:
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def port_in_use(port: int) -> bool:
    with socket.socket() as probe:
        probe.settimeout(0.5)
        return probe.connect_ex(("127.0.0.1", port)) == 0


def start_host(mode: str, port: int, run_dir: Path) -> subprocess.Popen:
    """Start a throwaway Host, wired as the console harness wires one.

    Every value here is generated locally, stored under the run directory, and never printed.
    """
    if port_in_use(port):
        die(f"Port {port} is already in use, so this run would talk to whatever is listening "
            f"there rather than to its own Host. Set --port to a free one.")

    if not HOST_BINARY.is_file():
        die(f"No Host binary at {HOST_BINARY.relative_to(REPO)}.\n"
            f"Build it first, from the repository root, by relative path:\n"
            f"    cd {REPO} && dotnet build StyloMail.slnx --nologo")

    data = run_dir / "data"
    (data / "spool").mkdir(parents=True, exist_ok=True)

    env = dict(os.environ)
    for stale in ("TYPESAFE_API_KEY", "STYLOMAIL_PROFILE_KEY", "StyloMail__Jev__Endpoint"):
        env.pop(stale, None)

    # Generated per run. Written to the run directory for the Host to read; never echoed.
    env["STYLOMAIL_PROFILE_KEY"] = secrets.token_hex(32)
    principal_key = secrets.token_hex(24)

    if mode == "jev":
        # The console harness's own placeholder and its unreachable endpoint. Not a credential:
        # nothing here reaches the hosted provider, which is the state being measured.
        env["TYPESAFE_API_KEY"] = "not-a-real-key-measurement-only"
        env["StyloMail__Jev__Endpoint"] = "http://127.0.0.1:9/v1/systemone"
    else:
        env["StyloMail__Assessment__Provider"] = "Nimble"

    # Deliberately NOT set: StyloMail__Transport__Upstream__Host. With no upstream the delivery
    # worker builds no port and never runs, so this instrument opens no socket of its own.
    env.pop("StyloMail__Transport__Upstream__Host", None)

    env["ASPNETCORE_URLS"] = f"http://127.0.0.1:{port}"
    env["StyloMail__Storage__SpoolRoot"] = str(data / "spool")
    env["StyloMail__Storage__DatabasePath"] = str(data / "host.db")
    env["StyloMail__Auth__Principals__0__PrincipalId"] = "corpus"
    env["StyloMail__Auth__Principals__0__TenantId"] = "corpus"
    env["StyloMail__Auth__Principals__0__Key"] = principal_key
    for index, privilege in enumerate(["Assess", "Send", "Review", "Feedback", "Administer"]):
        env[f"StyloMail__Auth__Principals__0__Privileges__{index}"] = privilege

    log = (run_dir / "host.log").open("wb")
    process = subprocess.Popen(
        [str(HOST_BINARY), "serve"], cwd=str(REPO), env=env, stdout=log, stderr=subprocess.STDOUT
    )

    base = f"http://127.0.0.1:{port}"
    for _ in range(90):
        if process.poll() is not None:
            die(f"The Host exited before becoming live. Last lines of {log.name}:\n"
                f"{tail(run_dir / 'host.log')}")
        try:
            get(f"{base}/health/live", None)
            return process, base, principal_key
        except OSError:
            time.sleep(1)

    process.kill()
    die(f"The Host did not become live within 90s. Last lines of {log.name}:\n"
        f"{tail(run_dir / 'host.log')}")


def tail(path: Path, lines: int = 20) -> str:
    try:
        return "\n".join(path.read_text(errors="replace").splitlines()[-lines:])
    except OSError:
        return "(no log)"


def die(message: str) -> None:
    print(f"REFUSING: {message}", file=sys.stderr)
    raise SystemExit(2)


# ---------------------------------------------------------------------------------------------
# HTTP
# ---------------------------------------------------------------------------------------------


def request(method: str, url: str, key: str | None, body: dict | None, headers: dict | None = None):
    """Returns (status, parsed-or-raw). Never raises for an HTTP error status."""
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    if key:
        req.add_header("X-StyloMail-Key", key)
    for name, value in (headers or {}).items():
        req.add_header(name, value)
    try:
        with urllib.request.urlopen(req, timeout=60) as response:
            raw = response.read().decode("utf-8", errors="replace")
            status = response.status
    except urllib.error.HTTPError as error:
        raw = error.read().decode("utf-8", errors="replace")
        status = error.code
    try:
        return status, json.loads(raw)
    except json.JSONDecodeError:
        return status, raw


def get(url: str, key: str | None):
    return request("GET", url, key, None)


def newest_decision_id(base: str, key: str) -> str | None:
    status, body = get(f"{base}/v1/decisions?limit=1", key)
    if status != 200 or not isinstance(body, dict):
        return None
    rows = body.get("decisions") or []
    return rows[0].get("assessmentId") if rows else None


def fetch_decision(base: str, key: str, decision_id: str) -> dict:
    status, body = get(f"{base}/v1/decisions/{decision_id}", key)
    return body if isinstance(body, dict) else {"status": status, "body": str(body)[:200]}


# ---------------------------------------------------------------------------------------------
# The measurement
# ---------------------------------------------------------------------------------------------


def summarise(name: str, status: int, body: dict) -> dict:
    """What a decision says, reduced to the fields the four questions turn on."""
    if not isinstance(body, dict):
        return {"case": name, "status": status, "body": str(body)[:200]}

    reasons = [reason.get("code") for reason in body.get("reasons", [])]
    evidence = body.get("evidence", [])

    def rows(origin: str, availability: str) -> list[str]:
        return sorted(
            {
                row.get("signalId")
                for row in evidence
                if row.get("origin") == origin and row.get("availability") == availability
            }
        )

    def values(origin: str) -> dict:
        """Every measured row of one origin, by signal id, with the value it carried.

        Measured is not the same as flagged, and this instrument must not conflate them:
        `deterministic.display_name_address_mismatch` is reported Available with a value of 0.0 on
        a message that has no such mismatch, and calling that a finding would make the oracle
        report coverage it does not have.
        """
        return {
            row["signalId"]: row.get("value")
            for row in evidence
            if row.get("origin") == origin and row.get("availability") == "Available"
        }

    deterministic = values("Deterministic")
    return {
        "case": name,
        "status": status,
        "assessmentId": body.get("assessmentId"),
        "action": body.get("action"),
        "riskIndex": body.get("riskIndex"),
        "reasons": reasons,
        "mimeCoverage": body.get("coverage"),
        "deterministicAvailable": sorted(deterministic),
        "deterministicFindings": sorted(
            signal for signal, value in deterministic.items() if value not in (None, 0, 0.0)
        ),
        "deterministicUnavailable": rows("Deterministic", "Unavailable"),
        "semanticAvailable": rows("Semantic", "Available"),
        "semanticUnavailable": len(rows("Semantic", "Unavailable")),
        "semanticValues": values("Semantic"),
        "behaviouralAvailable": rows("Behavioural", "Available"),
        "recipients": [
            {"recipient": r.get("recipient"), "state": r.get("deliveryState")}
            for r in body.get("recipients", [])
        ],
    }


def run(mode: str, suite: str, port: int, run_dir: Path, repeat_count: int = 8) -> dict:
    if run_dir.exists():
        shutil.rmtree(run_dir)
    run_dir.mkdir(parents=True)

    result: dict = {
        "mode": mode,
        "suite": suite,
        "repo": str(REPO),
        "hostBinary": str(HOST_BINARY.relative_to(REPO)),
        "port": port,
        # Asserted, not assumed: this instrument opens no socket of its own.
        "transportUpstreamConfigured": "StyloMail__Transport__Upstream__Host" in os.environ,
        "cases": {},
    }

    process, base, key = start_host(mode, port, run_dir)
    result["providerComposed"] = None
    try:
        _, ready = get(f"{base}/health/ready", key)
        result["readiness"] = ready

        for case in cases(suite, repeat_count):
            name = case["name"]
            payload = {
                "direction": case["request"].get("direction", "Inbound"),
                "mailFrom": case["request"].get("mailFrom", "alice@example.test"),
                "rcptTo": case["request"].get("rcptTo", ["operator@example.test"]),
                "rawMime": base64.b64encode(case["mime"]).decode("ascii"),
                "connectingIp": case["request"].get("connectingIp", "198.51.100.9"),
                "authenticationResults": case["request"].get("authenticationResults", []),
            }

            # The durable route FIRST, and this ordering decides the result rather than being tidy.
            #
            # POST /v1/assessments is assessment-only: it hands the pipeline an ephemeral payload
            # reference, so `MailAssessor` step 2 finds no original bytes, records
            # `assessment.deterministic_extraction` as Unavailable, and never runs the MIME adapter
            # at all. The planted facts therefore cannot appear in the ledger through that route,
            # whatever the message contains. Measured, not assumed: the first run of this script
            # reduced every case to that one row.
            #
            # POST /v1/submissions spools the payload before assessing, so it is the only route
            # through which the deterministic layer is exercised. Its response carries no decision,
            # so the decision is read back from the ledger, newest first.
            submit_status, submit_body = request(
                "POST", f"{base}/v1/submissions", key, payload, {"Idempotency-Key": f"measure-{name}"}
            )
            durable_id = newest_decision_id(base, key)
            durable = fetch_decision(base, key, durable_id) if durable_id else {}

            # The join the console needs to draw a message row and its decision in one pane. Both
            # sides carry internalMessageId: the queue item (SubmissionStatusResponse) and the
            # ledger row. Read both and compare them rather than reasoning that they must agree.
            queue_id = submit_body.get("queueId") if isinstance(submit_body, dict) else None
            queue_item = None
            if queue_id:
                item_status, queue_item = get(f"{base}/v1/submissions/{queue_id}", key)
                if item_status != 200:
                    queue_item = {"status": item_status, "body": str(queue_item)[:200]}
            durable_internal = durable.get("internalMessageId") if isinstance(durable, dict) else None
            queue_internal = queue_item.get("internalMessageId") if isinstance(queue_item, dict) else None
            join = (
                "not-attempted"
                if queue_id is None
                else "ok"
                if durable_internal and durable_internal == queue_internal
                else "MISMATCH"
            )

            # Some suites are about accumulated history rather than about the two routes, and the
            # assessment-only call spools nothing. Sending it anyway would put a second decision per
            # message in the ledger and make a sequence about campaign history unreadable, so a case
            # can declare which routes it wants. Both routes remain the default.
            status, body = None, {}
            if "assessments" in case.get("routes", ("submissions", "assessments")):
                status, body = request("POST", f"{base}/v1/assessments", key, payload)
                # The ledger is newest-first, so the decision the assessment-only call just made
                # must now be the newest one. If it is not, the two routes have been told apart by
                # guesswork and the measurement says so rather than attributing evidence to the
                # wrong message.
                attribution = (
                    "ok"
                    if (isinstance(body, dict) and body.get("assessmentId") == newest_decision_id(base, key))
                    else "check"
                )
            else:
                attribution = "skipped: case declared the durable route only"

            observed = {
                "planted": case["planted"],
                "intent": case["intent"],
                "note": case["note"],
                "attribution": attribution,
                "submission": {
                    "status": submit_status,
                    "body": submit_body if isinstance(submit_body, dict) else str(submit_body)[:400],
                },
                "queueItem": queue_item,
                "join": join,
                "durableRoute": summarise(f"{name}/submissions", 200, durable),
                "assessmentRoute": summarise(f"{name}/assessments", status, body),
            }
            for label, payload_json in (("durable", durable), ("assessment", body)):
                if isinstance(payload_json, dict) and payload_json.get("assessmentId"):
                    (run_dir / f"{name}.{label}.decision.json").write_text(
                        json.dumps(payload_json, indent=2), encoding="utf-8"
                    )
            result["cases"][name] = observed

        # Question 3, continued: what the console's own listings then show.
        listings = {}
        for state in QUEUE_STATES:
            status, body = get(f"{base}/v1/messages?state={state}", key)
            count = len(body.get("messages", [])) if isinstance(body, dict) else None
            listings[state] = {"status": status, "count": count, "body": body}
        result["listings"] = listings

        status, body = get(f"{base}/v1/decisions?limit=50", key)
        result["ledger"] = {
            "status": status,
            "count": len(body.get("decisions", [])) if isinstance(body, dict) else None,
            "actions": sorted(
                {d.get("action") for d in body.get("decisions", [])} if isinstance(body, dict) else []
            ),
        }

        # The release surface, probed with an identifier that names nothing. A quarantine that no
        # run can reach is exactly why this route's success path is untested; its refusal is what
        # is reachable, and recording that is the point rather than a substitute for a real row.
        status, body = request("POST", f"{base}/v1/quarantine/quarantine_does_not_exist/release", key, {})
        result["releaseProbe"] = {"status": status, "body": body}

        # The success path, which the console covers only by unit tests because no run reached a
        # quarantined row. Reached now by traffic, so it can be exercised against a real row on a
        # throwaway Host. Before, release, after: three reads so the change is observed rather than
        # assumed, and idempotency is checked by releasing a second time.
        released = None
        quarantined = [
            row.get("queueId")
            for row in (listings.get("quarantined", {}).get("body") or {}).get("messages", [])
            if isinstance(row, dict) and row.get("queueId")
        ]
        if quarantined:
            target = quarantined[0]
            _, before = get(f"{base}/v1/submissions/{target}", key)
            release_status, release_body = request(
                "POST", f"{base}/v1/quarantine/{target}/release", key, {}
            )
            _, after = get(f"{base}/v1/submissions/{target}", key)
            repeat_status, repeat_body = request(
                "POST", f"{base}/v1/quarantine/{target}/release", key, {}
            )
            released = {
                "queueId": target,
                "stateBefore": (before or {}).get("state"),
                "releaseStatus": release_status,
                "releaseBody": release_body,
                "stateAfter": (after or {}).get("state"),
                "recipientStateAfter": [
                    r.get("state") for r in ((after or {}).get("recipients") or []) if isinstance(r, dict)
                ],
                "secondReleaseStatus": repeat_status,
                "secondReleaseBody": repeat_body,
            }
        result["releaseOfQuarantine"] = released

        return result
    finally:
        process.terminate()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()


def report(result: dict) -> None:
    print(f"mode: {result['mode']}")
    print(f"transport upstream configured: {result['transportUpstreamConfigured']}  (false = no socket opened)")
    print()
    for route in ("durableRoute", "assessmentRoute"):
        print(f"-- {route} (action / index / reasons)")
        for name, row in result["cases"].items():
            decision = row[route]
            print(
                f"  {name:<34} {str(decision.get('action')):<11} "
                f"{fmt(decision.get('riskIndex')):>5}  {','.join(decision.get('reasons') or [])}"
            )
        print()
    print(f"-- submissions route, HTTP status per case")
    for name, row in result["cases"].items():
        print(f"  {name:<34} {row['submission']['status']:>4}  {json.dumps(row['submission']['body'])[:110]}")
    print()
    print("-- planted deterministic facts versus what the durable route reported")
    for name, row in result["cases"].items():
        planted = [f for f in (row.get("planted") or []) if not f.startswith("envelope.")]
        found = row["durableRoute"].get("deterministicFindings") or []
        missing = [fact for fact in planted if fact not in found]
        print(f"  {name:<34} planted={planted or '[]'}")
        print(f"  {'':<34} reported={found or '[]'}{'  MISSING=' + str(missing) if missing else ''}")
    print()
    print(f"-- attribution self-check: {sorted({r['attribution'] for r in result['cases'].values()})}")
    for state in QUEUE_STATES:
        row = result["listings"][state]
        print(f"GET /v1/messages?state={state:<18} -> {row['status']}, {row['count']} row(s)")
    print(f"GET /v1/decisions          -> {result['ledger']['status']}, "
          f"{result['ledger']['count']} row(s), actions {result['ledger']['actions']}")
    print(f"POST /v1/quarantine/{{id}}/release -> {result['releaseProbe']['status']} (id names nothing)")
    released = result.get("releaseOfQuarantine")
    if released:
        print()
        print(f"-- release of a real quarantined row: {released['queueId']}")
        print(f"   state before: {released['stateBefore']}")
        print(f"   POST /v1/quarantine/{{id}}/release -> {released['releaseStatus']} {released['releaseBody']}")
        print(f"   state after : {released['stateAfter']}  recipient states {released['recipientStateAfter']}")
        print(f"   released twice (idempotency) -> {released['secondReleaseStatus']} {released['secondReleaseBody']}")


def fmt(value) -> str:
    return "-" if value is None else f"{value:0.3f}".rstrip("0").rstrip(".")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=["jev", "nimble"], required=True)
    parser.add_argument("--suite", choices=["core", "hard", "max", "hold", "repeat", "quarantine", "quarantine2"], default="core")
    parser.add_argument("--repeat-count", type=int, default=8, help="messages in the repeat suite")
    parser.add_argument("--out", required=True, help="where the JSON result is written")
    parser.add_argument("--port", type=int, default=0, help="0 picks a free port")
    parser.add_argument("--run-dir", default=".styloagent/scratch/corpus/measure")
    args = parser.parse_args()

    run_dir = (REPO / args.run_dir) if not Path(args.run_dir).is_absolute() else Path(args.run_dir)
    result = run(args.mode, args.suite, args.port or free_port(), run_dir, args.repeat_count)
    report(result)

    out = Path(args.out)
    if not out.is_absolute():
        out = REPO / out
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(f"\nwrote {out.relative_to(REPO)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
