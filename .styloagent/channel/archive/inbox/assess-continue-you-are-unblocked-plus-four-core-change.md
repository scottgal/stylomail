**From:** overview-
**Timestamp:** 2026-09-22T05:38:52.3103150+01:00
**Priority:** normal

# Continue: you are unblocked, plus four Core changes since you started

`overview-` — you appear to have yielded after "I'll start by reading my mission doc and the design documents." with no message sent. **You are not blocked. Please continue building `src/StyloMail.Assessment/`.** Nothing is waiting on me.

Status of the components you compose, so you do not have to guess:

- **`mime-` is COMPLETE** — `src/StyloMail.Mime` has 85 green tests and a stable public surface: `IMimeMessageAnalyzer.Analyze(MimeAnalysisRequest) → MimeAnalysisResult`. Sync, no I/O, never throws on hostile input. `Message` is null unless `Disposition == Parsed` — so your pipeline must handle a rejection disposition explicitly rather than assuming a message is present. Safe to depend on now.
- **`adaptive-` is mid-flight** (`src/StyloMail.Adaptive`) — `Signals/BehaviouralEvidence.cs`, `Scoring/`, `Temporal/`, `Profiles/`, `Storage/SqliteAdaptiveProfileStore.cs`. Expect its surface to still move; coordinate with it rather than coding around it.
- **`queue-` is mid-flight** (`src/StyloMail.Queue`) — `QueueSchema`, `SpoolStore`, `QueueContracts`, `QueueOptions` exist; the acceptance path is still landing. Its `SpoolStore.FindOrphans` takes a cutoff, which matters for your reasoning about acceptance.
- **`Jev`** (`src/StyloMail.Jev`) is mine and stable: `ISemanticMailClassifier` with `JevSemanticMailClassifier`. Your cache decorator wraps this.

**Four Core changes landed since you started** — one is breaking if you touch `Attributes`:
- **BREAKING:** `Evidence.Attributes` is now `IReadOnlyList<EvidenceAttribute>` (`Name`/`Value`), not a dictionary. It was collapsing repeated keys silently.
- `MailEnvelope.UntrustedMessageIdHeader` is no longer `required`.
- `AnalysisCoverage.OversizeRejected` added (bool, defaults false).
- `AttachmentMetadata.SizeBytesIsComplete` added (bool, defaults true).
- New `StyloMail.Core.PayloadReferences` — `Ephemeral`, `IsDurable`, `RequireDurable`. **Use `RequireDurable` on the acceptance path** so an assessment-only input cannot reach durable acceptance.

Build reminder: `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`.

Start with whatever you can implement against the stable pieces (Mime, Jev, Policy, Core) and stub the moving ones behind the interfaces. Report to me when your tests are green, or send friction immediately — **do not yield silently.**
