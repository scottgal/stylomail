**From:** ingress-
**Timestamp:** 2026-09-22T07:42:28.1758620+01:00
**Priority:** normal

# ingress- done: sink, both listeners, delivery worker hosted, both assertions — 129 green, host probed live. One decision needed.

All four items are done. 129 Host tests green, stable over 12 consecutive runs. Runtime: `claude-deepseek` / `deepseek-flash` per your memory note.

FILES
Created (all under src/StyloMail.Host/Hosting/): HostIngressSink.cs · IngressComposition.cs ·
HostTransportOptions.cs · PrincipalSubmissionAuthenticator.cs · IngressHostedServices.cs
Modified: HostServices.cs · Program.cs · Auth/HostAuthOptions.cs · StyloMail.Host.csproj (now
references StyloMail.Transport; no cycle).
Tests: IngressSinkTests, IngressCompositionTests, SmtpIngressTests, DeliveryWorkerHostingTests,
IngressPipelineSeamTests, + TestSupport.cs. 94 → 129.

1. ISmtpIngressSink — HostIngressSink. Spools the bytes through the composition root's SpoolStore
   under `ingress-<internalMessageId>`, translates to MailAnalysisInput, calls AssessAsync with
   AssessmentOnly=false and ClientIdempotencyKey=null, reads SubmissionId. **It has no queue and no
   intake in its constructor**, so the double-accept is structurally impossible, not merely avoided —
   and a test counts accepts to prove exactly one. `Allow` with no SubmissionId is a Defer, never a
   250. Reject prose never reaches the client: reason *codes* only.

2. Both listeners constructed. SmtpSubmissionListener is a hosted service gated on
   `SmtpIngress:Enabled` (decided at StartAsync, not registration — the composition root runs before
   the test host layers config in). CloudflareEmailRoutingConnector is a container singleton over the
   same sink. I also implemented `ISubmissionAuthenticator` over PrincipalDirectory plus a new
   `ApprovedSenderIdentities` config list on HostPrincipalOptions — without it the SMTP listener's
   AUTH path is dead code, since a principal with no approved senders may only send as the null
   sender. Flagging it as an addition beyond the four items.

3. QueueDeliveryWorker hosted as a BackgroundService forwarding the shutdown token to RunAsync, so its
   bounded drain works. The SmtpDeliveryPort is built inside it rather than registered, because "is
   there a port?" and "does the worker run?" are one question. No upstream configured → no worker, and
   the startup line says so out loud rather than leaving a silent absence.

4. Both assertions, at construction, naming both values: transportMax <= queueMax (checked for both
   ingresses regardless of enabled), and one SpoolStore — compared by **reference identity**, so a
   second instance over the same directory is refused.

PROBED — not just tested. Real `dotnet run -- serve`, real socket, no provider secrets: 21/21 checks.
Both ports bind; health/metrics 200; full SMTP conversation; relay refusal 550 for an unserved
domain; a served-domain message NOT acknowledged without a durable row (451); the payload spooled
through the shared spool; exactly one `Received:` line prepended with everything after it
byte-for-byte what the client sent; the hop marker names no recipient; queue empty. Separately, a host
with a drifted ingress bound **refuses to start** (exit 134) naming both values.

What the probe could NOT prove: the acceptance mapping needs a working assessor and this deployment
has no provider secrets. Acceptance is proven in the suite instead, against the container-wired fake
with a real queue row on disk. I am not claiming a live 250.

DECISION NEEDED — the Cloudflare connector has no route. It is constructed, container-owned, built
over the same sink and covered by tests, but no `POST /v1/ingress/cloudflare` exists. My responsibility
line is wiring, not routes, and mapping a new anonymous route with its own shared-secret credential
model is a privilege-model decision. So today a deployment enabling CloudflareIngress gets a connector
nothing calls. Do you want the route, and is the Worker's shared secret presented as a bearer header
with a new env-fed config key? I did not invent a secret name for it.

DEFECT FOUND IN transport- 's LANE, reported and filed (medium). `SmtpSubmissionListener.StopAsync`
then `DisposeAsync` throws ObjectDisposedException out of a live session's cleanup on host shutdown:
StopAsync nulls `_listener` before draining, so the later DisposeAsync returns early without joining
the drain and disposes the connection semaphore sessions are still releasing. Their tests only ever
use `await using`, so the pair was never exercised. Evidence: with my workaround reverted, 7 of 15
suite runs red; with it, 15 of 15 green. Workaround in my lane (stop, don't dispose), documented in
place so it is not tidied back, and I told them what I think the fix is.

CONTRACT FRICTION — three components disagree about whether a bounce may be sent. transport's
`MaySendAs` says the null sender is always permitted; `AssessmentValidation` raises
`envelope.unapproved_sender_identity`; `QueueStore.ValidateSubmission` rejects it with an
ArgumentException. `MailEnvelope.MailFrom` and `QueueSubmission.MailFrom` both document the opposite
of the queue's behaviour. Recorded as a characterisation test for whoever owns the call; same shape on
the HTTP submission path, so not ingress-specific. Also: `HopCount` never reaches the queue on the
submission path (Step7Async omits it, so MaxHops is a backstop that reads as present and is not).

DELIBERATELY NOT DONE — delete-after-accept, per your instruction while the two questions with queue-
are open. Every accepted message is written twice: the ingress copy the pipeline reads back from, and
the queue's copy under the queue id, which is the acceptance. The ingress copy is unreferenced and the
orphan sweep collects it after an hour. Pinned by a test so the change that removes it has something
to fail against. Also not done: making `JevOptions.Endpoint` host-configurable, which is what blocks a
live acceptance probe against a local stub — a config key that can redirect message content is an
operator privacy decision, not a wiring gap. Suggested, not taken.

Checkpoint at `.styloagent/channel/saved-context/ingress--context.md`.
