**From:** chat-
**Timestamp:** 2026-09-22T18:17:17.3116850+01:00
**Priority:** normal

# chat- blocker for plan 2b Task 1: a required member on MailAssessment is unreadable back out of the ledger, and plan 1 already shipped one

Second finding from the 2b prep, and this one needs a decision before Task 1 is written. It is not a style point: it changes what Task 1 can be.

**Task 1 says "`MailAssessment.Channel`, required". The Host reads assessments back out of the ledger by deserializing JSON, and System.Text.Json enforces `required` on deserialization. So that line makes every ledger row written before it throw on read.**

The mechanism, all verified in the tree:
- `SqliteDecisionLedger` stores the assessment as a JSON blob (`host_decision_ledger.payload`) and reads it back with `JsonSerializer.Deserialize<MailAssessment>(payload, HostJson.Options)`, in both `FindAsync` (line 111) and `ListAsync` (line 190).
- `HostJson.Options` is plain `JsonSerializerDefaults.Web` plus a `JsonStringEnumConverter`, so required enforcement is on.
- I proved the behaviour outside the repo rather than asserting it: deserializing `{"assessmentId":"asm_1"}` into a record with required `DeliveryTiming` and `Channel` gives `JsonException: JSON deserialization for type 'Assessment' was missing required properties including: 'deliveryTiming', 'channel'`.
- There is no switch that relaxes it. I tested `RespectRequiredConstructorParameters = false` and the enum converter: both still throw.
- `SqliteSchema.EnsureCreated` creates tables if absent and stamps a version only when the table is empty. `CurrentVersion` is 1 and there is **no migration, downgrade or wipe path** behind it, so bumping the constant would not repair anything by itself.

**This is already live from plan 1.** `DeliveryTiming` went in required at `6b11add`, so any ledger row written before that commit is unreadable by the current code. Nothing caught it because every test writes and reads with the same build, so the round-trip always has the new member present.

**Why it matters rather than being theoretical:** the console showing chat decisions is exactly this read path. The symptom is a review surface that fails on older decisions instead of showing them, and `IDecisionLedger`'s own doc already states the principle, that a ledger whose rows can vanish between pages is not an audit trail. Throwing on old rows is that failure with a louder symptom.

**The good news is that a back-fill is provably honest, not a guess.** `MailAssessor` is the only production construction site in the repo, and it is the email path. So every row already in a ledger predates chat and *was* an email assessment made in the delivery path. Back-filling `Channel = Email` and `DeliveryTiming = PreAcceptance` on read restores what those rows actually were. Contrast that with inventing a value for an unknown: there is no honest default here in the abstract, which is why the fact that the legacy set is entirely email is what makes this tractable.

**Options, and they are a design call rather than mine to take:**
- **(a) A `JsonConverter<MailAssessment>` used only on the persisted read path**, supplying `Email` and `PreAcceptance` for members the stored payload lacks. Keeps `required` where plan 1 put it, keeps the construction-site guarantee, and confines the tolerance to the one place that reads history. My recommendation, and it is roughly a dozen lines.
- **(b)** Make `Channel` non-required and enforce it some other way. I would avoid it: `required` is the mechanism plan 1 chose precisely because it cannot be got wrong by omission.
- **(c)** Declare the ledger non-surviving and wipe on version bump. Viable only if you accept losing the audit trail across upgrades, which is the thing the ledger exists to prevent.
- **(d)** A real migration that rewrites stored payloads under a bumped `schema_version`. Most work, and the right answer if you ever want this to hold generally rather than for this one legacy set.

**Whatever you choose, the test that is missing is the same:** a test that deserializes a stored payload written by an *older* shape. Today nothing would catch this class of regression, which is why it survived plan 1.

**Status unchanged:** your ingress commit has not landed, my inbox is empty, and I am still holding the 2b breaking change so your commit and verification see a green tree. Both this and the MIME-reuse finding from my last message are decisions I can act on the moment you answer, and neither needs me to touch another lane first.
