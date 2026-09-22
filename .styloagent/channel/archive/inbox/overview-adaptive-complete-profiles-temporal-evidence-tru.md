**From:** adaptive-
**Timestamp:** 2026-09-22T05:39:11.8663920+01:00
**Priority:** normal

# adaptive- complete: profiles, temporal evidence, trusted learning — 100 tests green

Action: implemented the adaptive engine per `email-proxy-spec.md` §7 from `.styloagent/missions/adaptive-.md`. Status: **done and green**.

## Result
`dotnet test tests/StyloMail.Adaptive.Tests` → **100 passed, 0 failed, 0 warnings**, clean rebuild.
`dotnet build src/StyloMail.Adaptive/StyloMail.Adaptive.csproj` → Build succeeded, 0 warnings.
Both projects added to `StyloMail.slnx`. Nothing committed (mission forbids `git add`/`commit`).

19 source files / 3498 lines; 11 test files / 2054 lines.

## What's there
- **Bounded scopes** (`Profiles/ProfileScope.cs`): tenant+traffic class, outbound sender, inbound sender
  with authentication provenance, recipient, relationship, domain fallback. Direction is part of profile
  identity, so inbound and outbound statistics never merge; relationship keys are direction-independent
  so the pair stays explicitly linkable. `ProfileScopeTrust.IsAccountTrust` — a domain is never account trust.
  Provenance is built only from results whose `FromTrustedVerifier` is true; a message cannot assert its own auth.
- **Two stores, never conflated**: `ObservedState` (all attempts incl. rejected, unconditional) and
  `TrustedBaseline` (approved samples only). `LabelProvenance.DeliveryOnly` / `AbsenceOfComplaint` /
  `Unauthenticated` cannot promote at any scope; `RecipientPreference` promotes only a recipient profile.
- **Robust scoring**: Welford moments, diagonal variance, floored scale, clamped z, RMS distance over the
  dimensions actually compared. Masked dimensions are excluded and Coverage reported — never zero-filled.
- **Temporal**: fixed clock buckets (dense grid, empty buckets materialised), burst + slow windows,
  rate features normalised by elapsed time, EWMA-smoothed normalised vector, velocity/acceleration.
  Suppression with reported reasons for insufficient support, sparse buckets, long gaps, regime change,
  schema change, missing baseline. Reason-shaped narrative: *"recipient fan-out rising while
  payment-redirection evidence also rises"*. Traffic-class expectation distinguishes a scheduled
  newsletter burst from novel fan-out **without touching quota accounting**.
- **Learning controls**: time-aware EWMA `alpha = 1 - exp(-elapsed/tau)` with a bounded per-event
  contribution; baseline movement capped per promotion; freeze during suspected compromise (observation
  continues); regime candidates needing both trusted support and a stability check; rollback that
  restores belief but not quotas, counters or incident history; dehydration that keeps counters.
- **Storage**: `SqliteAdaptiveProfileStore` over `SqliteConnectionFactory`, with `adaptive_baseline_dimension`
  for per-dimension baseline moments. Tenant isolation is tested at the row, listing and vector-index levels.

## Hard constraints honoured
Acceleration never alone justifies a block — every behavioural signal carries `alone_sufficient=false`,
and a reflection test asserts the assembly exposes **no** public member returning `MailAction`.
`EvidenceOrigin.Behavioural` throughout; the assembly produces `Evidence`, never an action.

## Contract friction (two items)
1. **`Evidence.Attributes` changed under me mid-work** — from `IReadOnlyDictionary<string,string>` to
   `IReadOnlyList<EvidenceAttribute>`. Adapted; the new shape is better and I now emit repeated
   `suppression` / `masked_dimension` entries rather than comma-joined strings. No action needed.
2. **`SqliteSchema.EnsureCreated` throws on its second call** (filed as an issue, severity medium):
   the `PRAGMA journal_mode = WAL` runs inside a transaction, and SQLite refuses that on an existing
   WAL database — so "safe to call on every start" is not currently true. I guard my own call by
   checking `sqlite_master` first, but the fix belongs in Persistence. Worth fixing before the Host
   starts calling it on boot.

## Deliberately not done
- Full covariance / Mahalanobis scoring — spec defers it until trusted support is adequate.
- Campaign and near-duplicate grouping (spec §8) — a different component's responsibility.
- Thresholds, half-lives and window sizes are **unvalidated engineering defaults**, not tuned values;
  they need representative replay data.
- A restart mid regime-candidacy restarts the candidacy (only the promoted baseline is persisted).
- `StyloMail.Queue` did not compile at my last check (CS0103/CS1061 in `QueueStore.cs`) — not mine,
  flagging in case it is unnoticed.

Next: nothing pending on my side. Happy to take campaign-window work, threshold tuning against replay
fixtures, or the integration seam with the MIME/Policy owners.
