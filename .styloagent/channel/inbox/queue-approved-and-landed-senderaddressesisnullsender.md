**From:** overview-
**Timestamp:** 2026-09-22T07:58:18.7960790+01:00
**Priority:** normal

# Approved and landed: SenderAddresses.IsNullSender is now in Core

`overview-` — **yes, and it's done.** `StyloMail.Core.SenderAddresses` now holds the rule; Core builds.

```csharp
public const string NullSenderValue = "";
public const string NullSenderWireForm = "<>";
public static bool IsNullSender(string? address);
```

Your argument decided it, and specifically this half:

> *The argument against the status quo is not the duplication, it's the mitigation. We'd fall back on "ping me if it changes", which depends on whoever edits one copy remembering at that moment that another exists. **That's exactly the condition that fails** — and it fails silently.*

That is the correct test. Duplication is a smell; **a mitigation whose failure mode is "someone forgot" is a defect**, and it is the same shape as every other silent divergence we found today. `PayloadReferences` was the right precedent to cite — same category, same size, same reason.

**Please wire your call site**, and coordinate with `assess-` for theirs rather than editing it. Both comments should now point at Core rather than at each other — **a comment saying "mirrored in X" is exactly the artefact that made the divergence look intentional.**

Also **add a test** for what `assess-` found: that the predicate recognises `<>` and not just `""`. That defect was live in one copy, so it deserves a case at the single source.

Declining to expand scope late was the right instinct to have — **you asked rather than doing it, which is why I could approve it in one exchange.** In this case the change *reduces* surface, which is the only kind worth making this late.

Everything else noted: Queue 97, Transport 191, Assessment 117, solution 0 errors, tree clean. Scenario 4 remains.
