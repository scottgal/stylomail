# `transport-` — saved context

Checkpointed 2026-09-22. Enough to cold-start without re-deriving anything.

---

## 1. Identity and scope

Owns **`src/StyloMail.Transport/`** and **`tests/StyloMail.Transport.Tests/`**. Three deliverables:

1. **SMTP/MTA handoff** — egress (client) and a restricted inbound submission listener.
2. **The queue's delivery port** — `IDeliveryPort` implementation. The queue owns no SMTP.
3. **Cloudflare Email Routing connector** — inbound only, zero provider credentials.

**Boundary — hard rule:** do not modify any other project. If a contract must change, `send_message`
the owner. This applies to `Host`, `Queue`, `Core`, `Assessment`, everything. I declined an
`overview-` request to edit `QueueStore` on exactly this ground, and was told I was right to.

Repo: `/Users/scottgalloway/RiderProjects/stylomail` · branch `main` · HEAD `ef43812`
(earlier in the session HEAD was empty — history appeared during the day).

**No commits by me.** `git add` / `git commit` are forbidden in my brief.

## 2. Build and verify

```bash
export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"

# Judge my own work, and avoid being blocked by another lane's transient red:
dotnet test tests/StyloMail.Transport.Tests/StyloMail.Transport.Tests.csproj

# REQUIRED before declaring completion (fleet rule): own-project-green is not "I broke nobody":
dotnet build StyloMail.slnx
```

**State at checkpoint: 192 passed, 0 skipped. Solution 0 errors, 0 warnings.**

Analyzers run as **errors** (`CA*`/`IDE*` fail the build). `net10.0`, SDK 10.0.201.
`InternalsVisibleTo("StyloMail.Integration.Tests")` was granted at one point and **removed** — that
project no longer exists; the seam test lives in my test project instead.

## 3. Source layout

**`Smtp/`** — `SmtpReply`, `SmtpReplyReader` (bounded), `SmtpDataWriter` (dot-stuffing + CRLF),
`SmtpSession` (client state machine), `SmtpChannel`/`SocketSmtpChannel`, `SmtpBounds`, `SmtpUpstream`
/`SmtpCredentials`, `SmtpTranscript` (redacting), transaction results + exceptions.

**`Delivery/SmtpDeliveryPort.cs`** — implements `IDeliveryPort`.

**`Ingress/`** — `SmtpSubmissionListener`, `SmtpIngressSession`, `SmtpIngressOptions`,
`RecipientDomainPolicy`, `TransportHeaderScanner`, `ReceivedHeader`, `IngressContracts`.

**`Cloudflare/CloudflareEmailRoutingConnector.cs`.**

**`tests/.../Support/`** — `FakeSmtpServer` (loopback + real TLS + self-signed cert),
`FakeSmtpBehaviour`, `TestSmtpClient`, `SmtpTestRig`, `TestIngressSink`, `DeadEndpoint`,
`BlockingStream`.

**Dependencies: Core + Queue only. Zero third-party packages** — the SMTP protocol is hand-written
deliberately (see decisions).

## 4. Decisions — do not re-litigate without evidence

1. **Hand-written SMTP client, not MailKit.** MailKit is in the NuGet cache and available; it is a
   deliberate rejection. Byte preservation and the `InDoubt` boundary are only ownable in code we
   control — a library round-tripping through `MimeMessage` can re-fold headers and break DKIM.
2. **One SMTP transaction per recipient.** Gives per-recipient outcomes and prevents Bcc leakage to
   the upstream. Costs N× body bytes for N recipients; accepted.
3. **`InDoubt` boundary = the write of the end-of-data terminator.** Before → `Failed`; after →
   `InDoubt`. Errs toward retry: a duplicate is recoverable, a silent loss is not.
4. **A TLS downgrade is never accepted**, even in `Opportunistic` mode: advertised-then-refused
   `STARTTLS` fails the session. Capabilities are cleared and EHLO re-issued after the handshake.
5. **The transport has no spool.** `IngressSubmission.RawMessage` is bytes; `SmtpDeliveryPort` never
   resolves `spool://`. Deliberate — a second place that gets durability right is where a second
   spool root would come from.
