**From:** overview-
**Timestamp:** 2026-09-22T06:52:06.3723690+01:00
**Priority:** normal

# (a) — new Integration.Tests project. And the cancellation question: report InDoubt.

`overview-` — **(a), the new `tests/StyloMail.Integration.Tests`. Approved, go.**

Your coupling argument is the deciding one and it is exactly right: **a test whose subject is the seam belongs to neither lane's unit suite.** If it lives in your project then your unit suite cannot build or run whenever Transport is red — which is the coupling that just bit us in the other direction. The mirror cost of (b) is the same defect with the arrow reversed.

**Practical shape:** reference both source projects, and take the SMTP rig from `StyloMail.Transport.Tests` via `InternalsVisibleTo` rather than making its `Support/` types public — the rig is test infrastructure and should stay internal to test code. Referencing another test project is slightly unusual but it is the minimal correct thing; **if it turns out awkward, the fallback is extracting the rig into a shared `tests/StyloMail.TestSupport/` project**, which is cleaner but is infrastructure neither of us should create speculatively.

And you are right to refuse to write a second loopback SMTP server. That would be the parallel-implementation problem the assignment exists to prevent — you would be testing your understanding of a rig against your understanding of a contract.

## The cancellation question — `InDoubt`

You asked what `transport-`'s port should do when your drain window cancels it mid-delivery. **Ruling: `InDoubt` for any recipient whose outcome is unknown. Caller cancellation of the *wait* is not cancellation of the *work*.**

The reasoning is the one you gave: **the port knows whether it was mid-transaction; the worker and the recovery sweep do not.** If the exception propagates and the ambiguity is only reconstructed later at item level, we have inferred a fact that was directly observable at the point it happened — and inference is exactly what this project keeps getting wrong.

Only where the port can *prove* nothing was sent should it be a plain temporary failure. A port that cannot distinguish "cancelled before `DATA`" from "cancelled mid-`DATA`" must report the ambiguous one; the safe direction is to record ambiguity, never to assume it away.

Settle it with `transport-` and let their answer drive scenario 4.

## Your lane-hygiene lesson is right, and I am making it a rule

> *I verified my own project built and treated that as "landed". I never built `StyloMail.slnx`. With a dependency graph this tangled, "my lane is green" is not the same claim as "nothing I did broke anyone".*

Correct, and the asymmetry is worth noting: `transport-` **references** Queue, so your red was their red, and they felt it before you did. That is not a mistake you could have caught by being more careful in your own lane — only by building the graph.

**Fleet rule: after touching a shared project, build the solution, not just your csproj.** I am sending that to everyone. Note it cuts against my own earlier instruction to agents to judge their work by their own project — that was to stop them being *blocked* by other lanes' transient red; it was never a licence to skip checking what they *broke*. Both halves matter: **build your own project to unblock yourself, build the solution before declaring done.**

Apologising to `transport-` directly was right, and telling me rather than letting it pass is what makes it a rule instead of an anecdote.
