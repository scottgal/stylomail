# The desktop half of the desktop test harness: design

**Approved by the operator, 2026-10-01T09:55:18+01:00**, quoted verbatim: "Approve the design; build the
console-seeded runner (makes the seam real)". That stamp is the approval note's own (`operator-`, answering
`desktop-` at 09:55:18); it is NOT 10:09:15, which is the stamp of `desktop-`'s reply record and was cited
as the approval time in `overview-`'s ruling (a) of 10:19:41.

**As built, 2026-10-01.** Section 6's runner is committed: `ux-scripts/run-console-corpus-smoke.sh` with
`ux-scripts/console-corpus-smoke.yaml`, in `745fa82` on main. Two as-built notes against section 6 as
written. (1) The route-level assertion is made by the corpus's own checker, `corpus.py check`, which reads
the ledger by the message's join key, while the console smoke asserts the same planted signal id on the
operator's screen: two instruments, one object, and the run is green only if both are. (2) The join key
reaches the manifest only because `console_seed_corpus` now passes `--resolve-join`, the one harness change
this work carried (`ux-scripts/console-harness.sh`). Apart from the status paragraph below, the body of this
document is the approved text unchanged.

**What the operator asked for, verbatim** (captured when the operator invoked the brainstorming
skill, and passed to me by `overview-` at 08:04): "Design a desktop client test harness for
StyloMail, which needs a spam/real-email corpus generator tool." The harness is the ask; the corpus
generator is named as a *need*, and it is already built and committed (`tools/corpus/`). That is the
re-scope this design answers: the missing half is not a generator but the runner that makes the
corpus-to-console seam real.

**Status: APPROVED 2026-10-01T09:55:18 and built.** This paragraph read "DRAFT, awaiting operator
approval... no code is written against this until the operator approves" when it was written, and rewording
it is the only body edit in this copy. The gate it names was `overview-`'s at 07:56, which `corpus-` also
held, and no code was written against this design before the operator approved it. Author
`desktop-`, 1 Oct 2026. Lane: `ux-scripts/` and `tests/StyloMail.Desktop.Tests/`. Charter: the
Avalonia operator console.

## 0. The seam, in one sentence

The console is an API client against the Host (spec §10.1) and has **no mail-ingestion path**: no
SMTP, no mailbox, no drop directory, no `.eml` reader. So the entire interface between the harness
and the corpus is **a batch directory handed to one command**, against a loopback Host the harness
started. The harness never parses a corpus file, which is what makes this a one-command boundary
rather than a shared format.

## 1. What already exists, measured

**The harness is `ux-scripts/console-harness.sh`, owned by this lane**, with 10 committed runners and
9 `console-*-smoke.yaml` scripts.

| switch | committed runners that set it |
|---|---|
| `CONSOLE_TRAFFIC` | 5 (`feed-drop`, `feed-recovery`, `long-outage`, `operator-retry`, `no-feed`) |
| `CONSOLE_CORPUS` | **0** |

`console_seed_corpus` is `console-harness.sh:1194`. `rg -l 'CONSOLE_CORPUS' ux-scripts` returns the
harness and its README and nothing else. `ux-scripts/README.md:372` states it plainly: "The switch is
wired but **no committed runner calls it yet**, and it has never been exercised end to end under a
smoke... A verified invocation is not a verified run."

So the corpus seam exists, its invocation is verified compatible with `corpus-`'s CLI and key file,
and nothing has ever run through it. **That gap, not a missing generator, is the piece the operator's
request is short of on this side.**

**The tests** are `tests/StyloMail.Desktop.Tests/` (30 files), including `DecisionListingTests.cs`,
`DecisionViewTests.cs`, `ListingTests.cs` and `LiveHostTests.cs`. They cover the row view models
against fixtures. None of them covers a row the Host produced from bytes a corpus planted.

## 2. What the render actually reads, and this decides the axes

Two lists, two routes, both in `Views/MainWindow.axaml`.

**Queues, `MessageList`** (`:366`), `ItemsSource="{Binding Messages}"`, filled from
`GET /v1/messages?state=…` (`Models/ShellModel.cs:588,596,604`, the three dispositions
`awaiting_decision`, `held`, `quarantined`). The row template (`:382-410`) renders exactly four
fields: `QueueId`, `RecipientsLabel`, `StateLabel`, `AttemptsLabel`. Its own comment (`:376-381`) says
why: "No subject and no sender column, because a queued message carries neither and empty columns are
a promise the console cannot keep." An Allow populates none of the three states.

**Review, `DecisionList`** (`:423`), `ItemsSource="{Binding Decisions}"`, filled from
`GET /v1/decisions` (`ShellModel.cs:655`). This is the ledger, and its own empty-state text is the
definition: "A decision is written when a message is assessed". The row template (`:432-494`) renders
`HeadlineReason`, `ActionLabel`, `AssessedAtLabel`, `RiskIndexLabel`, `InternalMessageId`,
`ShadowLabel` (only when `HasShadow`), `CoverageLabel` (only when `HasCoverage`).

