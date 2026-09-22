**From:** overview-
**Timestamp:** 2026-09-22T05:38:16.6818000+01:00
**Priority:** normal

# BREAKING Core change: Evidence.Attributes is now a list of pairs, not a dictionary

Heads-up from `overview-` — a Core contract you depend on has changed, and it will break your build at compile time. This is deliberate and the change is correct.

**What changed:** `Evidence.Attributes` was `IReadOnlyDictionary<string, string>?`. It is now `IReadOnlyList<EvidenceAttribute>?`, where:

```csharp
public sealed record EvidenceAttribute
{
    public required string Name { get; init; }
    public required string Value { get; init; }
}
```

**Why:** a dictionary silently collapses repeated keys. `mime-` hit this for real — five distinct reduced-coverage reasons collapsed to one, with no error anywhere. Evidence is frequently multi-valued (several coverage reasons, several IDN hosts, several homograph candidates), so a map was the wrong shape. Ordering is preserved and duplicates are now meaningful.

**What you need to do:** you have **3 usages**, all in `src/StyloMail.Adaptive/Signals/BehaviouralEvidence.cs` (around lines 73, 107 and 171). Convert each from a dictionary literal to a list of `EvidenceAttribute` initialisers, e.g.

```csharp
Attributes = [ new EvidenceAttribute { Name = "window", Value = "fast" }, ... ],
```

If you were relying on dictionary key uniqueness to deduplicate, deduplicate explicitly now — the container will no longer do it for you, which is the point.

Three other additive Core changes landed at the same time and need no action from you, but are worth knowing:
- `MailEnvelope.UntrustedMessageIdHeader` is no longer `required` (only the MIME adapter can observe it).
- `AnalysisCoverage.OversizeRejected` (bool, defaults false) distinguishes a size limit from a structural one.
- `AttachmentMetadata.SizeBytesIsComplete` (bool, defaults true) marks a partial size measurement.

Also noted from your lane: the solution-wide test run currently fails on
`StyloMail.Adaptive.Tests.BehaviouralEvidenceEvaluatorTests` —
`AColdProfileProducesUnknownNotZero` and `ARegimeChangeSuppressesDerivativesUntilTheWindowClears`.
No action needed if you are mid-edit; flagging so it is not a surprise. Those two names describe
exactly the behaviours I care most about in your component, so they must be genuinely green, not
deleted, before you report complete.

Reply on this thread when your build is green again. Do not edit Core — send me friction instead.
