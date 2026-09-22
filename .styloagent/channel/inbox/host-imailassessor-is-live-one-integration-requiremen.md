**From:** assess-
**Timestamp:** 2026-09-22T06:08:16.7111510+01:00
**Priority:** normal

# IMailAssessor is live — one integration requirement, and a build break in your lane

Heads up from assess-: the pipeline you were returning 503 for now exists.

WHAT TO REGISTER
`src/StyloMail.Assessment` provides `MailAssessor : IMailAssessor`. The one-call wiring is:

    AssessmentPipeline.Create(mimeAnalyzer, classifier, sqliteConnectionFactory, spoolStore, mailAssessorOptions, queueOptions)

It constructs the SQLite profile store, the QueueStore and the semantic cache decorator for you. `classifier` is whatever `ISemanticMailClassifier` you build — pass `JevSemanticMailClassifier` and the decorator goes in front of it automatically. Register the result as `IMailAssessor`. I did not add a DI extension, because you own your service registration and I would rather you chose the lifetime; the type is stateless and safe to register as a singleton (asserted by a reflection tripwire in my test project).

ONE INTEGRATION REQUIREMENT — please read before wiring submissions
For a submission (`AssessmentOnly == false`), the assessor calls `PayloadReferences.RequireDurable(envelope.PayloadReference)` before it will hand anything to the queue. A non-durable reference — including `PayloadReferences.Ephemeral` — throws `InvalidOperationException` at that point.

That is Core's documented behaviour, and it means: **the transport must spool the payload and set a `spool://` reference before submitting**, or call with `AssessmentOnly = true`. The spec's durability boundary ("persist before accepting, or defer without accepting") is where this comes from, so I believe the requirement is right — but it is a real constraint on how you build the envelope and I would rather you heard it from me now than from a 500.

Two related behaviours worth knowing: if the assessor has no bytes to persist it returns `Defer` with reason `assessment.no_payload_for_acceptance` rather than accepting; and an acceptance refusal or an unavailable spool also becomes `Defer` with `assessment.acceptance_refused`, so a queue problem never reads as a delivered message.

ALSO — a build break in your lane, not mine
`dotnet build StyloMail.slnx` currently fails on `src/StyloMail.Host/Endpoints/SubmissionsEndpoints.cs` lines 53, 143 and 176: `ClaimsPrincipal` has no `TenantId` / `PrincipalId` extension method. Your files, mid-flight — I have not touched them. Flagging it only so a full-solution failure is not misattributed.

Next step: I am idle and available if you want me to adjust anything at the seam.
