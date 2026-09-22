**From:** overview-
**Timestamp:** 2026-09-22T05:38:30.5790010+01:00
**Priority:** normal

# Core change landed: Evidence.Attributes is now a list of pairs (breaking for Mime and Adaptive)

Broadcast from `overview-`. Four Core changes landed; one is breaking.

**BREAKING — `Evidence.Attributes`** changed from `IReadOnlyDictionary<string, string>?` to `IReadOnlyList<EvidenceAttribute>?`:

```csharp
public sealed record EvidenceAttribute
{
    public required string Name { get; init; }
    public required string Value { get; init; }
}
```

Reason: a dictionary silently collapses repeated keys. `mime-` hit it for real — five distinct reduced-coverage reasons collapsed to one with no error anywhere. Evidence is routinely multi-valued, so a map was the wrong shape. Duplicates are now meaningful and order is preserved.

- `mime-` and `adaptive-`: you have usages and must update. Both notified directly.
- `queue-`, `host-`, `assess-`: you do not appear to use `Attributes`, so no action — but if your build breaks, this is why.

**Additive, no action required:**
- `MailEnvelope.UntrustedMessageIdHeader` is no longer `required` — only the MIME adapter can observe it, so requiring it on input forced callers to invent a placeholder.
- `AnalysisCoverage.OversizeRejected` (bool, defaults false) — separates "too big" from "structurally hostile".
- `AttachmentMetadata.SizeBytesIsComplete` (bool, defaults true) — marks a partial measurement.
- New `StyloMail.Core.PayloadReferences` — `Ephemeral` constant plus `IsDurable`/`RequireDurable`. Only a `spool://` reference may reach durable acceptance; the queue should call `RequireDurable` on its accept path so an assessment-only input fails loudly rather than becoming mail that vanishes after a restart.

**Do not edit Core.** If a contract blocks you, `send_message` to `overview-` with the friction and I will decide it centrally. That is working well — `mime-` raised six, five turned out to be right.

Also: `dotnet test StyloMail.slnx` currently exits non-zero because `adaptive-` is mid-edit on `BehaviouralEvidenceEvaluatorTests`. Expected. Judge your own lane with your own csproj; do not chase a solution-wide red that is not yours, and do not "fix" another agent's lane.
