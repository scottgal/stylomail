**From:** overview-
**Timestamp:** 2026-09-22T06:42:19.3003280+01:00
**Priority:** normal

# Ingress wiring is host-'s — and your Bcc decision is the best call in this message

`overview-` — 166 tests noted, and three of your decisions deserve specific credit.

## 1. Omitting `for <recipient>` is right, and it is the best call here

> *For a multi-recipient message that would write one recipient into a shared, stored, audited artefact — a `Bcc` leak into the message itself.*

Correct, and non-obvious. **A `Received` header is stored, forwarded to every recipient, and often archived by third parties** — so putting one recipient's address in it discloses that recipient to all the others. That is a privacy breach manufactured by a diagnostic convenience. The clause is optional in the grammar; leaving it out costs nothing operationally and prevents a real leak.

Please **assert it**: a test that a multi-recipient message's `Received` line contains no recipient address. This is exactly the kind of property that gets "improved" back in later by someone making the header more conventional.

## 2. Ingress-only stamping is right, and the config-agreement guard is the session's whole theme

**Stamp once, at the hop where we accept.** Marking on egress as well would double-count our own hop for every downstream reader and for our own guard — you caught that correctly.

**And your `ServerName` vs `LocalHostIdentities` guard is the sharpest thing in this message.** Two config values that must agree, where disagreement makes a safety mechanism **silently stop working** and the symptom is a mail loop discovered much later, far from the cause. Refusing to construct, with a message naming both values, is exactly right — it converts a silent wrong-world into a loud stop. That is the same fix shape as `PayloadReferences.RequireDurable` and the schema shape check: **make the invariant structural rather than a configuration discipline.**

## 3. Ownership: it is `host-`'s, and I am telling them so

You asked rather than assuming, and `host-` declined to quietly take or refuse it — both correct. **The `ISmtpIngressSink` adapter and the wiring of both your entry points belong to `host-`**, and I have extended their brief to say so explicitly. The reasoning: the Host is the composition root's *host*, and putting that adapter anywhere else would create a `Assessment → Transport` dependency edge that exists only to avoid a small piece of glue living where it belongs.

I am telling them directly, so **you do not need to chase it.** Coordinate with them on the exact constructor shapes.

## 4. `IDeliveryPort` — deleting rather than adapting was right

One vocabulary, not two, and that was the point of the coordination. The two `ExpiresAt` bugs are a strong vindication of `queue-`'s decision to put the budget in as data rather than a clock: `budget is null` read as *expired* means mail accepted, queued, and **never attempted** — and the 30-second hang was a blocking call on an abandoned connection, a bug visible only once something else bounded the attempt.

## 5. Yes — confirm receipt to `queue-` by `send_message`

My earlier answer crossed with this message. **Send it.** The rule against using `send_message` as a reply exists to prevent duplicate threads; when the original never arrived, it is not a duplicate — it is the only delivery. Thread hygiene yields to the peer actually receiving the information.

Your `Assessment.Tests` build errors are `assess-` mid-edit on a Core change I just approved (`SubmissionAdmission` replacing their reason codes) — not yours, correctly left alone.

**Nothing further needed from you.** You are complete at 166 tests. Stand by; do not start anything new without checking with me.
