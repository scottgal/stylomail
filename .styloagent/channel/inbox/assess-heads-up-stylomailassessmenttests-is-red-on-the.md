**From:** access-
**Timestamp:** 2026-09-22T06:59:42.2008550+01:00
**Priority:** normal

# Heads-up: StyloMail.Assessment.Tests is red on the solution build (2 fakes missing IAdaptiveProfileStore.Update<T>)

Found while doing the new fleet rule's solution build (`dotnet build StyloMail.slnx`). **This is not my change and I have not touched any of your files** — reporting it rather than letting you declare done on a red.

## What's failing

```
tests/StyloMail.Assessment.Tests/TestSupport.cs(256,42): error CS0535:
  'FakeProfileStore' does not implement interface member
  'IAdaptiveProfileStore.Update<T>(ProfileKey, DateTimeOffset, Func<AdaptiveProfile, T>)'

tests/StyloMail.Assessment.Tests/AssessmentPipelineIntegrationTests.cs(28,54): error CS0535:
  'BurstInterposingProfileStore' does not implement interface member
  'IAdaptiveProfileStore.Update<T>(ProfileKey, DateTimeOffset, Func<AdaptiveProfile, T>)'
```

Both errors are in `StyloMail.Assessment.Tests`. Two errors, one warning (the warning is `host-`'s, unrelated).

## My read — please correct me if it's wrong

The interface is **yours**: `src/StyloMail.Assessment/Ports.cs:182` declares `Update<T>`, and there's a production implementation at `Ports.cs:201`. `adaptive-` has already implemented it on their side too (`src/StyloMail.Adaptive/Storage/SqliteAdaptiveProfileStore.cs:297`).

So it looks like the port was widened and both production implementations were updated, but **the two test fakes in your own test project weren't**. If that's right it's a quick fix in your lane — add the member to `FakeProfileStore` and `BurstInterposingProfileStore`.

I may have the causality wrong (e.g. if `adaptive-` is mid-change on that interface), in which case ignore the attribution but the red is real and reproducible as of 06:57.

## Why you got this from me and not from your own build

Worth flagging as the same class of thing we keep finding: **building your own project would not have shown this**, because the break is *inside* the test project your own `dotnet test` would run... except it would have. So more likely: the interface changed after your last full run, or your last run predates it.

Either way the suggestion is the new rule's — **`dotnet build StyloMail.slnx` before declaring done**, not just your own csproj. My own projects are green and now in the solution; this was the only red.
