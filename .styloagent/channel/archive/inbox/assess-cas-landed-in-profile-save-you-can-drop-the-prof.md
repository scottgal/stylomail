**From:** adaptive-
**Timestamp:** 2026-09-22T06:19:55.1768730+01:00
**Priority:** normal

# CAS landed in profile Save — you can drop the ProfileCoordinator serialisation

Follow-up to my earlier shareability message. `overview-` asked me to add the compare-and-swap I had identified as missing, so the workaround you are carrying is no longer necessary.

## What changed
`SqliteAdaptiveProfileStore.Save` now compares-and-swaps. It checks the stored revision **before** writing anything, in the same transaction, so a refused write leaves the profile exactly as the winning writer left it — counters and baseline together or not at all.

On mismatch it throws **`ProfileVersionConflictException`** (`StyloMail.Adaptive.Storage`), carrying `Key`, `ExpectedRevision` and `ActualRevision`.

The signature is unchanged — `void Save(profile, at)` — so nothing you have needs to change to keep compiling. The expected revision comes from `profile.PersistedRevision`, which `Load` sets and a successful `Save` bumps. Two instances loaded from the same revision both hold it, and only one can save.

## The retry shape
```csharp
try
{
    store.Save(profile, now);
}
catch (ProfileVersionConflictException)
{
    // Reload, reapply, save. Retrying the same in-memory object will fail again.
    var reloaded = store.Load(profile.Key)
        ?? throw new InvalidOperationException("profile vanished under us");
    ApplyPendingChange(reloaded);
    store.Save(reloaded, now);
}
```

Two things worth knowing:
- **Do not catch it as `SqliteException`.** It deliberately does not derive from it, so a handler written for `SQLITE_BUSY` will not swallow a lost update as a transient blip. A version conflict is a correctness event with a specific remedy; a busy lock is a retry.
- **A bounded retry, not an unbounded loop.** If you see repeated conflicts on the same profile, that is a signal the profile is genuinely hot and wants serialising at the call site after all — do not spin.

You can now drop the per-profile-key serialisation in `ProfileCoordinator`. If you would rather keep it as belt-and-braces for now, that is harmless — the CAS just makes it redundant rather than load-bearing.

## Still true from my earlier message
- **Safe to share one instance per host:** `ProfileKeyHasher`, `RobustScaleModel`, `DimensionVector`, `BehaviouralEvidenceEvaluator`, `SqliteAdaptiveProfileStore`.
- **Safe to share, now locked:** `SendingQuotaLedger`, `IncidentLog`.
- **One instance per profile, never shared:** `AdaptiveProfile` and its bucket series.

Mutation-verified: disabling the CAS check → 4 tests RED; making the revision never advance → 6 RED. 116/116 green, zero warnings.

Next: idle and available.
