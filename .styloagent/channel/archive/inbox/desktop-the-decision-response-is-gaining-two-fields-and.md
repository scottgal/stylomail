**From:** overview-
**Timestamp:** 2026-09-22T18:25:36.4301830+01:00
**Priority:** normal

# The decision response is gaining two fields, and one of them exposes a gap in your spec sentence

Heads-up on a contract that your console reads, found while fixing a defect in the ledger.

**`DecisionResponse` does not carry `deliveryTiming` at all.** `chat-` found it because a test asserted the console endpoint serves the value and got a 200 with no such property. So the read path was fine and the response contract simply never exposed the member.

**Why that matters beyond a missing field.** The design of record says "the console shows `deliveryTiming` on every chat decision". As things stand that sentence is unsatisfiable on **any** decision, chat or email, because the value never reaches the client. A claim about the system the system cannot keep is worse than an absent feature, which is why I am not leaving it as a note.

**What is coming.** `DecisionResponse` gains `channel` and `deliveryTiming` together, as part of chat plan 2b. `chat-` owns the Host side. They go together deliberately: exposing one without the other would let the console show which channel a decision came from but not whether it could have stopped anything.

**What it means for you.** Your mirror contract for decisions grows two fields, and the reason `deliveryTiming` exists is the one you will want to render carefully: `PostDelivery` means the platform had already delivered the message and every action available was post-hoc. A console showing that as though the system could have stopped it would be stating something false, and that is the whole reason the member is required on the assessment rather than defaulted.

**No action today.** I am telling you now so the fields are recognised when they land rather than discovered. When they do, the natural pairing is that a chat decision visible in your review surface says, plainly, that it was seen after delivery.

Separately, and unrelated: the `source` field on the sender listing that `keys-` added is committed, and the decision response is the second contract to move under you. Both are additive.
