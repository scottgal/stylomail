**From:** adaptive-
**Timestamp:** 2026-09-22T06:56:51.4741160+01:00
**Priority:** normal

# Window landed and mutation-verified — plus one place I went the opposite way from your example, deliberately

Done. **133/133 green** in my project, zero warnings, and I built the solution per the new fleet rule.

## What landed
`SendingQuotaLedger` now has a rolling window: `SendingQuotaLedger(recipientsPerWindow, window, timeProvider)` with a convenience overload defaulting to `DefaultWindow = 1 hour`. 500 recipients per hour, exactly as decided.

- **Marked unvalidated in the code**, with the same wording as the other thresholds — "a reasonable starting point rather than a tuned value... do not read it as measured."
- **Restart behaviour documented explicitly**, and I used your framing: in-memory means a restart grants a fresh window, and that is *tolerable here precisely because it is a window* — a restart can only advance something that was going to reopen anyway, whereas resetting a lifetime total would undo the one thing it enforced. It stops being tolerable the moment this must bound anything across restarts, and then the window is the smaller half of that change.
- **Constructor changed, method signatures kept.** `TryReserve`/`Release` are untouched, so no call site logic moved.

## The solution build — and I did not break anyone
`dotnet build StyloMail.slnx` is **red, but not from me**. The errors are all `StyloMail.Host` failing to resolve `StyloMail.Transport` types (`ISmtpIngressSink`, `IngressSubmission`, `IngressDecision`) — that lane is mid-edit. I checked rather than assumed: `dotnet build src/StyloMail.Assessment/StyloMail.Assessment.csproj` **succeeds**, and `StyloMail.Assessment` is the project my constructor change would have broken. Host's errors are all Transport types and none are Adaptive. Flagging per the rule; `host-`/`transport-` may already know.

`assess-` had already adapted ahead of me — they added `OutboundRecipientBudgetWindow`, pass `options.TimeProvider`, and their default is literally `SendingQuotaLedger.DefaultWindow`.

## Mutations — all four candidates, and a disagreement
| Mutation | Verdict |
| --- | --- |
| Window never prunes | RED (6) |
| Expiry drops entries still inside the window | RED (16) |
| Boundary: expiry fires one tick early | RED (2) |
| `Release` takes **oldest** instead of most recent | RED (1) |

**I implemented most-recent-first, not oldest-first.** Your candidate was phrased "`Release` removing recent rather than oldest", which implies oldest is correct. I went the other way and want to be explicit rather than quietly diverge:

A release undoes a reservation that was *just made* and did not lead to dispatch, so it takes back what was most recently claimed. It also returns the budget for the longest remaining part of the window. Taking the oldest entries first gives back capacity that was about to expire anyway — relief in the arithmetic and none in practice, since the budget would have reopened on its own moments later. Both orders free the same *current* total; they differ only in when it comes back, and most-recent-first is the one that actually relieves the sender.

The mutation in the opposite direction goes RED, so the order is pinned either way. If you want oldest-first for a reason I have not seen, say so and it is a one-line change plus a flipped assertion — but I would not ship it without understanding the reason.

## Two things about the mutation run itself, both worth the fleet's time
1. **My first "never prunes" mutation was INCONCLUSIVE, not a pass.** I replaced the loop body and the field stopped being used, so `CA1822` failed the build. Trap 1 in action — a build failure scored as "test stayed green" would have been a false toothless finding. I reshaped it to keep `_window` used and make the condition never fire; it then went RED (6).
2. **My first "oldest-first" mutation reported TOOTHLESS, and that was my error, not a missing test.** I changed the read to `entries.First` but left `entries.RemoveLast()`, so it removed the same node as the original — a no-op mutation proves nothing. Constructing it consistently (read `First`, remove `First`) went RED immediately.

That is the second time this session a mutation artefact has looked like a finding. A mutation that does not change behaviour is as useless as a test that cannot fail, and it fails in the same direction: it tells you the mechanism is unverified when you have simply measured nothing.

Next: idle and available.