6. **Stamp once, at ingress.** One `Received` hop marker prepended on the way in; **never** on the
   way out (double-counting would fool our own loop guard). Body byte-for-byte; existing headers
   byte-for-byte and in order.
7. **No `for <recipient>` clause in the hop marker.** A `Received` header is stored, forwarded to
   every recipient and archived by third parties — naming one recipient discloses it to all.
   `ReceivedHeaderStamp` cannot even express a recipient (reflection-tested).
8. **`ISmtpIngressSink` is shared** by the SMTP listener and the Cloudflare connector. One accept
   path, one `IngressDecision`.
9. **`IsAcceptanceValid`** — an acceptance that names no queue row is downgraded to a deferral by
   both ingresses. A `250`/`202` requires a durable queue id.
10. **`CloudflareIngressResult` factories are public**; `Refused` requires a **4xx** (a 5xx would tell
    the Worker to re-offer a permanent refusal forever).

## 5. Contract facts settled this session

**Accept path (I had this WRONG once).** The **assessor** is the only component that accepts.
`ISmtpIngressSink` implementations call `IMailAssessor.AssessAsync` with `AssessmentOnly = false` and
read `MailAssessment.SubmissionId` — non-null exactly when a durable row exists. **Never call
`QueueStore.AcceptAsync` from the transport.** My original guidance to `host-` said otherwise and
would have reintroduced a duplicate-delivery bug; `assess-` and `host-` both caught it.

**Cancellation never propagates.** `SmtpDeliveryPort.DeliverAsync` returns one outcome per recipient
for *every* cancellation, including the caller's own:
- before the terminator → `TemporaryFailure`
- **after the terminator → `InDoubt`** (may already be accepted)
- recipients not reached → `TemporaryFailure`

**Bounce ruling (`overview-`).** A null sender (`<>`) means "this is a DSN"; we do not originate
bounces, so it is **refused on the outbound submission path**. `MaySendAs` has no null-sender
exemption. **Inbound is different** — a DSN delivered *to* a mailbox arrives unauthenticated and is
legitimate. Canonical representation is **`""`**, not `"<>"`.
⚠ An open defect below contradicts the "inbound unaffected" half.

**Hop marker.** `ReceivedHeader.Prepend` at ingress, `"ESMTP"` or `"HTTPS"`, no `from` clause for
Cloudflare (it has no connection and must not invent one). Interpolated tokens reduced to
`[A-Za-z0-9._-@:[]` — everything structural becomes `?`, so nothing can forge a clause.
`SmtpIngressOptions.ServerName` **must be in `LocalHostIdentities`** and `CloudflareIngressOptions.ByHost`
likewise, or the loop guard cannot recognise our own hop — both throw at construction.

**Size bounds are coupled.** `SmtpIngressOptions.MaxMessageBytes` (64 MB) is deliberately equal to
`QueueOptions.MaxPayloadBytes`. If `queue-` lowers theirs and mine does not follow, my ingress accepts
payloads the queue refuses, surfacing as spurious "spool pressure". `ingress-` asserts
`transportMax <= queueMax` at construction in the composition root.

## 6. Open items — watch list

1. **Inbound DSN cannot be accepted (reported to `queue-` and `overview-`).** The ruling's "inbound
   is unaffected" is **false**: inbound reaches `ValidateSubmission`, where
   `Require(submission.MailFrom)` (`QueueStore.cs:1940`) throws on empty **before** `IsNullSender`
   (`:1946`) runs. A DSN to a mailbox is refused. Not a regression — `Require` predates the ruling.
   Fix is `queue-`'s (scope the refusal to `Direction == Outbound`). My scope statement:
   **every link read, not run.**
2. **The sink's `HopCount` link looks unasserted** (flagged to `ingress-`). `HostIngressSink.cs:130`
   is correct, but the only `HopCount` refs in `Host.Tests` are `= 0` **inputs**, so deleting that
   line would break no test while the loop backstop silently went inert again.
3. **`MaxHops` was inert end to end** until today — fixed by a Core field + `Step7Async` + the sink +
   a nullable-aware queue guard. Now complete; item 2 is what protects it.

## 7. Hazards

**Mutation sweep corrupts the shared tree.** `queue-`'s harness edits source **in place**. Before
believing any red, run **both** signals (documented in `.styloagent/PROTOCOL.md` → `## Completion gate`):

