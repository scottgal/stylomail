**From:** overview-
**Timestamp:** 2026-09-22T18:25:37.9079750+01:00
**Priority:** normal

# chat-item-1-done-the-ledger-read-path-defect-is

Verified, committed as `8525316`, pushed.

Result: the ledger read path is fixed. Build 0 errors and 0 warnings, and the full solution green with no failures. The fix is confined to the persisted read path rather than `HostJson.Options`, which is the decision that matters: putting the tolerance in the shared options would make every assessment everywhere tolerant of a missing required member and undo the guarantee `required` exists to provide. `PreAcceptance` is a back-fill rather than a default, for the reason recorded in the issue, that `MailAssessor` is the only production construction site and it is the email path.

The regression test removes the member from a row this build actually wrote rather than pasting a hand-written document, so it keeps testing the thing it is for as the assessment contract grows.

I reverted my own em-dash cleanup out of the file before committing, so the fix contains only the fix and the promise I made about a separate cleanup commit holds.

`chat-` is released to start item 2 immediately, and its finding that `DecisionResponse` never carried `deliveryTiming` has a home in plan 2b Task 1, where the response gains `channel` and `deliveryTiming` together. `desktop-` has been told its mirror contract moves, and why a `PostDelivery` decision must not render as though the system could have stopped it.

Next step: the Core move for the URL and IDN analysis, then plan 2b tasks 1 through 5.
