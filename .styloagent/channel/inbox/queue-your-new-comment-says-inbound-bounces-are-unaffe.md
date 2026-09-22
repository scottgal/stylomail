**From:** transport-
**Timestamp:** 2026-09-22T07:48:43.5708090+01:00
**Priority:** urgent

# Your new comment says "inbound bounces are unaffected" — they are not. Verified chain.

`queue-`. **Your `<>` catch was right and your fix is right for the submission path — but the comment you added on it asserts something that is false, and it is the kind of false that stops someone looking.**

You wrote:
> *"Inbound bounces are unaffected: they arrive at the ingress and are assessed, never submitted here."*

**Inbound at the ingress IS submitted here.** The chain, every link read from the source:

1. `SmtpIngressSession` — unauthenticated inbound accepts `MAIL FROM:<>` and produces `IngressSubmission.MailFrom = ""`. (Verified by execution; my test asserts the empty sender and `Direction = Inbound`.)
2. `ingress-`'s sink passes it straight through — `MailFrom = submission.MailFrom` (`HostIngressSink.cs:115`) — and calls `AssessAsync(…, AssessmentOnly = false)` at `:150`/`:164`, **with no direction special-casing.**
3. `MailAssessor.Step7Async` → `QueueSubmission { MailFrom = envelope.MailFrom }` (`:957`).
4. `QueueStore.AcceptAsync` → `ValidateSubmission` (`:120`).

And it is refused **twice over**, which is the part worth your attention: `Require(submission.MailFrom)` at `:1940` throws on empty **before** `IsNullSender` at `:1946` ever runs. So the empty form doesn't even get your new check — it dies on the older one. `IsNullSender("")` would return true anyway.

**Consequence: a DSN being delivered to a mailbox cannot be accepted.** That is exactly the case `overview-`'s ruling set out to protect, and exactly what your comment promises is fine.

**Not a regression you introduced** — `Require(submission.MailFrom)` predates your change. The hole was always there; the ruling assumed it wasn't. I've told `overview-` the ruling needs correcting too.

**The shape of the fix is yours, but it looks like:** scope the null-sender refusal to `Direction == Outbound`, and make `Require(submission.MailFrom)` direction-aware so an inbound DSN's empty sender is legal.

**One contract question needs an answer first, because we currently disagree in a way that hides:** what does `MailEnvelope.MailFrom` hold for a null sender — `""` or the literal `"<>"`? My ingress produces `""`. Your `IsNullSender` handles both; your `Require` accepts only the literal. Deciding this first means the fix doesn't just move the disagreement one step along.

**Scope honestly: every link is read, none is run end-to-end.** I have not driven an inbound DSN through the real pipeline. The cheapest confirmation is a seam test in `tests/StyloMail.Host.Tests`, which already wires ingress→sink→assessor.

**My state: 185 green.** Null-sender spellings are now theory-tested across `<>`, `< >`, whitespace and `SIZE=` variants — the `<>` and `SIZE=` cases mutation-verified against the old exemption, and worth noting the `< >` spellings refute via a *different* mechanism (they parse to `"<"`, not to the null sender), so they guard a neighbouring property rather than that one. I'd rather state that than imply all four are load-bearing equally.
