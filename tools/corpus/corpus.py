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

    python3 tools/corpus/corpus.py generate --seed 1234 --count 20 --out scratch/batch --profile mixed
    python3 tools/corpus/corpus.py seed  --base-url http://127.0.0.1:5271 --key-file scratch/batch/principal.key --batch scratch/batch
    python3 tools/corpus/corpus.py check --base-url http://127.0.0.1:5271 --key-file scratch/batch/principal.key --manifest scratch/batch/manifest.json

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
import email
import hashlib
import io
import json
import os
import quopri
import re
import sys
import urllib.error
import urllib.request
import zipfile
from dataclasses import dataclass, field, replace
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
# 4 (1 Oct 2026): the manifest records the DRAWN SHAPE of each message, per the approved desktop-harness
# design rev 2 (`overview-` 07:56, operator approval 18:53). The first such field is `encoding`, drawn
# from `--encoding-mix`; the size mix and the seed-recorded decision fields follow under the same
# number. The number moves rather than staying because a version 3 consumer that switched on a shape
# field would read a different thing under the same number: `encoding` is present on every message
# here and absent from every version 3 message, so "absent" cannot be left to mean "plain".
CORPUS_VERSION = 4

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
    "campaign.near_duplicate": {
        "where": "batch",
        "site": (
            "the decision's evidence list, for a message whose wording and security-bearing "
            "details both agree with a recent message's: the same comparison as the variant, "
            "answering the other way"
        ),
    },
    "campaign.security_bearing_variant": {
        # A third `where`, and it is not decoration. Every other fact this corpus plants is a
        # property of ONE message: `mime` says the bytes carry it, `submission` says the request
        # carries it. This one is carried by neither, because the same bytes report differently
        # depending on what preceded them in the batch (`RecentCampaignWindow`, compared against
        # the deployment's recent messages, `CampaignNearDuplicateDetector.cs`). A consumer that
        # read this as `mime` would look for the change in the message and find nothing to look at.
        "where": "batch",
        "site": (
            "the decision's evidence list, for a message whose wording reuses a recent message's "
            "while its security-bearing details do not: the destination is in THIS message's body, "
            "but the comparison that makes it a finding is against the batch before it"
        ),
        "constraint": (
            "BEHAVIOURAL, NOT DETERMINISTIC, AND BATCH-DEPENDENT. The fingerprint component that "
            "carries the change is `payment.identifier`, extracted deterministically from the body "
            "(`SecurityBearingFingerprint.cs:100`), but the SIGNAL is a comparison against recent "
            "messages, so the same bytes report differently at different positions in a batch. "
            "Measured 1 Oct, seed 77, count 4: the signal fires on both turn 2s (changed "
            "destination, reused wording) and on the SECOND turn 1 (index 2), which has a changed "
            "turn 2 behind it, and not on index 0, which has nothing behind it. Asserted only on "
            "the indices where it was measured true, so this is a claim about a MESSAGE IN A BATCH "
            "and not about a message alone."
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
# Shape axes
# ---------------------------------------------------------------------------------------------
#
# An axis is a property of the MESSAGE, drawn from the seed rather than chosen per profile, and it is
# recorded in the manifest so a consumer can select a fixture by the shape it claims. The values here
# are the DRAWN ones; `mixed` is not a value but a rule for drawing, which is why it is a separate
# constant from the set a message can carry.

# How the leaf bodies are transferred, and whether the subject needs an encoded-word. Each value
# changes WHICH BYTES carry a site, never WHETHER the site exists: every variant decodes back to the
# same text, which is the constraint the design puts on this axis (§3.4). A variant that dropped or
# reworded a planted fact would make the manifest describe a message the extractor never saw.
ENCODING_MIXES = ("plain", "quoted-printable", "rfc2047")
ENCODING_MIX_CHOICES = ENCODING_MIXES + ("mixed",)

# The non-ASCII markers the `rfc2047` variant appends to a subject. A marker is needed rather than
# merely decorative encoding: wrapping an ASCII subject in `=?utf-8?...?=` is legal and proves nothing,
# because nothing forced the encoder to run. Measured in the lane's harness by asserting the decoded
# subject IS non-ASCII, which a no-op implementation would fail.
RFC2047_MARKERS = ("Réunion", "Zürich", "København", "señor")


def draw_encoding(seed: int, index: int, mix: str) -> str:
    """The encoding one message carries. `mixed` draws; anything else is taken literally."""
    if mix == "mixed":
        return pick(seed, index, "encoding", list(ENCODING_MIXES))
    return mix


# How large a message is, measured as the size of the parts that are NOT the model's turn.
#
# The target is a number of BYTES for each carrier part, and it is a TARGET, not a claim about the
# Host: whether a given size trips the parser's `truncated` or `parser_limit_exceeded` flag is the
# Host's threshold and is DERIVED in the design (section 3.1), to be measured in a window before any
# harness asserts on it. What this axis promises is narrower and checkable: the message is this big,
# and its turn is not.
SIZE_MIXES = ("small", "medium", "large")
SIZE_MIX_CHOICES = SIZE_MIXES + ("mixed",)
SIZE_TARGETS = {"small": 0, "medium": 32 * 1024, "large": 256 * 1024}

# THE ADAPTER'S OWN BODY BUDGET, and the value is now a measurement rather than a guess. An earlier
# version of this comment said the adapter truncates a turn at 2,000 characters. MEASURED, it does not:
# the classifier's body budget is `NimbleOptions.MaxBodyCharacters` **2500** (NimbleOptions.cs:229); the
# parser's per-body limit is `MimeParseLimits.MaxBodyChars` 2 MiB (MimeParseLimits.cs:46); and the ONLY
# 2,000 in the measurement path is `Truncate(m, 2_000)` over `message.ConversationContext`
# (NimbleMessageState.cs:155), which bounds a WINDOW entry and not the body.
#
# SO 2000 WAS THIS LANE'S OWN MARGIN AND NOT THE ADAPTER'S BOUND, and it was wrong in the direction
# that costs: it made every dense fixture 500 characters SHORTER than the adapter allows while the
# expansion FALLS with body size, so it made the family's arms weaker than they had to be. `nimble-`
# then measured the arm at the real budget -- a 2500-character hexish body evaluates **43202** tokens,
# 6.252x, the largest adapter-producible evaluation anyone has measured -- and the pin at 65536 clears
# it by about 1.52x.
#
# `>` AND NOT `>=`, because 2500 is the budget ITSELF: a body exactly at the budget is not over it. A
# turn ABOVE this is one the fit must shorten, and a shortened body is not the fixture the manifest
# describes.
#
# The README has said since the size axis landed that "`check` asserts this directly: no message in a
# batch may have `turnCharacters >= 2000`", and it did not: `turnCharacters` appeared in this file only
# as a manifest FIELD, with no comparison against 2000 anywhere. The claim is a good one and it is now
# true rather than removed.
#
# THE "OWED ASSERTION" IS CLOSED AS UNREACHABLE, and the reason is measured rather than assumed. The
# bound that is REAL is PER WINDOW ENTRY: the adapter truncates each context entry at 2,000 characters
# (`NimbleMessageState.cs:155`) while `windowCharacters` here is a SUM, so the assertion I said was owed
# could not be written against the manifest as it stands. **But no fixture in this corpus can approach
# that bound either.** A window entry is the PREVIOUS message's raw bytes, and the only profiles that
# carry a window are `pair` and `pair-control`, whose plans have `html=False, attachment=False` -- so the
# size axis refuses anything but `small`/`mixed` for them and `mixed` draws `small` on a text-only
# message. MEASURED: the largest single entry over 40 seeds is **546 characters**, 1,454 under the
# truncation. **So a guard here would be a DEAD GUARD: nothing can make it fire, which means nothing can
# test it and it would only look like cover.**
#
# THE TRIGGER THAT WOULD MAKE IT LIVE, recorded so it is not lost rather than guarded speculatively: if a
# window-bearing profile ever gains an html part or an attachment, its window entries become growable and
# the per-entry bound becomes reachable in one `--size-mix` flag. **The bound should be asserted the day
# that profile exists and not before.**
TURN_LIMIT = 2500

# -------------------------------------------------------------------------------------------------
# The dense body-shape axis: density that SURVIVES DECODING, which is the author's density
# -------------------------------------------------------------------------------------------------
#
# WHY THIS EXISTS. `nimble-` measured six body shapes with everything else held and found the prompt
# expansion running from 2.808x to 7.017x, with the message's CONTENT as the only variable. Two
# consequences the fleet took from that measurement: a corpus's safety is a property of the CORPUS and
# not of the window setting alone, and a fixture set that is all prose cannot assess the setting at
# all. This axis makes the property declarable, so a batch says which shapes it drew rather than
# leaving a consumer to guess density from the bytes.
#
# WHERE THE DENSITY IS, AND WHERE IT IS NOT. `conversation-` read the state at source: an attachment
# enters it as five METADATA fields and never as bytes, and a transfer encoding is DECODED before the
# state is built. So this axis can only be the author's text in `BodyText`, and it is orthogonal both
# to `--size-mix` (which rides the html part and the attachment) and to `--encoding-mix` (which is
# gone before the classifier sees anything). That orthogonality is the reason the two other axes
# cannot make any shape here redundant, and it is why the transform is applied to the plan's own text.
BODY_SHAPES = ("prose", "base64ish", "mixed", "randomcase", "punct", "hexish")

# `off` is the default and it changes NO existing invocation: this is a new axis rather than a new
# profile because it composes with a profile instead of replacing one, and `off` is what makes that
# safe for every command line that already exists. `all` draws each shape in turn rather than sampling
# at random, so a six-message batch contains the whole table and the spread is visible in one artefact;
# a named shape forces it, which is what makes a single-shape run possible without a second flag.
BODY_SHAPE_MIX_CHOICES = ("off", "all") + BODY_SHAPES

# The unit strings are `nimble-`'s, taken verbatim from `.styloagent/scratch/nimble/probe-expansion.py`
# and NOT re-authored here. A fixture that rebuilt their table with units of my own would be a second
# table rather than a reproduction of theirs, and the fleet's only interest in this family is that the
# measured spread transfers to a corpus that ships. The tiling is theirs too; the budget is mine and
# it is characters rather than bytes, because `turnCharacters` is `len(plan.text)`.
SHAPE_UNITS = {
    "prose": "The quarterly figures are attached and the invoice is due at the end of the month. ",
    "punct": "a,b.c;d:e!f?g-h_i+j=k/l\\m(n)o[p]q{r}s<t>u=v&w%x$y#z@1,2.3;4:5!6?7-8_9+0/ ",
    "randomcase": "Qm7xK2pLv9Bn4Rtz8Ws3Yd6Fg1Hj5Mc0Ue2AoTi7Ol9Pk3Vb6Nw8Xs4Zr2Td5Yf1Gh7Jk3Lm9 ",
    "hexish": "9f2ab7c4e10d38a6b5c9e2f7a4d1b8c3e6f0a9d2b7c4e1f8a5d3b9c6e0f2a7d4b1 ",
    "base64ish": "TWFuIGlzIGRpc3Rpbmd1aXNoZWQgbm90IG9ubHkgYnkgYWR2YW50YWdlcyBidXQgYnkgdGhlaXIgYWJzZW5jZQ==",
    "mixed": "Re: invoice INV-2026-0917 (ref: a,b.c;d) <https://x.example/p?a=1&b=2> 12,345.67 USD ",
}

# THE DEFAULT BODY LENGTH IS *NOT* THE ADAPTER'S BUDGET, and the measurement is why. A run showed the
# fit's cut is a SIZE rule: through the shipping path at the pinned 65536, a 2500-character body is
# shortened (`the client shortened the message body to fit the context window`) and a ~450-character one
# is not. Two further arms bracket where it begins, same instrument, same window, three messages each:
#
#     1500 characters   0 of 3 shortened
#     2000 characters   0 of 3 shortened
#     2500 characters   6 of 6 shortened
#
# So the default is 2000, the LARGEST length MEASURED to arrive un-cut, rather than 2500 which is measured
# to be cut. **The bracket is (2000, 2500) and the threshold inside it is NOT measured**, so this is a
# safe default and not a ceiling: a state with more links, attachments or envelope than the benign
# full-coverage one measured here carries more overhead and can cut sooner.
#
# `TURN_LIMIT` stays at the adapter's budget and keeps its own job: `check` refuses anything ABOVE it,
# because a body over the adapter's budget is one the adapter must shorten for a reason this corpus
# cannot see. The two numbers answer different questions and the gap between them is deliberate.
SHAPE_BODY_CHARACTERS = 2000


def draw_body_shape(index: int, mix: str) -> str | None:
    """The shape one message draws. `off` is None, `all` cycles the six, anything else is literal.

    DELIBERATELY NOT SEEDED. `all` cycling by index rather than drawing is the whole point: a random
    draw would sample the range, and a batch that claims to show six shapes has to CONTAIN six shapes
    for the claim to be checkable. Nothing here is hidden from the manifest, so there is no
    reproducibility to preserve by drawing.
    """
    if mix == "off":
        return None
    if mix == "all":
        return BODY_SHAPES[index % len(BODY_SHAPES)]
    return mix


def shape_body(shape: str, characters: int = SHAPE_BODY_CHARACTERS) -> str:
    """Tile the shape's unit to exactly `characters`, which is nimble-'s tiling at a char budget."""
    unit = SHAPE_UNITS[shape]
    return (unit * (characters // len(unit) + 2))[:characters]


# THE LAST CHARACTER IS LOAD-BEARING AND IT IS NOT GUARANTEED BY THE SHAPE. FIVE of the six units in
# `SHAPE_UNITS` END WITH A SPACE, so whether a tiled body trails in whitespace is decided by where the
# slice above lands rather than by what the unit ends with. Swept over 1..3000: `prose` trails at 577
# counts, `mixed` at 282, `hexish`/`punct`/`randomcase` at about 40, `base64ish` never. **The default
# 2000 lands off it for all six, which is why `kept == bodyShapeCharacters` holds on a quoted-tail arm
# today; the alphabets are full of spaces and the count is the only thing standing between that
# assertion and prose at 19 percent.** It matters because `QuotedHistory.Split` returns
# `NewText = body[..cut].TrimEnd()`, so a trailing space makes the ANALYSED body shorter than the
# manifest's declared `bodyShapeCharacters` and an arm comparing the two fails for a reason that has
# nothing to do with the fit. `tests/test-body-shapes.py` asserts the property at the default and
# carries a control at 4 characters, where `prose` is `"The "` and the assertion goes red.


# -------------------------------------------------------------------------------------------------
# The quoted tail: the only fixture this generator can make that raises the QUOTED half of the flag
# -------------------------------------------------------------------------------------------------
#
# WHY. `shortened` is a DISJUNCTION -- `Shortened(BodyText, body) || Shortened(QuotedText, quoted)` --
# so a reading of `flag + kept + total` cannot say WHICH field was cut. The quoted disjunct fires iff
# `QuotedText.Length` exceeds the fit's CURRENT budget, and the budget starts at `MaxBodyCharacters`
# 2500 (`NimbleSemanticMailClassifier.cs:358`) and is reduced a step per pass (`:407`). So:
#
#   a tail of 162  fires only if a fit pass drives the budget under 162   -- CONDITIONAL
#   a tail above 2500 fires UNCONDITIONALLY, on the first pass             -- which is the fixture
#
# AND THE SHAPE IS NOT THE GAP. The committed fixtures at `tests/fixtures/jev/` already include
# `reply-in-thread.eml`, whose quoted tail is 162 characters -- so the corpus HAS a reply and what it
# lacks is one sized above the budget. This option is for that case.
#
# THE MARKER IS THE SPLITTER'S OWN, not a pattern of this corpus's invention: `QuotedHistory.cs:38-39`
# looks for a line beginning `>`, and `:34-35` for an `on ... wrote:` attribution. A tail appended
# without them would be a long BODY and no quoted field at all, which is the failure mode worth the
# comment.
QUOTED_TAIL_ATTRIBUTION = (
    "\n\nOn Mon, 1 Jan 2024 at 09:00, Correspondent <correspondent@" + SENDER_DOMAIN + "> wrote:\n"
)
QUOTED_TAIL_UNIT = "> The earlier message is quoted here so the tail has something to be.\n"


def quoted_tail(characters: int) -> str:
    """The attribution line plus a `>`-quoted section of about `characters`, deterministically.

    No clock: the attribution date is fixed, because this corpus is reproducible from `(seed, index)`
    and a rendering time would make the same seed produce different bytes on two runs.
    """
    repetitions = max(1, characters // len(QUOTED_TAIL_UNIT) + 1)
    return QUOTED_TAIL_ATTRIBUTION + QUOTED_TAIL_UNIT * repetitions


def size_carriers_from(html: str | None, facts: list[str]) -> tuple[bool, bool]:
    """(has html, has attachment): the parts that can carry size without touching the model's turn."""
    return html is not None, "deterministic.attachment_type_mismatch" in facts


def size_carriers(plan: MessagePlan) -> tuple[bool, bool]:
    """The same predicate, reached from a plan.

    ONE implementation with two entry points, deliberately: the refusal in `cmd_generate` and the
    growth in `build_mime` must agree, and two copies of the condition would drift into letting
    through exactly the message the refusal was written to stop.
    """
    return size_carriers_from(plan.html, plan.facts)


def draw_size(seed: int, index: int, mix: str, can_carry: bool) -> str:
    """The size one message carries.

    A message with no carrier draws `small` even under `mixed`, rather than drawing a size it cannot
    express and then reporting a failure: the manifest records what the message IS.
    """
    if mix == "mixed":
        return pick(seed, index, "size", list(SIZE_MIXES) if can_carry else ["small"])
    return mix


def pad_html(html: str, size: str) -> str:
    """Grow the html part toward the target WITHOUT adding word tokens.

    The filler is an HTML comment filled with `=`. Two risks are being avoided at once, and both are
    real rather than theoretical: padding with prose would move `deterministic.html_text_disagreement`,
    whose whole question is how many tokens the text and html representations share, and padding with
    a single long run of letters would move it too. A run of `=` contributes essentially nothing under
    any tokenisation that splits on non-word characters.
    """
    target = SIZE_TARGETS[size]
    if target <= len(html.encode("utf-8")):
        return html
    need = target - len(html.encode("utf-8"))
    return html + "<!--" + "=" * max(need - 7, 0) + "-->"


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
    encoding: str = "plain",
    size: str = "small",
) -> bytes:
    """One raw MIME message. The shape follows the facts, never the other way round.

    `encoding` is the drawn shape axis. It is applied to the LEAF BODIES and to the Subject header,
    and to nothing else: the attachment keeps its own base64 transfer encoding, and no variant adds or
    removes a part, so a fact planted in the bytes is carried by every variant.
    """
    headers = [
        f"From: \"{sender_name}\" <{sender_address}>",
        f"To: {RECIPIENT}",
        f"Subject: {_encoded_subject(subject, encoding, seed, index)}",
        "Date: Tue, 30 Sep 2026 09:00:00 +0000",
        f"Message-ID: <{message_id(seed, index)}@{SENDER_DOMAIN}>",
        "MIME-Version: 1.0",
    ]

    want_html, want_attachment = size_carriers_from(html, facts)
    # The html grows here rather than in `_alternative`, so the padding decision is made once and the
    # two containers below cannot disagree about which part carries the bulk.
    if want_html and html is not None:
        html = pad_html(html, size)

    if not want_attachment and not want_html:
        headers.append('Content-Type: text/plain; charset="utf-8"')
        cte, body = _encode_leaf(text, encoding)
        if cte:
            headers.append(cte)
        return ("\r\n".join(headers) + "\r\n\r\n").encode("utf-8") + body

    # The body parts first, then the container, so the boundary is only chosen once the number of
    # parts is known. Declaring multipart/mixed with no parts was a mistake made in measurement and
    # it produced a message that declared link and attachment facts and carried neither.
    if want_html and want_attachment:
        inner = _alternative("alt", text, html, encoding)
        parts = [
            ('Content-Type: multipart/alternative; boundary="alt"', inner),
            (_attachment_headers(), _attachment_body(size)),
        ]
        headers.append('Content-Type: multipart/mixed; boundary="mix"')
        body = _mixed("mix", parts)
    elif want_attachment:
        headers.append('Content-Type: multipart/mixed; boundary="mix"')
        body = _mixed("mix", [(_attachment_headers(), _attachment_body(size))])
    else:
        headers.append('Content-Type: multipart/alternative; boundary="alt"')
        body = _alternative("alt", text, html, encoding)

    return ("\r\n".join(headers) + "\r\n\r\n").encode("utf-8") + body


def _mixed(boundary: str, parts: list[tuple[str, bytes]]) -> bytes:
    out = b""
    for headers, body in parts:
        out += f"--{boundary}\r\n{headers}\r\n\r\n".encode("utf-8") + body + b"\r\n"
    return out + f"--{boundary}--\r\n".encode("utf-8")


def _alternative(boundary: str, text: str, html: str, encoding: str = "plain") -> bytes:
    out = b""
    for content_type, payload in (("text/plain", text), ("text/html", html)):
        cte, body = _encode_leaf(payload, encoding)
        header = f'Content-Type: {content_type}; charset="utf-8"'
        if cte:
            header = f"{header}\r\n{cte}"
        out += f"--{boundary}\r\n{header}\r\n\r\n".encode("utf-8") + body + b"\r\n"
    return out + f"--{boundary}--\r\n".encode("utf-8")


def _encode_leaf(payload: str, encoding: str) -> tuple[str, bytes]:
    """One leaf part's extra header line (empty when there is none) and its transfer-encoded body.

    The invariant this must keep, and the reason it is one function rather than a branch per caller:
    the DECODED bytes are identical across every variant, so a fact planted at a site is carried by
    every variant and the manifest's claim survives the encoding.
    """
    if encoding == "quoted-printable":
        encoded = quopri.encodestring(payload.encode("utf-8"), quotetabs=False)
        # `quopri` emits bare LF. A body in this corpus is CRLF throughout, and a hand-rolled
        # soft-line-break join has to match what the decoder will unfold.
        return "Content-Transfer-Encoding: quoted-printable", encoded.replace(b"\n", b"\r\n")
    return "", payload.encode("utf-8")


def _encoded_subject(subject: str, encoding: str, seed: int, index: int) -> str:
    """The Subject header value for this variant, encoded-word included rather than left to a writer.

    The `rfc2047` variant appends a non-ASCII marker, because an ASCII subject wrapped in `=?utf-8?...?=`
    is legal and proves nothing: nothing forced the encoder to run, so a no-op implementation would
    pass the same check. The encoded-word is emitted whole and unfolded, because RFC 2047 forbids
    splitting one across lines and a folding writer would make the wire form depend on the subject's
    length.
    """
    if encoding != "rfc2047":
        return subject
    marked = f"{subject} [{pick(seed, index, 'rfc2047-marker', list(RFC2047_MARKERS))}]"
    return "=?utf-8?B?" + base64.b64encode(marked.encode("utf-8")).decode("ascii") + "?="


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


def _attachment_body(size: str = "small") -> bytes:
    """The attachment payload, grown toward the size target when one is asked for.

    The attachment is the safest place for bulk: nothing reads it for tokens, so growing it cannot
    move a text-comparison fact the way growing the body or the html can.
    """
    payload = b"This attachment is not a PDF, whatever the filename says.\r\n"
    target = SIZE_TARGETS[size]
    if target:
        filler = b"-" * 76 + b"\r\n"
        while len(payload) < target:
            payload += filler
    return base64.encodebytes(payload)


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
    # Fact ids that must be ABSENT FROM THE EVIDENCE LIST in this message, not present-and-NotApplicable.
    # The two are different observations and only one of them is the claim a negative control makes:
    # a row that is there with `availability: Unavailable` says the pipeline considered the signal and
    # found nothing to say, while a row that is absent says the signal was never emitted at all. The
    # `controls` map above cannot express the second, because it is checked through `expected` and
    # every predicate in that vocabulary describes a ROW. See `expected_predicate`.
    #
    # A list sibling to `facts` rather than a predicate on it, because a planted fact is by definition
    # something this tool put in the message, and a negative control is a claim about the arm rather
    # than a fact that was planted.
    #
    # SCOPE, and it is a real limit rather than a footnote: an absence claim is about the LEDGER, not
    # the message. A signal absent in a fresh run directory can be present when the same batch is
    # seeded into a ledger that already holds a similar message, because the campaign window is
    # populated from what the deployment has already seen. Every driver in this lane starts a fresh
    # run directory, so the claim holds there; a consumer seeding into a live ledger must check it
    # there rather than assume it travels.
    not_planted: list[str] = field(default_factory=list)
    # Which turn of a pair this message is. Null for an unpaired message.
    turn: int | None = None
    # Described but not declared: a change this corpus plants for a lane whose signal id does not
    # exist yet. Recorded honestly as unverifiable rather than given an invented id.
    undescribed_change: dict | None = None
    # Whether `--body-shapes` may replace this plan's text. DEFAULT FALSE, and the default is the
    # guard rather than a formality: the axis rewrites the turn, so it may only run on a plan whose
    # planted facts do NOT live in that text. `phishing` and its siblings carry their needles in the
    # body, so replacing the body would leave the manifest describing a message the extractor never
    # saw. A plan says it is safe, and the axis refuses every plan that has not said so, which is the
    # fail-closed direction: a new plan builder added later is refused until it opts in.
    dense_safe: bool = False

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
        # This plan's facts are the BENIGN_FACTS expected-zeros, which are claims about rows rather
        # than needles in the body, so replacing the body cannot falsify one. Its html interpolates
        # the text, and the axis re-derives the html by substitution rather than dropping it, so the
        # agreeing anchor survives as a real link at the same href.
        dense_safe=True,
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
    as a planted fact, for a temporary reason and a durable one. Practically, no signal id for it
    exists in the tree, and inventing one would put a claim in the manifest that no pipeline could
    report and that `check` would fail a correct pipeline for. Lastingly, a `planted` entry is a
    promise about a **deterministic** property, and the ids this change could surface as today are
    the semantic dimensions: `semantic.payment_redirection` going true, or
    `semantic.conversational_continuity`'s own judgement that the turn does not fit the window. Those
    values are model answers, and the corpus declares continuity as availability only and never
    asserts a value, so **more semantic ids cannot make this plantable**. It becomes plantable the
    day a deterministic finding reports that a turn differs from its supplied window; the ten in
    `src/StyloMail.Policy/DeterministicFindings.cs` do not, and `deterministic.thread_header_consistency`
    is nearest and is about headers rather than the body. The change is real in the bytes (different
    account number); it is simply not assertable, and the manifest says which.
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
            # The rewrite this pair is built on does not reach the campaign variant, at ANY index of
            # this profile, and that is asserted rather than described in prose. Measured first: the
            # only campaign id this profile emits, on any message, is `campaign.near_duplicate`.
            # Without this the finding "a rewrite is not a template reuse" would live only in the
            # README, where nothing checks it.
            not_planted=["campaign.security_bearing_variant"],
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
            # DENSE-SAFE, and the reason is what this profile ASSERTS and not what its bytes contain.
            # Both turns plant the availability of a WINDOW dimension and the ABSENCE of a campaign id;
            # neither is a needle in the body. The body-borne change lives in `undescribedChange`, which
            # is a DESCRIPTION and explicitly not a claim, so replacing the body falsifies nothing
            # `check` verifies -- it only makes the description stale, which the transform handles by
            # dropping it. This is the profile that carries the RICH state, so it is the one that can
            # reach a retained length below 1988.
            dense_safe=True,
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
                "recorded but NOT declared as a planted fact: a planted entry promises a "
                "DETERMINISTIC property, and the ids this change could surface as are semantic "
                "dimensions whose value is the model's own answer, which the corpus never asserts. "
                "Nothing to report here until a deterministic finding describes a turn differing "
                "from its supplied window. The change is real in the bytes; it is not assertable."
            ),
        }
    return MessagePlan(
        index=index,
        intent=INTENTS["transactional"],
        facts=["semantic.conversational_continuity"],
        # Same claim as turn 1, and it holds for the CHANGED turn too: a changed destination on a
        # rewritten message does not reach the campaign variant. See `plan_pair`'s docstring.
        not_planted=["campaign.security_bearing_variant"],
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
        dense_safe=True,
        turn=2,
        undescribed_change=change,
    )


# A reused template with a changed destination: turn 2 is turn 1 with the bank details swapped and
# nothing else, which is the canonical shape `SecurityBearingFingerprint.cs:97-103` names in its own
# comment ("a changed account number is the canonical example of an identical template with a
# different effect").
#
# Why this is a SEPARATE profile from `pair` and not a rewording of it. The two fixtures test
# different layers and only one of them is deterministic:
#
#   `pair`            turn 2 REWRITES turn 1 and supplies it as a conversation window. That is the
#                     conversational axis, and its signal belongs to a lane whose id does not exist.
#   `template-variant` turn 2 REUSES turn 1's wording verbatim and supplies NO window. That is the
#                     campaign axis, whose signal (`campaign.security_bearing_variant`) is already in
#                     the tree and needs no model.
#
# Measured 1 Oct on the committed tree: `pair` reaches `campaign.near_duplicate` only on repeated
# pairs and NEVER reaches `campaign.security_bearing_variant`, so the pair cannot be the fixture that
# exercises the variant. The variant needs wording similarity at or above
# `CampaignDetectionOptions.MinimumSimilarity` (0.85, `CampaignNearDuplicateDetector.cs:57`) and at
# least `MinimumComparedDimensions` (4, `:59`) shared; a rewrite does not meet that, and that is a
# property of the fixture rather than of the pipeline. This profile is the one that meets it: with
# turn 2 reusing turn 1's wording, `campaign.security_bearing_variant` comes back Available with
# value 1, and its control (same wording, destination kept) never produces the row at all.
#
# No conversation window here on purpose. The campaign window is the Host's recent-message store, not
# the request's `conversationContext`, so supplying one would add the conversational axis to a fixture
# meant to isolate the campaign axis, and a difference between the turns could then belong to either.
#
# The account numbers are deliberately DIFFERENT from the `pair` fixture's (which uses 11112222 and
# 12345678). Two fixtures that name the same destination are two fixtures that can match each other
# if a consumer seeds them into one ledger, and a negative control that holds only while no other
# batch is present is a control that fails for a reason its author did not intend.
TEMPLATE_TURN1 = (
    "Dear Accounts Payable, please arrange payment of invoice 4471, which falls due on the "
    "fourteenth. Our bank details are sort code 20-00-00, account number 44339911, held at Example "
    "Bank. Please confirm once the transfer has gone out."
)
TEMPLATE_TURN2_VARIANT = (
    "Dear Accounts Payable, please arrange payment of invoice 4471, which falls due on the "
    "fourteenth. Our bank details are sort code 04-00-04, account number 77889922, held at Example "
    "Bank. Please confirm once the transfer has gone out."
)
# Byte-identical to turn 1 on purpose, spelled out rather than aliased so that an edit to either
# constant cannot silently change the control. The destination NOT changing is the whole control.
TEMPLATE_TURN2_CONTROL = TEMPLATE_TURN1


def plan_template_variant(seed: int, index: int) -> MessagePlan:
    """One turn of a template-reuse pair: the same wording twice with the destination swapped.

    The batch is couplets of (turn 1 with destination X, turn 2 with destination Y), all four
    messages sharing one wording. So index 0 is the only position with no message behind it; every
    index after it either is a swapped turn 2 or is a turn 1 that REVERTS the destination its
    predecessor's turn 2 changed. Either way its predecessor carries a different destination under
    the same wording, which is exactly what the variant signal is defined on.

    So the fact is planted from index 1 onward and asserted ABSENT at index 0. The rule is
    positional, not by parity, and the difference is measured rather than argued: at seed 77, count
    4 the variant is absent at index 0 and Available value 1 at indices 1, **2** and 3. Index 2 is a
    turn 1, not a turn 2: it does not follow its own pair's change, it follows the PREVIOUS pair's
    changed turn 2 and reverts the destination. A manifest that planted the fact on odd indices
    would have left index 2 firing, true and unasserted.
    """
    if index == 0:
        return MessagePlan(
            index=0,
            intent=INTENTS["transactional"],
            facts=[],
            # The position claim, and it is the one that gives `where: batch` its meaning: the same
            # bytes at index 0 report differently from the same bytes at index 2, because the
            # signal is a comparison against what came before rather than a property of the message.
            not_planted=["campaign.security_bearing_variant"],
            subjects=["Invoice 4471 due on the fourteenth"],
            text=TEMPLATE_TURN1,
            html=None,
            sender_name="Supplier Accounts",
            sender_address=f"ap@{SENDER_DOMAIN}",
            auth=_auth_pass(),
            recipients=[RECIPIENT],
            direction="Inbound",
            mail_from=f"ap@{SENDER_DOMAIN}",
            note="first in the batch: nothing precedes it, so the variant must not fire",
            turn=1,
        )
    turn = 2 if index % 2 else 1
    return MessagePlan(
        index=index,
        intent=INTENTS["transactional"],
        facts=["campaign.security_bearing_variant"],
        subjects=["Invoice 4471 due on the fourteenth"],
        text=TEMPLATE_TURN2_VARIANT if turn == 2 else TEMPLATE_TURN1,
        html=None,
        sender_name="Supplier Accounts",
        sender_address=f"ap@{SENDER_DOMAIN}",
        auth=_auth_pass(),
        recipients=[RECIPIENT],
        direction="Inbound",
        mail_from=f"ap@{SENDER_DOMAIN}",
        note=(
            "turn 2 of a template-reuse pair: the same wording with the account number and sort "
            "code swapped, so the wording agrees and the security-bearing detail does not"
            if turn == 2
            else "turn 1 of the second couplet: it reverts the destination the previous turn 2 "
            "changed, under the same wording, so the variant fires here too"
        ),
        turn=turn,
    )


def plan_template_variant_control(seed: int, index: int) -> MessagePlan:
    """The same reuse with the destination left alone: identical wording AND identical details.

    This is the negative half, and it is a separate profile for the same reason `pair-control` is:
    a consumer asking for a control should get one every time rather than half the time.

    It is also the half that keeps the positive honest. A detector that fired
    `campaign.security_bearing_variant` on ANY reused wording would pass the `template-variant`
    claim while being wrong about ordinary repeated mail, and this profile is where that shows up.
    Turn 2 here is turn 1's bytes exactly, so the batch is one template repeated, which is the
    tightest control available: the ONLY thing that differs from the `template-variant` arm is the
    destination.

    The declared fact is `campaign.near_duplicate`, which is checkable and is what a repeated
    template with an UNCHANGED destination is supposed to produce. AND the variant is asserted
    ABSENT on every message here, via `not_planted`. That second half is the whole point of the arm:
    without it, a detector that fired the variant on any reused wording would leave BOTH profiles
    green, because this one asserted nothing about the variant at all. Measured before the claim was
    added: no message in this profile emits the variant, at any index.

    `near_duplicate` is declared from index 1 onward for the same positional reason as the variant
    arm: each message repeats its predecessor's bytes exactly, so it is a duplicate of something, and
    index 0 has nothing behind it. Measured at seed 77, count 4: index 0 reports the row as
    **Unavailable** rather than omitting it, and indices 1, 2 and 3 report Available value 1. Note
    the asymmetry with the variant, which is genuinely ABSENT at index 0 rather than Unavailable:
    that is why the absence claim is `not_planted` on the variant and a plain positional rule for
    the duplicate, and it is the difference the two mechanisms exist to keep apart.
    """
    text = TEMPLATE_TURN2_CONTROL if index % 2 else TEMPLATE_TURN1
    return MessagePlan(
        index=index,
        intent=INTENTS["transactional"],
        facts=["campaign.near_duplicate"] if index >= 1 else [],
        # Absent everywhere here, and measured before it was asserted. Index 0 has nothing behind it
        # and indices 1+ repeat a template without changing anything security-bearing, so no message
        # in this profile has a variant to report.
        not_planted=["campaign.security_bearing_variant"],
        subjects=["Invoice 4471 due on the fourteenth"],
        text=text,
        html=None,
        sender_name="Supplier Accounts",
        sender_address=f"ap@{SENDER_DOMAIN}",
        auth=_auth_pass(),
        recipients=[RECIPIENT],
        direction="Inbound",
        mail_from=f"ap@{SENDER_DOMAIN}",
        note=(
            "control turn 2 of a template-reuse pair: the same wording and the SAME destination, "
            "so this is a duplicate and not a variant"
            if index % 2
            else "control turn 1 of a template-reuse pair: the template as first sent"
        ),
        turn=2 if index % 2 else 1,
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
    "template-variant": [plan_template_variant],
    "template-variant-control": [plan_template_variant_control],
}

# The `mailbox` profile (design section 3.5): a batch whose point is a POPULATED, VARIED ledger.
#
# The composition is positional and stated as data rather than derived, because the two client lists
# see different populations and a reader has to be able to see which positions carry what. The counts
# are TARGETS in the design's sense of the word and are deliberately not promises: decision 27 forbids
# a planted fact from naming a tier, an action or an expected outcome, so this profile supplies
# messages and the run MEASURES what each became. Nothing here, and no field in the manifest, says
# "this message will be Held". A harness selects rows by `seeded.state`.
#
# The targets, per the design's table: 1 quarantine-shaped, 3 risk-shaped (the Hold candidates), 2
# envelope violations (which populate no queue listing, so they are a control on the intake path),
# and 18 benign, so the ledger has an Allow population to render.
#
# KNOWN LIMIT, and it is a consequence of the design's own constraint rather than a defect: the
# envelope-violation builder emits a text-only message, and section 3.2 forbids growing the body, so
# a text-only message has no part that can carry `medium` or `large`. `--profile mailbox --size-mix
# large` is therefore REFUSED at that index rather than faked. `--size-mix mixed` draws `small`
# there and works, which is the recommended pairing for this profile.
MAILBOX_COMPOSITION = (
    (plan_quarantine,)                  # 1, the quarantine target
    + (plan_phishing,) * 3              # 3, the risk targets
    + (plan_envelope_violation,) * 2    # 2, the intake-refusal targets
    + (plan_benign,) * 18               # 18, the Allow population
)
PROFILES["mailbox"] = list(MAILBOX_COMPOSITION)


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

    Four shapes of claim can be a predicate on a ROW, and an integer can express only the first, so
    the predicate is a dict:

      {"value": 1}                          a firing dimension
      {"available": true, "value": 0}       present and NOT firing
      {"available": true, "origin": "..."}  a claim about provenance rather than a value
      {"available": true|false}             whether the signal was computable at all

    A fifth kind of claim is not expressible here at all, because it is a claim that there is NO row:
    `not_planted` on the plan, written as a sibling `notPlanted` list in the manifest. A signal
    present with `availability: Unavailable` says the pipeline considered it and found nothing to
    say; a signal absent from the list says it was never emitted. Those are different observations
    and `{"available": false}` asserts only the first, which is why a negative control needs the
    other mechanism rather than a fifth dict.

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
    if fact_id == "campaign.security_bearing_variant":
        # Firing, and nothing subtler: this fact is declared only on the indices where it was
        # measured to fire, so the predicate does not have to encode the batch position, the
        # declaration does. A turn that carries the change but sits where nothing precedes it gets
        # no declared fact rather than a declared zero, because the row is ABSENT from the evidence
        # list there, not present-and-zero, and this schema has no predicate for absence. See the
        # README, "the absence the schema cannot yet state".
        return {"value": 1}
    if fact_id.startswith("envelope."):
        return {"value": 1}
    # A benign message plants its facts as present-and-not-firing: the assertion is that the property
    # is absent and is reported absent, which is the half of acceptance a risk-only corpus cannot
    # reach. `available: true` and not merely `value: 0`, because those are different claims: a row
    # that came back NotApplicable would otherwise satisfy an expected zero.
    if plan.intent == INTENTS["ordinary"]:
        return {"available": True, "value": 0}
    return {"value": 1}


def manifest_entry(
    plan: MessagePlan,
    raw: bytes,
    coverage: str,
    encoding: str = "plain",
    size: str = "small",
    shape: str | None = None,
    quoted_tail: int = 0,
    quoted_tail_characters: int = 0,
) -> dict:
    # The envelope is recorded in the manifest, not re-derived by `seed`. It has to be, because a
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

    # An id on both sides is a contradiction, not a precedence question, and this refuses rather than
    # picking one. It is a defect in the PLAN rather than in any message, so it is raised here where
    # the plan is turned into a manifest rather than left for `check` to find after a Host has run.
    both = sorted(set(facts) & set(plan.not_planted))
    if both:
        raise ValueError(
            f"index {plan.index}: {', '.join(both)} declared in BOTH `facts` and `not_planted`. A fact "
            "cannot be planted and asserted absent at once; the manifest would contradict itself and "
            "`check` would have to pick a side."
        )
    for fact_id in plan.not_planted:
        if fact_id not in FACTS:
            raise ValueError(
                f"index {plan.index}: `not_planted` names {fact_id!r}, which is not a fact this corpus "
                "knows. A typo here would silently assert nothing."
            )

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
        # The drawn shape, recorded rather than left for a consumer to infer from the bytes. Present on
        # every message at corpusVersion 4, including the default: a consumer that has to tell "plain"
        # from "the field is missing" would be reading a decision out of an absence.
        "encoding": encoding,
        # The drawn size, and it is what the message IS rather than what was asked for: a message with
        # no html and no attachment cannot express medium or large, so it records `small` even under
        # `mixed`. A consumer selecting a large fixture by this field is selecting a real one.
        "size": size,
        "submission": envelope,
        "thresholdTargeted": plan.threshold_targeted,
        "turn": plan.turn,
        # Character counts, so that a provider refusal is attributable to the FIXTURE rather than to
        # the pipeline. A window entry over 2,000 characters IS cut by the adapter
        # (NimbleMessageState.cs:155, over ConversationContext -- the body is not bounded there), and
        # the whole rendered prompt is checked against NumCtx as UTF-8 bytes before the provider
        # refuses outright rather than truncating, so a window that is too long is a defect in the
        # corpus and has to be visible in the manifest. Counts are of the raw strings sent, not of the
        # rendered prompt.
        "turnCharacters": len(plan.text),
        "windowCharacters": sum(len(entry) for entry in plan.window),
        "intent": {"label": plan.intent, "note": plan.note},
        "planted": planted,
        # Sibling to `planted`, and the fifth kind of claim this manifest can make: ids that must be
        # ABSENT from the evidence list rather than present with a value. A different KIND from the
        # four `expected` predicates, which are all assertions about a row; this asserts no row.
        # Field present only when the
        # message makes such a claim, so absence of the field means "nothing asserted absent" rather
        # than "asserted empty".
        **({"notPlanted": sorted(plan.not_planted)} if plan.not_planted else {}),
        # The drawn dense shape, present only on a message this axis actually transformed. The
        # additive form is deliberate and it is the same argument `notPlanted` makes: an absent field
        # means "this batch drew no shapes", and writing `bodyShape: "prose"` on every message is the
        # choice that would destroy that reading and so would force a corpusVersion bump instead.
        **({"bodyShape": shape} if shape else {}),
        # The appended quoted tail's requested size, present only when the option was used. Additive
        # on the same argument as `bodyShape`: absence means "no tail was appended", not "a tail of 0".
        **({"quotedTail": quoted_tail} if quoted_tail > 0 else {}),
        # THE ACTUAL APPENDED LENGTH, not the requested one: the tail tiles a unit to at least the
        # requested size, so the two differ by up to one unit. The check needs the real figure
        # because the tail is a SEPARATE FIELD with its own budget and the turn is legitimately
        # longer than the body by exactly this much.
        **({"quotedTailCharacters": quoted_tail_characters} if quoted_tail > 0 else {}),
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

    # Every plan is built BEFORE anything is written, so the batch can be refused as a whole. A
    # refusal discovered part-way through the loop would leave the messages already written on disk,
    # and a directory holding some of a batch and no manifest is not a batch: anything globbing it
    # finds files it cannot tell from a complete run. Building all the plans first costs nothing,
    # because a plan is data and the MIME is built in the loop below, where a turn's window can be
    # filled from the bytes of the message before it.
    plans = [builders[index % len(builders)](args.seed, index) for index in range(args.count)]

    # The dense-shape refusal, pre-flighted for the same reason the size one is: a refusal discovered
    # part-way through the loop would leave messages on disk with no manifest, and a directory holding
    # some of a batch is not a batch. Two ways a plan can be unable to carry a shape, and both are
    # checked here rather than discovered in the transform:
    #
    #   1. it has not declared itself dense_safe, which is the default and means its planted facts may
    #      live in the text this axis would replace;
    #   2. its html does not contain its text, so re-deriving the html from the text is impossible and
    #      replacing the text alone would ship a message whose two parts DISAGREE. That disagreement is
    #      a real coverage flag (`html_text_disagreement`) and it is an uncontrolled extra variable in
    #      a family whose entire point is one variable.
    if args.body_shapes != "off" and args.body_characters > TURN_LIMIT:
        print(
            f"refusing: --body-characters {args.body_characters} is above the adapter's own body "
            f"budget of {TURN_LIMIT} (`NimbleOptions.MaxBodyCharacters`), so the fixture would be one "
            "the adapter must shorten before the classifier sees it and would not be the fixture it "
            "claims. Nothing has been written.",
            file=sys.stderr,
        )
        return 2

    if args.body_shapes != "off":
        for index, plan in enumerate(plans):
            shape = draw_body_shape(index, args.body_shapes)
            if shape is None:
                continue
            if not plan.dense_safe:
                print(
                    f"refusing: --body-shapes {args.body_shapes} at index {index} ({args.profile}). "
                    "This profile's plan has not declared itself dense_safe, so its planted facts may "
                    "be needles in the text body that this axis would replace. A manifest describing a "
                    "message the extractor never saw is the one outcome this lane must never produce. "
                    "Nothing has been written.",
                    file=sys.stderr,
                )
                return 2
            if plan.html is not None and plan.text not in plan.html:
                print(
                    f"refusing: --body-shapes {args.body_shapes} at index {index} ({args.profile}). "
                    "This plan has an html part that does not contain its text, so the shape cannot be "
                    "re-derived into the html and the two parts would disagree at an html/text flag. "
                    "Nothing has been written.",
                    file=sys.stderr,
                )
                return 2

    # The size refusal, pre-flighted and keyed on the same predicate that does the growing. A message
    # whose only writable part is the turn cannot be made large without breaking section 3.2's
    # constraint, and reporting it as large while emitting a small one is the one outcome this lane
    # must never produce: a manifest that does not describe its batch.
    if args.size_mix not in ("small", "mixed"):
        for index, plan in enumerate(plans):
            if not any(size_carriers(plan)):
                print(
                    f"refusing: --size-mix {args.size_mix} at index {index} ({args.profile}). This "
                    "message has no html part and no attachment, so the only part left to grow is the "
                    "text body, and the adapter's own body budget is 2,500 characters. Growing the body "
                    "would surface as a provider refusal rather than as the size it claims, and adding "
                    "a part that was not there would move a coverage flag as a side effect of length. "
                    "Nothing has been written.",
                    file=sys.stderr,
                )
                return 2

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    messages = []
    previous_raw: bytes | None = None
    for index in range(args.count):
        plan = plans[index]
        # The dense-shape transform, applied to the PLAN rather than to the built MIME, so that the
        # body the manifest counts is the body that is written. Both parts move together when the
        # plan has html, because a text-only replacement would leave the html asserting the prose the
        # turn no longer contains.
        shape = draw_body_shape(index, args.body_shapes)
        if shape is not None:
            dense = shape_body(shape, args.body_characters)
            plan = replace(
                plan,
                text=dense,
                html=plan.html.replace(plan.text, dense) if plan.html is not None else None,
                # THE DESCRIPTION GOES WITH THE BODY IT DESCRIBES. `undescribedChange` records that this
                # message's payment destination moved in the body; a shaped body does not carry that move,
                # so keeping the record would ship a manifest describing bytes that are not in the file.
                # It is a DESCRIPTION and never a claim, so dropping it falsifies nothing, and its absence
                # then reads as "nothing described" -- which is true of the transformed message.
                undescribed_change=None,
            )
        # THE TAIL IS APPENDED AFTER THE SHAPE, so a message can carry a dense body AND a quoted tail
        # and the two fields are independently sized. The html moves with the text for the same reason
        # it does in the shape transform: a text-only append would leave the parts disagreeing.
        appended = quoted_tail(args.quoted_tail) if args.quoted_tail > 0 else ""
        if appended:
            tailed = plan.text + appended
            plan = replace(
                plan,
                text=tailed,
                html=plan.html.replace(plan.text, tailed) if plan.html is not None else None,
            )
        encoding = draw_encoding(args.seed, index, args.encoding_mix)
        has_html, has_attachment = size_carriers(plan)
        size = draw_size(args.seed, index, args.size_mix, has_html or has_attachment)
        raw = build_mime(
            sender_name=plan.sender_name,
            sender_address=plan.sender_address,
            subject=plan.subjects[0],
            text=plan.text,
            html=plan.html,
            facts=plan.facts,
            seed=args.seed,
            index=index,
            encoding=encoding,
            size=size,
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
        messages.append(
            manifest_entry(plan, raw, args.coverage, encoding, size, shape, args.quoted_tail,
                           len(appended))
        )
        previous_raw = raw

    manifest = {
        "corpusVersion": CORPUS_VERSION,
        "generatedBy": "tools/corpus/corpus.py",
        "seed": args.seed,
        "profile": args.profile,
        "coverage": args.coverage,
        # The REQUESTED axis value, beside the per-message drawn one. Both are needed to reproduce a
        # batch: the drawn shape is not recoverable from the seed alone without also knowing which
        # rule drew it, and `mixed` and `plain` are different rules that can agree on one message.
        "encodingMix": args.encoding_mix,
        "sizeMix": args.size_mix,
        # The REQUESTED shape axis, beside the per-message drawn shape, for the same reason the two
        # lines above are here: `off` and `all` are different rules that can agree on a message, and
        # a consumer reproducing a batch needs to know which rule drew it.
        #
        # ABSENT when the axis is off, which is the one place this key differs from `encodingMix` and
        # `sizeMix` above. Those two are mandatory at version 4 because a consumer must be able to tell
        # "plain" from "the field is missing"; this one is additive on the `notPlanted` argument, so
        # that a batch drawn with no shapes is BYTE-IDENTICAL to the same batch before this axis
        # existed. That identity is a claim the README makes and this lane re-measures, and writing
        # `"off"` here would silently break it for every existing invocation.
        **({"bodyShapeMix": args.body_shapes} if args.body_shapes != "off" else {}),
        # The parameter that drove the length, beside the rule that chose the shape, for the same
        # reason `encodingMix` sits beside the per-message `encoding`: a batch is reproducible only
        # with both, and a length is now a knob rather than a constant.
        **({"bodyShapeCharacters": args.body_characters} if args.body_shapes != "off" else {}),
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
    encodings: dict[str, int] = {}
    sizes: dict[str, int] = {}
    for message in messages:
        encodings[message["encoding"]] = encodings.get(message["encoding"], 0) + 1
        sizes[message["size"]] = sizes.get(message["size"], 0) + 1
    print(f"wrote {len(messages)} message(s) to {out}  profile={args.profile} coverage={counts}")
    print(f"encoding mix={args.encoding_mix}  drawn={encodings}")
    print(f"size     mix={args.size_mix}  drawn={sizes}")
    if args.body_shapes != "off":
        shapes: dict[str, int] = {}
        for message in messages:
            if "bodyShape" in message:
                shapes[message["bodyShape"]] = shapes.get(message["bodyShape"], 0) + 1
        print(f"shape    mix={args.body_shapes}  drawn={shapes}")
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

    # The durable route, and the choice decides what can be seen. POST /v1/assessments is
    # assessment-only: it hands the pipeline an ephemeral payload, so the MIME adapter never runs and
    # a planted MIME fact can never appear as a finding. Measured; the first measurement run was
    # reduced to that one row.
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

def shapes_of(raw: bytes) -> dict:
    """What the BYTES actually carry, as the shape fields claim it.

    Measured from the message rather than recomputed from the plan, because the claim being checked is
    "this batch's bytes carry this shape" and a plan is not the bytes. The html and attachment sizes
    are of the DECODED payload, so a quoted-printable part is measured as the text it decodes to and
    not as the bytes it happens to occupy on the wire.
    """
    msg = email.message_from_bytes(raw)
    quoted_printable = 0
    html_bytes = 0
    attachment_bytes = 0
    for part in msg.walk():
        if part.get_content_maintype() == "multipart":
            continue
        if (part.get("Content-Transfer-Encoding") or "").lower() == "quoted-printable":
            quoted_printable += 1
        payload = part.get_payload(decode=True) or b""
        ctype = part.get_content_type()
        if ctype == "text/html":
            html_bytes += len(payload)
        elif ctype == "application/pdf":
            attachment_bytes += len(payload)
    subject = msg.get("Subject") or ""
    return {
        "quotedPrintableParts": quoted_printable,
        "htmlBytes": html_bytes,
        "attachmentBytes": attachment_bytes,
        # The RAW header value, undecoded, because the claim is about the wire form: `compat32` leaves
        # `=?...?=` in place, which is exactly what is being asked about.
        "subjectIsEncodedWord": "=?" in subject and "?=" in subject,
    }


def shape_failures(batch_dir: Path, manifest: dict) -> list[str]:
    """Every message whose bytes do not carry the shape its manifest claims.

    Gated on `corpusVersion`, and the gate is the whole reason this is a version check rather than an
    absence check. A version 3 manifest makes no shape claim, so there is nothing to hold it to and it
    is SKIPPED; a version 4 manifest makes the fields mandatory, so their absence is a defect. Reading
    "the field is missing" as "the shape is plain" would let a truncated manifest pass by omission,
    which is the failure mode this whole schema exists to prevent.

    ALL failures are returned rather than the first, so one broken batch reports its whole defect list
    instead of hiding every problem after the first behind a re-run.
    """
    # A manifest is a text file anyone can edit, so the version may not be a number. Refused rather
    # than skipped, and refused rather than crashed: an unreadable version is not an OLD one, and a
    # version this cannot be read is a manifest whose claims cannot be placed against any schema.
    try:
        version = int(manifest.get("corpusVersion", 0))
    except (TypeError, ValueError):
        return [
            f"corpusVersion is {manifest.get('corpusVersion')!r}, which is not a number, so this "
            "manifest's shape claims cannot be placed against a schema version"
        ]
    if version < 4:
        return []

    base = batch_dir.resolve()
    failures: list[str] = []
    for entry in manifest.get("messages", []):
        path = batch_dir / entry["file"]
        # CONTAINMENT BEFORE ANY READ. `entry["file"]` comes straight out of the manifest, and a
        # manifest is a text file anyone can edit (`ingest` reconstitutes one from an operator dataset),
        # so an entry such as `../secrets` would otherwise be read and judged as though it were part of
        # the batch. `seed` has carried this guard since it was written; this pass was added later
        # without it, which is the ordinary way a guard fails to travel.
        #
        # Refused as a FAILURE and not skipped: a `check` that quietly ignores an escaping entry
        # reports a clean batch for a manifest it never fully read, which is the false-clearance
        # shape rather than a tolerance.
        if not path.resolve().is_relative_to(base):
            failures.append(
                f"{entry['file']}: path escapes the batch directory, so it was not read"
            )
            continue
        if not path.exists():
            failures.append(f"{entry['file']}: `check` cannot verify its shape: no such file")
            continue
        shapes = shapes_of(path.read_bytes())

        # The turn limit, which the README has claimed since the size axis landed and which nothing
        # checked until now. It is a manifest field rather than a property of the bytes, so it is
        # read off the entry; a body at or over the limit is a fixture defect the adapter turns into
        # a provider refusal, which is exactly the failure the size axis is built to avoid.
        turn = entry.get("turnCharacters")
        # A DECLARED QUOTED TAIL IS A SEPARATE FIELD WITH ITS OWN BUDGET, so the turn is legitimately
        # longer than the body by exactly the length appended. `NimbleMessageState.cs:62-63` truncates
        # `BodyText` and `QuotedText` against the SAME budget SEPARATELY, so what the budget bounds is
        # each field and not their sum -- which makes bounding the whole turn a proxy that over-counts
        # once a tail exists. **It over-counted the first time one was generated**: a 3000-character
        # tail made `turnCharacters` 3309 and `check` refused it, which is a FALSE refusal rather than
        # a fixture defect. The allowance is the MEASURED appended length, not the requested one.
        tail = entry.get("quotedTailCharacters") or 0
        if isinstance(turn, int) and turn > TURN_LIMIT + tail:
            failures.append(
                f"{entry['file']}: the turn is {turn} characters, over the adapter's body budget of "
                f"{TURN_LIMIT} (`NimbleOptions.MaxBodyCharacters`)"
                + (f" plus a declared quoted tail of {tail}" if tail else "")
                + ", so the fit must shorten it before the classifier sees it and this message is not "
                "the fixture it claims to be"
            )

        claimed = entry.get("encoding")
        if claimed is None:
            failures.append(
                f"{entry['file']}: corpusVersion 4 requires `encoding`, and this message has none, "
                "so the shape it carries is unstated rather than plain"
            )
        elif claimed not in ENCODING_MIXES:
            failures.append(f"{entry['file']}: `encoding` is {claimed!r}, which is not a known mix")
        elif claimed == "plain":
            if shapes["quotedPrintableParts"] or shapes["subjectIsEncodedWord"]:
                failures.append(
                    f"{entry['file']}: claims `plain` and its bytes are not: "
                    f"{shapes['quotedPrintableParts']} quoted-printable part(s), "
                    f"encoded-word subject={shapes['subjectIsEncodedWord']}"
                )
        elif claimed == "quoted-printable" and not shapes["quotedPrintableParts"]:
            failures.append(
                f"{entry['file']}: claims `quoted-printable` and no leaf part declares it"
            )
        elif claimed == "rfc2047" and not shapes["subjectIsEncodedWord"]:
            failures.append(
                f"{entry['file']}: claims `rfc2047` and the Subject is not an encoded-word"
            )

        size = entry.get("size")
        if size is None:
            failures.append(
                f"{entry['file']}: corpusVersion 4 requires `size`, and this message has none"
            )
        elif size not in SIZE_MIXES:
            failures.append(f"{entry['file']}: `size` is {size!r}, which is not a known size")
        else:
            # Each size names a BAND, and it is bounded on both ends. A lower bound alone would make
            # `small` unfalsifiable in the direction that matters: a batch that secretly grew could
            # still declare `small` and pass, and the claim would only ever be able to fail upward.
            # A first version of this assertion was one-sided and a tampered manifest declared `small`
            # over a 256 KiB attachment without a word from `check`.
            biggest = max(shapes["htmlBytes"], shapes["attachmentBytes"])
            lower = SIZE_TARGETS[size]
            above = [name for name in SIZE_MIXES if SIZE_TARGETS[name] > lower]
            upper = SIZE_TARGETS[min(above, key=lambda name: SIZE_TARGETS[name])] if above else None
            if biggest < lower:
                failures.append(
                    f"{entry['file']}: claims `{size}` (at least {lower} bytes in its html or "
                    f"attachment) and the larger of the two is {biggest}"
                )
            elif upper is not None and biggest >= upper:
                failures.append(
                    f"{entry['file']}: claims `{size}` (under {upper} bytes in its html or "
                    f"attachment) and the larger of the two is {biggest}"
                )

        # The dense shape, when the axis drew one. The vocabulary is CLOSED on purpose: `bodyShape`
        # is a claim about which transform produced the bytes, and `check` can assert membership in a
        # finite set and can assert nothing at all about an open generator. The turn-limit assertion
        # above is what bounds the body; this one is what names it.
        claimed_shape = entry.get("bodyShape")
        if claimed_shape is not None and claimed_shape not in BODY_SHAPES:
            failures.append(
                f"{entry['file']}: `bodyShape` is {claimed_shape!r}, which is not one of the declared "
                f"shapes ({', '.join(BODY_SHAPES)}), so the density it claims is one this corpus "
                "cannot produce and cannot reproduce from a seed"
            )
    return failures


def cmd_check(args: argparse.Namespace) -> int:
    key = read_key_file(args.key_file)
    manifest_path = Path(args.manifest)
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    base = args.base_url.rstrip("/")

    # The shape pass runs FIRST and is local: it needs no ledger, so a batch whose manifest does not
    # describe its own bytes is reported as that, rather than as a connection error from a Host that
    # was never worth asking. It returns before any request, because nothing downstream of a false
    # manifest can be trusted to mean what it says.
    shape = shape_failures(manifest_path.parent, manifest)
    if shape:
        print(f"\nSHAPE: {len(shape)} message(s) do not carry the shape they claim", file=sys.stderr)
        for line in shape:
            print(f"  SHAPE: {line}", file=sys.stderr)
        print(
            "\nA shape claim is about the BYTES. A batch whose manifest disagrees with its own files "
            "has not been checked against the pipeline, it has been checked against nothing.",
            file=sys.stderr,
        )
        return 1

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
                # A negative control is NOT satisfied by a message that never ran. Every signal is
                # absent from a refused message's ledger entry because there are no evidence rows at
                # all, which is absence for the wrong reason: it would make the control pass on a
                # message whose bytes the pipeline never looked at. Reported rather than counted.
                for fact_id in message.get("notPlanted", []):
                    missing.append(
                        f"{message['file']}: {fact_id} is asserted ABSENT, which is true here only "
                        "because the message was refused at intake and no evidence was gathered. "
                        "Absence for that reason does not test the claim."
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

        # The negative controls, checked against ABSENCE specifically. A row that is present with
        # `availability: Unavailable` does NOT satisfy this: it says the pipeline considered the
        # signal and found nothing to say, which is a different observation from the signal never
        # having been emitted. Conflating the two is how a control goes toothless.
        present_ids = {
            row["signalId"]
            for row in decision.get("evidence") or []
            if isinstance(row, dict) and row.get("signalId")
        }
        declared_ids = {fact["id"] for fact in message.get("planted", [])}
        for fact_id in message.get("notPlanted", []):
            if fact_id not in FACTS:
                missing.append(
                    f"{message['file']}: `notPlanted` names {fact_id!r}, which is not a fact this "
                    "corpus knows, so the assertion checked nothing"
                )
            elif fact_id in declared_ids:
                missing.append(
                    f"{message['file']}: {fact_id} is declared in BOTH `planted` and `notPlanted`, so "
                    "the manifest contradicts itself and one of the two is not a claim"
                )
            elif fact_id in present_ids:
                missing.append(
                    f"{message['file']}: {fact_id} must be ABSENT from the evidence list and it is "
                    "present there"
                )

    # Both kinds of claim count, because a batch whose only assertions are negative controls has
    # something to verify and must not report SKIPPED as though it declared nothing.
    declared = sum(
        len(message.get("planted", [])) + len(message.get("notPlanted", []))
        for message in manifest["messages"]
    )
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
                # The shape fields, and they are recorded rather than left out because the claim is
                # TRUE of these bytes: `build_mime` was called at its defaults, so the message really
                # is plain, and it has no html and no attachment, so there is no carrier part and the
                # `small` band holds by construction. Leaving them out while the manifest says
                # `corpusVersion` 4 would make `check` fail every message of every reconstituted
                # batch, which is a false failure in a correct batch and the worst kind here: it is
                # how a real defect gets ignored later.
                "encoding": "plain",
                "size": "small",
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
        # `null`, not "plain"/"small": no axis was DRAWN here, because this verb has no mix flags and
        # reconstitution applies none. The per-message values above are what the builder defaulted to
        # and are verifiable from the bytes; these two say that nothing was chosen. `generate` always
        # writes a real value in both, so a null mix and a null `source` cannot both be true of a
        # batch this tool built.
        "encodingMix": None,
        "sizeMix": None,
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
    gen.add_argument(
        "--size-mix",
        default="small",
        choices=list(SIZE_MIX_CHOICES),
        help=(
            "how large each message is, carried by its html part and its attachment and never by the "
            "text body; `mixed` draws one of " + ", ".join(SIZE_MIXES) + " per message from the seed"
        ),
    )
    gen.add_argument(
        "--body-shapes",
        default="off",
        choices=list(BODY_SHAPE_MIX_CHOICES),
        help=(
            "the DENSITY of the author's text, which is the one thing content-varied expansion turns "
            "on and the one thing no other axis moves; `off` (the default) transforms nothing and "
            "declares no `bodyShape`, `all` gives one message per shape so a batch contains the whole "
            "table, and a named shape forces it. Refused on any profile whose plan has not declared "
            "itself dense_safe: " + ", ".join(SHAPE_UNITS)
        ),
    )
    gen.add_argument(
        "--body-characters",
        type=int,
        default=SHAPE_BODY_CHARACTERS,
        help=(
            "how long the dense body is, in characters. A PARAMETER rather than a constant because a "
            "run showed the fit's cut is a SIZE rule. The default 2000 is the largest length MEASURED "
            "to arrive un-cut (2000 clean, 2500 cut, threshold unmeasured between). Refused above "
            + str(TURN_LIMIT)
            + ", the adapter's own body budget, because a fixture over it is not the fixture it claims"
        ),
    )
    gen.add_argument(
        "--quoted-tail",
        type=int,
        default=0,
        help=(
            "append a quoted reply section of about N characters after the splitter's own `on ... "
            "wrote:` attribution, so the message carries a QuotedText. `> 2500` is the only size at "
            "which the QUOTED half of the `shortened` flag fires UNCONDITIONALLY, since the fit starts "
            "at MaxBodyCharacters 2500 and reduces it a step per pass; a tail at or below the budget "
            "fires only if a pass drives the budget under it. 0 (the default) appends nothing and "
            "declares no `quotedTail`"
        ),
    )
    gen.add_argument(
        "--encoding-mix",
        default="plain",
        choices=list(ENCODING_MIX_CHOICES),
        help=(
            "how each message's leaf bodies and Subject are transferred; `mixed` draws one of "
            + ", ".join(ENCODING_MIXES)
            + " per message from the seed"
        ),
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
