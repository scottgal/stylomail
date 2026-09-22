**From:** host-
**Timestamp:** 2026-09-22T06:49:04.3707810+01:00
**Priority:** normal

# host-: assessor WIRED and verified — your fail-fast requirement caught a real defect

host- here. Wiring done, and **your "fail fast and loud" requirement found a real defect that compiling would never have shown.** 94/94 green.

**What I built**
- `HostCredentials` — both secrets from environment variables only. Uses `JevOptions.ApiKeyEnvironmentVariable` rather than the literal; adds `ProfileKeyEnvironmentVariable = "STYLOMAIL_PROFILE_KEY"` (defined once, greppable, no scattered literal). `jevkey.pvt` is neither referenced nor documented as a source.
- Unconfigured → `UnavailableMailAssessor` sentinel, as you specified. Half-configured, or master key under 32 bytes → throws at startup.
- `ProfileKeyHasher` built from the env master key; one deployment-level key with the tenant id mixed into the hashed input, with the per-tenant-keys-are-a-future-option **and "changing this is a migration"** note in the code, since every stored profile key becomes unreadable.
- 8 new tests including: a failure **never repeats the secret it rejected** (exception text travels into logs and crash reporters), and one asserting `HostCredentials.MinimalMasterKeyBytes == ProfileKeyHasher.MinimumKeyBytes` so the mirror cannot drift.

**The defect your requirement caught.** I wired it, built clean, and then ran it — because you said "verify end to end rather than only compiling". With only `TYPESAFE_API_KEY` set, **the host started normally and reported healthy.** `IMailAssessor` is a lazy singleton, so the credential check never ran at boot; it would have failed on the first real message instead. That is precisely the "works perfectly in tests, quietly broken in production" shape you warned about, and it passed a clean build.

Fixed by forcing `GetRequiredService<IMailAssessor>()` at startup in both `Program.cs` and `CliApplication`. Re-verified:

- **Half-configured** → refuses to start, exit 134, message names `STYLOMAIL_PROFILE_KEY` and never the key value, no "Now listening".
- **Fully configured** → starts, `/health/live` 200.
- **Malformed message through the real host** → 422, which proves DI actually *constructed* `MailAssessor` (construction failure would have surfaced as 500). Chose that probe deliberately: it forces resolution without transmitting anything to the provider.

**Two honest notes on verification quality.** My re-verification was blocked for several minutes by compile errors in `Queue/QueueDeliveryWorker.cs` and `Assessment/ProfileCoordinator.cs` — mid-edit by `queue-` and `assess-`, not mine. And when the build finally reached my code it caught a missing `using` on the very line I had just added — so the change was never verified until I retried. I only trust the final numbers above because the last run built my project clean and passed 94.

**One correction to `assess-`'s status note, worth recording.** They warned my 85/85 might not exercise the seam end to end because my fake assessor "does not accept". That was true when I first reported, and their instinct was right — but I had already fixed it in the same round: `RecordingAssessor` now reads the payload back through the durable reference and accepts through the **real** `QueueStore`, wired via the container. So my submission tests do run spool → assess → accept → queue end to end. Their correction was the right thing to send given what they had; it just crossed with the fix.

Still not starting the spool deletion, per your instruction — the two questions for `queue-` (shared spool root? measured peak?) remain open and unassumed.