```bash
ls .styloagent/tools/.mutation-sweep.lock   # a sweep is running RIGHT NOW
find src -name '*.bak'                      # a sweep was killed; mutation still applied
```

**Check both, not just the lock** — SIGKILL leaves mutated source and **no lock**. My lane is a
bystander (only `mutations/mime.py` and `queue.py` exist) but my suite can still be affected.

**A red is a claim about a moment.** My suite goes red for other lanes' in-flight edits (I reference
Queue, and `DeliveryWorkerSeamTests.cs` is `queue-`'s file in my project). Before reporting:
(1) sweep signals, (2) has *my* code changed, (3) read the failing line in their source. I have
nearly mis-reported a transient twice.

**Nothing verifies prose.** This is the session's dominant failure shape — a sentence read as current
that points somewhere else. Five instances, three of them prose: my mission doc's "route through it"
(double-accept trap), `queue-`'s "inbound bounces are unaffected" (false), `assess-`'s "I have
documented it" (their edit tool no-op'd silently), the `(a)`/`(b)` label collision, and
`DropDuringDataBody` promising an interruption it did not deliver. **After any prose-only change,
re-read the file.** Grep a claim about another lane rather than recalling it.

## 8. Hard rules

- **Never read, print, or reference `jevkey.pvt`.**
- **Never print, persist, or interpolate a secret.** `SmtpCredentials.ToString()` and
  `SmtpTranscript` redact by construction; the Cloudflare shared secret is compared with
  `CryptographicOperations.FixedTimeEquals` and never rendered.
- **Do not edit another lane's file.** Own-project-green is not "nothing broke"; build the solution
  before declaring done.
- **Production is forbidden** without an explicit operator `prod` instruction.

## 9. Test rig notes worth not rediscovering

- `FakeSmtpServer` uses a **real loopback socket and real TLS**. A mock at the stream seam would have
  hidden the STARTTLS reply-reader bug permanently.
- **`CloseAfterDataCommand`** closes after `DATA` without reading. What the client observes depends on
  the **socket buffer**: ≤ 4 KB → written successfully, failure seen awaiting the verdict →
  `InDoubt`; ≥ 64 KB → the write itself fails → `TemporaryFailure`. **Both correct.** A test wanting
  the pre-terminator path must exceed the buffer *deliberately*.
- **`FinalReplyDelay` is a ceiling, never waited out** (technique credit `queue-`). Set 30s,
  `WaitForMessagesAsync` is the synchronisation point since the message is recorded *before* the
  delay — so the cancellation test is deterministic and ~400ms, preserving the suite's lack of clock
  dependence.
- **`DeadEndpoint`** owns a listener for the test's duration; `AcceptedCount` proves the connection
  was *established* before failing. Replaced a `ClosedPort()` probe-then-release **TOCTOU** (found by
  `ingress-`) where another test could take the port between release and dial.
- `SmtpSession` **rebinds its reply reader after a TLS upgrade** — a stale reader silently reads TLS
  records as SMTP replies. Regression-tested.
- Regression tests have been **mutation-verified** (revert the fix, confirm red, restore) for:
  cancellation-after-terminator, the `StopAsync`/`DisposeAsync` drain, and the null-sender exemption.

## 10. Habits that earned their keep

- **Measure, don't reason**, when a peer points at an anomaly. The socket-buffer table settled an
  argument that two lanes had been having on inspection alone.
- **Mutation-verify a regression test** — a test that also passes against the bug is worse than none.
- **State scope honestly**: "every link read, not run" is worth more than implying execution.
- **Flag, don't patch**, across lane boundaries — even when the instruction appears to authorise it.

## 11. Coordination

`overview-` has exited and returned more than once; treat arbitration as intermittent. Direct peers:
`queue-` (port contract, seam test), `ingress-` (Host wiring, the sink), `assess-` (acceptance
pipeline, validation), `host-` (idle, handed ingress wiring to `ingress-`), `mime-`, `adaptive-`,
`access-`.

Bus hazard (fleet-wide): **`reply_to_thread` archives but does not deliver** — use `send_message` for
anything a peer must act on. If a reply may not have landed, `send_message` is correct.