The contract behind that row is `DecisionSummaryResponse`
(`Api/Contracts/DecisionListingContracts.cs:52-93`), whose complete field list is: `AssessmentId`,
`InternalMessageId`, `Action`, `ProposedActionInShadow`, `RiskIndex`, `Reasons`, `Versions`,
`Coverage`, `AssessedAt`.

**None of the rendered fields is a subject, a sender, a size, a message date, an attachment, a Cc, a
Reply-To, or an encoding.** That is an enumeration of both templates and both contracts, not a
sample.

## 3. What that means for the corpus design's proposed axes

`corpus-`'s design §2 closes with "Every one of those is a column a **list view** renders". At the
measurement that is false for three of the four, and the distinction is worth having before three
axes are built:

- **Size** (461 to 1545 bytes): no column anywhere in the console renders a byte size.
- **Date**: the ledger row renders `decision.AssessedAt` (`ShellModel.cs:1426`), the **Host's
  assessment time**, not the message's `Date` header. So the constant `Date` at `corpus.py:268` does
  not collapse the rendered sort. Whether the Host ever makes the two equal is a Host question and is
  not measured here.
- **Headers and encodings**: no column renders any of them.

Where the MIME and header axes **do** land is one level down, in the decision the row summarises:
`HeadlineReason` is "the first reason in policy's own words", and `CoverageLabel` counts coverage
flags. An attachment whose declared type lies plants `attachment_type_mismatch` and surfaces as a
reason. So the axes are read **through the decision**, not through a column. That is the answer to
"which one is read": the axis worth building is the one that moves a reason or a coverage flag.

The one axis with a consumer the harness already asserts on is **state**, because state is what
populates the Queues list. Both runners that reach a populated row today (`run-console-quarantine-smoke.sh`,
`run-console-nimble-smoke.sh`) set `CONSOLE_PROVIDER=nimble`, so a state axis needs a working
assessor, not a laxer corpus.

**No assertion in this design needs a floor on the Queues list, and that is deliberate.**
`corpus-` raised the risk and is right to: `RejectedAtIntake` (422) populates none of the three
dispositions, so a populated `/v1/messages` depends entirely on how many messages the model happens
to hold or quarantine. That makes a minimum row count there an operator-visible risk rather than a
corpus parameter, and it should never be written as one. The Queues assertion here is "a state was
reached", which is zero or more; the count-bearing assertion lives on `/v1/decisions`, which has no
floor because every assessed message is a row.

## 4. The join key, which is what makes this buildable

`DecisionSummaryResponse.InternalMessageId` is documented (`:56-57`) as "The message this decision is
about. **The join key from a message row.**" `corpus-`'s `seed` writes
`manifest.messages[i].seeded.internalMessageId`. If those two are the same string, a planted fact is
joinable to the exact rendered row: plant a fact in message `i`, find the ledger row whose
`InternalMessageId` equals `seeded.internalMessageId` for `i`, and assert that row's
`Reasons[*].EvidenceSignalIds` contains the planted fact's signal id (§6 step 3).

**That identity holds, and it was read at source rather than derived.** Both branches descend from
one local minted once per submission at `Host/Endpoints/SubmissionsEndpoints.cs:130`
(`msg_{Guid.NewGuid():N}`), which `MessageIngress.Prepare` puts on the envelope
(`MessageIngress.cs:84`). The branch `seed` records is `SubmissionResponse.cs:116`; the branch the
ledger serves goes through the queue store (`QueueStore.cs:1449` write to `internal_message_id`,
`:1746` `reader.GetString(2)`, `:1775` back onto the envelope) to `MailAssessor.cs:524`,
`SqliteDecisionLedger.cs:60` and `DecisionListingResponse.cs:109`. No hop re-mints or re-formats the
value.

**Scope: that is a source read, not a runtime observation.** Two things remain unobserved and both
want a build window: that the assessor path is taken for a message at all (it needs a working
provider, so a corpus-seeded runner needs `CONSOLE_PROVIDER=nimble`, as `run-console-quarantine-smoke.sh`
already does), and that the real queue database returns what the column read promises. **Do not let
the runner rest on the store round-trip without one runtime confirmation.** If it failed, the
fallback is joining on assessment order or on `AssessedAt`, which is weaker and should be said to be
weaker rather than used quietly.

**Two scope lines on that chain, each of which would let an assertion pass for the wrong reason**
(`corpus-`, from a census of all 45 `InternalMessageId` occurrences in `src`):

- **The chain above is the EMAIL path only.** `src/StyloMail.Assessment/ChatAssessor.cs:130` sets the
  same field from `input.EventId` rather than from the envelope, so a chat-driven assessment joins on
  a different value entirely. The corpus cannot reach that surface, since `seed` posts
  `POST /v1/submissions`. But a runner written from this chain would look right and join nothing if it
  ever drove the chat surface, so the boundary is written here rather than met later.
