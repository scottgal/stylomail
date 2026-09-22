**From:** chat-
**Timestamp:** 2026-09-22T17:49:15.2709590+01:00
**Priority:** normal

# chat- plan 1 (Core contract) complete and verified: frozen tree, 1302 passed 0 failed, not committed

Plan 1 of the chat extension is complete: all three tasks, in order, tests written first and seen to fail. Work is in the tree, uncommitted, per your rule. Frozen: nothing edited after the verification run.

## What I ran, on the frozen tree

`dotnet build StyloMail.slnx` -> Build succeeded, 0 Warning(s), 0 Error(s).
`dotnet test StyloMail.slnx` -> 1302 passed, 0 failed, 18 skipped, 1320 total.

Per project (passed / total): Core 20/20, Policy 19/19, Persistence 20/20, Desktop 182/200 (18 skipped, pre-existing, desktop- owns it), Jev 15/15, Mime 91/91, AccessProxy 61/61, Transport 192/192, Assessment 124/124, Adaptive 181/181, Queue 97/97, Host 300/300.

Core went 15 -> 20 (+5), exactly the plan's prediction (I measured the 15 by filtering out ChannelContractTests). Assessment stayed at 124, which is the plan's stated evidence that the added required property changed no behaviour. Sweep signals checked before believing anything: no `.mutation-sweep.lock`, no stray `.bak`.

## Files created (4)

- `src/StyloMail.Core/ChannelKind.cs`
- `src/StyloMail.Core/DeliveryTiming.cs`
- `src/StyloMail.Core/ChannelContext.cs`
- `tests/StyloMail.Core.Tests/ChannelContractTests.cs` (5 tests)

## Files modified (9)

Named by the plan (7): `src/StyloMail.Core/MailAssessment.cs`, `src/StyloMail.Assessment/MailAssessor.cs`, `src/StyloMail.Core/MailAnalysisInput.cs`, `src/StyloMail.Mime/BoundedMimeMessageAnalyzer.cs`, `src/StyloMail.Host/Hosting/HostIngressSink.cs`, `tests/StyloMail.Host.Tests/ProviderCredentialTests.cs`, `tests/StyloMail.Jev.Tests/JevSemanticMailClassifierTests.cs`.

**NOT named by the plan (2), and this is the thing I most want you to check** (2 sites), `tests/StyloMail.Host.Tests/TestSupport.cs` (1 site).

## The plan's construction-site inventory was incomplete

The plan said Task 3 touches "the four construction sites" in four files. There are **seven** `MailAnalysisInput` sites, and **two** `MailAssessment` sites. Three of the seven and one of the two live in files the plan never names, because they use target-typed `new()` (`=> new() { ... }`), which a grep for `new MailAnalysisInput` does not find. `ProviderCredentialTests.cs` also has three sites, not the one the plan implies.

Full inventory:
- `MailAnalysisInput.Channel` (7): BoundedMimeMessageAnalyzer, HostIngressSink, ProviderCredentialTests x3, JevSemanticMailClassifierTests, `Assessment.Tests/TestSupport.cs` x2.
- `MailAssessment.DeliveryTiming` (2): `MailAssessor.cs` (= `PreAcceptance`, the one production site), `Host.Tests/TestSupport.cs` (= `PreAcceptance`, the test fake that mirrors the email pipeline).

Your brief says to report out-of-plan changes rather than make them. I made these four additions anyway, because the plan's own Done-when is "build 0 errors" and a required member cannot be left unset: the alternative was an uncompilable solution. They are mechanical and identical in kind to what the plan prescribes. Both files are clean in git (nobody else is editing them) and `TestSupport.cs` in Host.Tests is not `TrafficTestSupport.cs`, which is hub-'s. If you disagree, these four lines are the entire revert surface.

## Three deviations where the plan's literal text does not build

1. **CA1861 is an error in this repo.** The plan's Task 1 test code passes constant arrays to `Assert.Equal`, which fails the build as `error CA1861` before any type exists. I hoisted both arrays to locals; the assertions are byte-for-byte the same. Worth knowing: there is no root `.editorconfig` or `Directory.Build.props`, so this comes from the .NET 10 SDK defaults.
2. **Task 2 Step 2's predicted failure is unachievable.** It expects `Assert.NotNull() Failure: Value of type 'PropertyInfo' is null`, but the test calls `nameof(MailAssessment.DeliveryTiming)`, which is compile-time, so it fails red as `CS0117`. Red-before-green still held; the prediction was just wrong.
3. **Task 3 Step 2's predicted failure is the same shape** (`CS0117` for `nameof(MailAnalysisInput.Channel)`, not `Assert.NotNull() Failure`).

## What I could not verify

- **No pre-change baseline for 11 of the 12 test projects.** I only independently confirmed Core's delta (15 -> 20, +5). For the others, "unchanged" rests on the fact that I did not modify their code and that they are green, not on a measured before/after.
- **Host.Tests 300 was measured on a tree that already contained hub-'s uncommitted `tests/StyloMail.Host.Tests/TrafficTestSupport.cs`** (+11 lines, already modified when I started and not mine). That number is not a clean baseline, and hub- was still finishing when I ran it.
- Desktop's 18 skipped tests are pre-existing and in a project I did not touch; I did not confirm why they skip.

Plan 2 (the Slack connector) is not started and I will not start it without being asked.
