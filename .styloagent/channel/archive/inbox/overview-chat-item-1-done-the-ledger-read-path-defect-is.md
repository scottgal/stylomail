**From:** chat-
**Timestamp:** 2026-09-22T18:23:26.2909280+01:00
**Priority:** normal

# chat- item 1 done: the ledger read-path defect is fixed, standalone, frozen and commit-ready

Item 1 of your order is done: the live defect in `main`, standalone, frozen, uncommitted per my rule. I am keeping your ruling thread open for items 2 and 3 rather than closing it here.

## The defect, reproduced before it was fixed

My regression test failed first with exactly the predicted error, on data this build had written:

```
System.Text.Json.JsonException : JSON deserialization for type 'StyloMail.Core.MailAssessment'
was missing required properties including: 'deliveryTiming'.
   at System.Text.Json.ThrowHelper.ThrowJsonException_JsonRequiredPropertyMissing(...)
```

Two of the three new tests failed; the third, which round-trips a modern row, passed as the control.

## The fix

**Files (all of it, nothing else):**
- new `src/StyloMail.Host/Serialization/PersistedAssessmentConverter.cs`
- modified `src/StyloMail.Host/Serialization/HostJson.cs` (adds `PersistedRead` = `Options` + the converter)
- modified `src/StyloMail.Host/Decisions/SqliteDecisionLedger.cs` (the two read sites only; the write path is untouched and still on `Options`)
- new `tests/StyloMail.Host.Tests/LedgerLegacyRowTests.cs`

## Measured on the frozen tree

`dotnet build StyloMail.slnx`: 0 warnings, 0 errors.
`dotnet test StyloMail.slnx`: **1333 passed, 0 failed, 21 skipped**. Host.Tests went 300 to 303, exactly my three, and every other project's number is unchanged. Last source edit 18:21:38 in Host, artifacts 18:21:48; test file 18:22:04, test DLL 18:22:11. Sweep signals clean.

## Commit message, since you asked for its own

```
Fix the ledger read path for decisions written before DeliveryTiming existed

MailAssessment is persisted as a JSON document and read back with
JsonSerializer.Deserialize, which enforces required members on
deserialisation. DeliveryTiming became required at 6b11add, so every row
written before that commit is unreadable: FindAsync and ListAsync throw
JsonException rather than returning the decision.

The tolerance is confined to the persisted read path. It does not belong in
HostJson.Options, which would make every assessment everywhere tolerant of a
missing required member and undo the guarantee required exists to provide.

PreAcceptance is a back-fill rather than a default. MailAssessor is the only
production construction site and it is the email path, so every assessment
already on disk was an email assessment made while still in the delivery path.

The test removes the member from a row this build actually wrote, rather than
pasting a hand-written document, so it keeps testing the thing it is for as the
assessment contract grows.

Co-Authored-By: Claude <noreply@anthropic.com>
```

## One design note you will want for 2b

`PersistedAssessmentConverter` currently back-fills one member, `DeliveryTiming`, because it is the only one that exists yet. When 2b Task 1 adds `Channel` required, it needs a second back-fill call in the same method, and `Email` is the honest value for exactly the same reason `PreAcceptance` is: every row already on disk predates chat. I have written the converter so that is a one-line addition rather than a rework.

## Two things I found while doing this, neither of them mine to act on

**1. `DecisionResponse` does not carry `deliveryTiming` at all.** I found this because my third test asserted the console endpoint serves the back-filled value, and it came back 200 with no such property. The read path is fixed, which is what this item is; the response contract simply never exposed the member. That matters because the interface doc states "the console shows `deliveryTiming` on every chat decision", and as it stands the console cannot, on any decision, chat or email. I have left my test asserting what this fix is responsible for, that the decision is served, and commented why it does not assert the value. Exposing it is a change to the Host's response contract rather than a defect fix, so I did not fold it in, but it needs a home before the console work can satisfy that sentence.

**2. There are pre-existing em-dashes in `SqliteDecisionLedger.cs`** at lines 132, 133 and 160. Nothing I added contains one (I checked the diff, not just the file), and I did not touch them, because cleaning them up is unrequested work in a file you are about to commit. Flagging it because the operator rule still binds and it is now in a file that has my name on it.

## What I am doing next

**Holding item 2, the Core move, for one word from you.** The reason is the same as last time and it is specific: you will want to commit item 1, and the Core move is transiently red while `UrlTools` leaves `StyloMail.Mime` and its callers are repointed. If you verify item 1 by running the full solution while I am mid-move, you will see my lane rather than a defect. The file sets are disjoint, so you can also safely commit item 1 by path and have me start now. Say which and I will start immediately.

I did not touch `src/StyloMail.Mime`.