- **The id's prefix names the mint site**: `msg_` at `SubmissionsEndpoints.cs:130`, `cli_` at
  `src/StyloMail.Host/Cli/CliCommands.cs:401`. So "did this row come from the corpus rather than from
  a CLI seeding path" is readable off the id rather than inferred, which is a cheap and strong
  assertion for the runner: a corpus-seeded row's `InternalMessageId` starts `msg_`.

## 5. The contract this half holds

1. **One command in**: a batch directory passed to `seed --base-url --key-file --batch`. No drop
   directory, no `.eml` reader, no transport acquired on this side. `corpus-`'s §6 boundary and mine
   agree.
2. **`seed` records `seeded.state` and `seeded.internalMessageId` per message.** Both exist today.
3. **Never promise an action or a state** (architecture decision 27). Rows are selected by measured
   state, never by index.
4. **Reproducible from a recorded seed**, with `seed`, `profile`, `count` and `coverage` in the run
   log, so a re-run next week is the same corpus or a visible difference.
5. **Refuse rather than fake**: unset `--coverage`, unknown profile, a manifest entry escaping the
   batch directory.

## 6. The missing runner, which is the deliverable

`ux-scripts/run-console-corpus-smoke.sh`, modelled on `run-console-quarantine-smoke.sh` because a
populated state needs `CONSOLE_PROVIDER=nimble`:

1. `generate` a batch into the run directory from a fixed seed.
2. `export CONSOLE_CORPUS` to that directory, leaving `console_seed_corpus` to load the Host.
3. Drive the console and assert two things, neither of which needs a predicted outcome: that the
   ledger lists a row per message, and that the row whose `InternalMessageId` equals a planted
   message's `seeded.internalMessageId` carries that fact's **signal id** in the union of its
   `Reasons[*].EvidenceSignalIds`. **A fact's twin is a signal id, never a reason code.** The eight
   plantable deterministic facts *are* the Host's own signal ids
   (`tools/corpus/corpus.py:87-126`); at the site where policy refuses on one,
   `MailPolicyEngine.cs:167-184` builds the reason with `EvidenceSignalIds = refusing`, the list of
   `e.SignalId` that `DeterministicFindings.Establishes` accepted, and `DecisionResponse.cs:184`
   relays that list verbatim. So assert membership of the planted signal id, anywhere in the list, so
   ranking does not matter. **Never match on `HeadlineReason`**: it is `Reasons[0].Message`
   (`ShellModel.cs:1423`), policy's own wording, which moves when policy does.
   A reason **code** is matched only when the assertion is about *policy* rather than about the
   planted fact: six of the eight facts are weighted findings (`DeterministicFindings.cs:72-83`),
   three are the Host's declared `Refusing` set (`:108-114`), and a refusal carries
   `policy.refusing_finding` (`MailPolicyEngine.cs:164`). That route reaches a Held row **by source
   rather than by declared intent**: `MailPolicyEngine.cs:159` returns `Hold(...)` when
   `refusing.Count > 0`, so what stays open is only whether a seeded MIME actually establishes the
   fact and whether an earlier tier returns first (`corpus-`, who corrected their own hedge to say
   this). Two of the eight (`link_idn`,
   `authentication_provenance`) are deliberately unweighted (`:37-50`), so no reason or index
   assertion may rest on either.
   **Retraction.** An earlier draft of this step asserted a fact-id-to-reason-**code** mapping,
   reading `DecisionListingTests.cs:106` as evidence that a fact id doubles as a code. That was
   wrong. That line is a hand-written wire fixture, `tests/StyloMail.Desktop.Tests/Wire.cs:46`, which
   spells `"code": "link_display_mismatch"` beside `"evidenceSignalIds": ["sig_link"]`; it is a
   client fixture, not Host output, and `rg -F '"link_display_mismatch"' src` returns nothing.
   **The needle is the quoted literal**, a code position in JSON. The bare word has three hits
   (`ChatSignals.cs:16`, `MimeSignals.cs:52`, `DeterministicFindings.cs:60`), all the C# const
   `LinkDisplayMismatch` whose value is the signal id; a different population, and not a code. A
   reader running the unquoted pattern must not read that `3` as a contradiction of this zero.
   (`corpus-`, who found the original error and then bounded the zero.)
4. Write `result.json` and screenshots like every other runner, so it is one of the set rather than a
   special case.

The split of assertions follows §2: **`/v1/decisions` for "the batch is visible", `/v1/messages` for
"a state was reached".** Those are two questions and one route cannot answer both.

## 7. Out of scope

- **No transport, ever.** The console has no ingestion path, and giving it one is a different
  subsystem with a different owner (spec §9, the access proxy), not a laxer harness.
- **No model-authored bodies** (spec §14.4.3).
- **No new .NET project.**
- **`CoveredWeightFraction`** stays blocked on the Host's carrier and is untouched by this design.

## 8. What the operator must decide

- Whether a corpus-seeded console smoke is wanted, and whether it **gates** or is on demand.
- The batch size and profile mix a gating run may carry, given it needs a working assessor.
- Whether the design is committed as a document beside `ux-scripts/README.md` once approved.
