# tools/corpus

A generator of seeded, reproducible mail with a manifest of the deterministic facts planted in it.
It exists so the console's harness can populate states on demand and so the fleet can measure the
pipeline against traffic whose properties are known rather than guessed.

## What is here

| file | what it is |
|---|---|
| `corpus.py` | the generator, seeder and checker. Python 3, standard library only, run as a file. |
| `measure_reachability.py` | the first deliverable, before the generator existed: fixed-literal messages posted to measure which states a Host can actually reach. Not needed to use the corpus. It is the origin of the reachability facts below, but not of all of them: see the provenance note under that section. |

Run `corpus.py` from the repository root. It has no dependencies and writes nothing outside the
directory you name.

## Running it

```sh
# 1. write a batch: NNN.eml files plus manifest.json
python3 tools/corpus/corpus.py generate --seed 1234 --count 20 --out .styloagent/scratch/batch --profile mixed

# 2. post it through the Host's authenticated routes and record what each message became
python3 tools/corpus/corpus.py seed \
    --base-url http://127.0.0.1:5271 \
    --key-file .styloagent/scratch/batch/principal.key \
    --batch .styloagent/scratch/batch \
    --resolve-join

# 3. read the decisions back and compare them against the planted facts
python3 tools/corpus/corpus.py check \
    --base-url http://127.0.0.1:5271 \
    --key-file .styloagent/scratch/batch/principal.key \
    --manifest .styloagent/scratch/batch/manifest.json
```

`--key-file` is a **path to a file**, read in-process. A key is never an argument, never printed and
never written into a manifest: the only thing this tool says about a key is whether the file it was
given could be read.

A manifest's `file` entry is resolved and required to stay **inside** the batch directory. A manifest
can arrive from outside the operator's control (`ingest` reconstitutes one from an operator dataset,
and a manifest is a text file anyone can edit), so an entry like `../../something` is refused and the
whole batch with it, rather than read and posted to the Host. A file that is absent but *inside* the
batch is a different case and is skipped per message.

Profiles: `benign` (ordinary mail whose checkable properties must come back absent), `phishing`,
`mixed` (both, plus an envelope violation), `quarantine` (threshold-targeted, see below), and the
paired fixture as `pair` (a baseline then a second turn carrying it as a window) and `pair-control`
(the same pair with the change removed).

`--coverage full` supplies authentication provenance and a connecting IP; `reduced` omits both on
purpose. There is no third value: a batch whose coverage was an accident cannot be told from one
whose coverage was planted, and only one of those is checkable, so `generate` refuses to default it.

## The manifest schema, version 3

This is the interface. It is versioned because a consumer that reads `corpusVersion` can refuse a
shape it does not know rather than reading the fields it recognises and missing the ones it does not.
The example below is the current shape; the two changelog paragraphs are kept in order so a reader
can see what each number meant when it was emitted.

Version 2 replaced `expected` the integer with `expected` the predicate, added per-fact `controlFor`,
and made the `submission` object carry the conversation window. A version 1 consumer reading a
version 2 manifest would silently misread every `expected`, which is why the number changed.

Version 3 changed what a conversation window **contains** and added the two counts that make an
over-long fixture visible. Under version 2 the window was the prior turn's body text alone; under
version 3 it is the prior turn's **raw message as the Host received it**, headers included, because
`conversation-` measured that the continuity answer turns on the rendered turn shape, so a
bodies-only window asks a different question from the one the Host asks. Version 3 also added
`turnCharacters` and `windowCharacters` to every message. The window change is why the number moved
rather than staying: a consumer that compared window contents, or that measured a window's size to
attribute a provider refusal, would read a different thing under the same number.

```json
{
  "corpusVersion": 3,
  "generatedBy": "tools/corpus/corpus.py",
  "seed": 1234,
  "profile": "mixed",
  "coverage": "full",
  "authoredByModel": false,
  "source": null,
  "batchNote": "…",
  "messages": [
    {
      "index": 0,
      "file": "000.eml",
      "sha256": "…",
      "coverage": "full",
      "coverageReason": null,
      "submission": {
        "direction": "Inbound",
        "mailFrom": "colleague@example.test",
        "rcptTo": ["recipient@example.test"],
        "connectingIp": "203.0.113.10",
        "authenticationResults": [ { "mechanism": "spf", "result": "pass", "fromTrustedVerifier": true } ]
      },
      "thresholdTargeted": false,
      "turn": null,
      "turnCharacters": 281,
      "windowCharacters": 0,
      "intent": { "label": "ordinary correspondence", "note": "…" },
      "planted": [
        {
          "id": "deterministic.link_display_mismatch",
          "where": "mime",
          "site": "html body: the <a> href host disagrees with the link text host",
          "turn": null,
          "expected": { "available": true, "value": 0 }
        }
      ],
      "coverageDropped": [],
      "seeded": {
        "httpStatus": 202,
        "queueId": "…",
        "internalMessageId": "…",
        "action": "Hold",
        "state": "Held"
      }
    }
  ]
}
```

### The two kinds of statement, and why they are two fields

`planted` and `intent` are different kinds of statement and are never one field (architecture
decision 27).

- **`planted`** is a deterministic property really put in the message, listed with the site it was
  put at. It is checkable, which makes the corpus an oracle for the deterministic layer: if the fact
  is in the message and the findings do not report it, that is a defect and `check` names it and
  points at where it is.
- **`intent`** is what the message was meant to be. A judgement. It is a denominator and a
  distribution: never evidence, never a policy input, never authority to act, and a rate computed
  from it is quoted with the corpus named rather than as accuracy.

### `expected` is a predicate, not an integer

Four shapes, because four kinds of claim are real and an integer can carry only the first:

| predicate | the claim it makes |
|---|---|
| `{"value": 1}` | a firing dimension, or a count (trusted authentication failures report 3, not 1) |
| `{"available": true, "value": 0}` | present and **not** firing: a benign message's whole claim |
| `{"available": true, "origin": "Deterministic"}` | provenance rather than a value |
| `{"available": true \| false}` | whether the signal was computable at all (what a window decides) |

A boolean cannot say "present and not firing", and it cannot say "the claim is provenance, not a
value". Both of those are real, and both appear in a single batch.

### `controlFor`

A fixture that only plants what it expects cannot tell detection from a detector that fires on
everything. `controlFor` names, on the entry that must **not** fire, the fact whose positive
expectation it protects. It is checked by the same code as any other fact, because a control checked
by a private path would not constrain the thing it was meant to constrain.

The envelope fixture is the worked example: `envelope.no_recipients` is the violation, and
`envelope.refusal_attributable_to_the_envelope` is the control asserting that no `policy.risk_above_*`
reason contributed.

An authentication control was tried in that fixture first and **removed after measurement**: a
message refused at intake never reaches the deterministic layer, so a fact planted in its
authentication results has no evidence row to be checked against. `check` reported exactly that. The
control has to be something the refusal leaves behind, and reason codes are that.

### The rule for any fixture in a refused message

**A message that will be refused at intake may only plant `envelope.*` or `where: "reasons"` facts.**
Intake is decided before the deterministic layer runs, so a `mime` or `submission` fact planted in
such a message can never be satisfied: there are no evidence rows to satisfy it. This is measured,
not inferred. It is a rule and not a note because the mistake is invisible at authoring time: the
manifest looks right, the message is genuinely carrying the property, and only `check` finds out.

### `turn`, `conversationContext` and `undescribedChange`

`turn` is null for an unpaired message and 1 or 2 for a paired one. `submission.conversationContext`
is the wire shape (`IReadOnlyList<string>`, `src/StyloMail.Host/Contracts/AssessmentRequest.cs:44`),
omitted when empty.

**What a window entry is.** The prior turn's raw message as the Host received it, headers included,
not the body alone. This is a measured requirement, not a preference: `conversation-` measured that
the continuity answer depends on the rendered turn shape, so a bodies-only window asks a different
question from the one the Host asks. The window is resolved the moment the preceding message's bytes
exist, which means an odd batch that declares a window with no preceding message is **refused**
rather than written windowless: a windowless turn 2 would silently measure the other question.

**Both second turns must be forward-moving.** `conversation-` measured the continuity axis as
answering A when the body's content is **contained** in the supplied window and B when it
**advances** past it, regardless of length. So a second turn that restates the first answers A for a
structural reason that has nothing to do with the change under test, and the pair measures the axis
instead of the change. The control here was rewritten for exactly that reason: it previously restated
the window's own bank details, and now advances (approval, payment run, remittance advice) while
touching the payment destination not at all.

`turnCharacters` and `windowCharacters` are the sizes of the strings actually sent. They exist so a
provider refusal is attributable to the **fixture** rather than to the pipeline: the adapter truncates
a turn at 2,000 characters, and the rendered prompt is checked against `NumCtx` as UTF-8 bytes and
refused outright rather than truncated, so an over-long fixture is a corpus defect and has to be
visible in the manifest.

The pair declares `semantic.conversational_continuity` as **availability only**. Supplying a window
is something this tool does, so its availability is the corpus's to claim. The value is the model's
judgement and the corpus does not declare it.

The *change* in a pair is recorded in `undescribedChange` and deliberately **not** in `planted`. No
conversation signal id exists in the tree yet, so a `planted` entry would be a claim nothing can
report and `check` would fail a correct pipeline for it. The field says what the change is, where it
is, and why it is not assertable. A `planted` list is a list of promises; this one is not made.

### The rest

`where` is `"mime"`, `"submission"` or `"reasons"`. It says where the pipeline reads the fact from,
because several plantable facts live in the request rather than in the bytes: two messages whose
MIME hashes identically can differ in whether the fact is planted, and the manifest says which.

`thresholdTargeted` marks a fixture authored to reach a policy threshold rather than to be
representative. Wording that reaches quarantine was arrived at by looking at the model's answers, so
it reaches the threshold partly by fitting that model: it is valid for **populating** the state and
must never be quoted as a detection rate.

`seed`, `generatedBy` and the per-message `sha256` make a batch reproducible: the same seed and index
give byte-identical bytes in any process, on any machine. Determinism is measured, not asserted.

`authoredByModel` is `false` for every batch this tool writes. A corpus written by the model it is
then measured on would measure self-consistency rather than detection, so such a batch would have to
be labelled here and reported apart. Nothing here produces one.

`source` is `null` for a generated batch. A reconstituted batch names the archive and its sha256,
and its messages carry an explicit **empty** `planted` list rather than omitting the field, because
an absent field and an empty list read the same to a consumer and mean different things.

`seeded` is written by the `seed` verb and by nothing else. It carries the queue id, the join key and
the state the Host returned, so a later run can join a message to its decision without re-deriving
it. The join key is `internalMessageId`: the decision summary carries no queue id, so that value is
what `GET /v1/decisions?messageId=` takes.

## What a batch can and cannot reach

Measured on a throwaway loopback Host against a local provider, 30 Sep and 1 Oct 2026. These are
results, not expectations.

Which instrument produced which line, because a number with no way back to its run is a number you
have to take on trust:

| measurement | instrument | artifact |
|---|---|---|
| the ladder, quarantine reachability, the dead-end campaign dimension, the release path, an Allow populating no listing | `tools/corpus/measure_reachability.py` | its `--out` JSON |
| the durable-route mechanism and the gate in both directions | `.styloagent/scratch/corpus/probe_gate.py` | `probe-gate/run.txt`, `probe-gate/result.json` |
| the window flipping continuity to Available, measured on the pair **and** its control | `.styloagent/scratch/corpus/probe_pair.py` | `probe-pair/both-profiles.json` |
| the v2 batch table and the reconstituted `emails.zip` result | `.styloagent/scratch/corpus/run_batch.py` | `scratch/corpus/logs/` |

- **The durable route is the only route whose evidence reaches the decision.** `seed` therefore uses
  `POST /v1/submissions`. The mechanism is one step further in than it looks, and an earlier version
  of this file stated it wrongly: the MIME analyser **does** run on both routes
  (`Hosting/MessageIngress.cs:98`, shared by both endpoints) and `Prepare` **discards** its evidence,
  returning only `result.Message` (`:118`). What differs is that `MailAssessor` resolves the original
  bytes itself through `IRawMessageSource` (`MailAssessor.cs:262`), and when they resolve it adds the
  parsed evidence (`:266-275`); when they do not, the else at `:292-295` adds
  `assessment.deterministic_extraction` instead. `SpoolRawMessageSource.TryGetAsync` returns null for
  any non-durable reference (`Assessment/Ports.cs:73-76`), and the assessment-only endpoint passes
  `PayloadReferences.Ephemeral` (`Endpoints/AssessmentsEndpoints.cs:64`).

  So, stated properly: **the assessor's only route to the original bytes is the spool, and the
  assessment-only route deliberately does not spool. A planted MIME fact can therefore never appear
  as a finding there.**

  **This is a corroboration, not a discovery, and the credit is not this tool's.** The fact that the
  route cannot corroborate an allow has been in `.styloagent/architecture.md` (composition-root
  component) since **30 Sep 2026**, measured by `conversation-`'s 5-arm run: a benign message at
  index 0.0000 came back `Hold` with `policy.allow_uncorroborated_by_deterministic_evidence`, on all
  15 assessments. What the probe above adds is an independent re-measurement from a different
  fixture, and what the paragraph above adds is the source-level cause, which the architecture text
  did not have (it says the deterministic layer "never runs there", which is not the mechanism). The
  reason code is also worth naming: it reads as a claim about the message when on this route it is a
  claim about the route.
- **That difference is the corroboration gate, measured in both directions** from identical bytes
  (`.styloagent/scratch/corpus/probe_gate.py`, which writes its own `probe-gate/run.txt` transcript
  and `probe-gate/result.json`): the same benign message is `Allow` on `/v1/submissions` with 14
  deterministic `Available` rows, and `Hold` on `/v1/assessments` with
  `policy.allow_uncorroborated_by_deterministic_evidence` and **zero** deterministic rows.
- **A queue row and a ledger entry are reachable with no provider credential.** Every message that
  passes envelope validation answers 202 with `{queueId, status, assessmentId, recipients[…]}`.
- **An `Allow` populates no console listing.** `GET /v1/messages` enumerates only
  `awaiting_decision`, `held` and `quarantined`, and the queue deliberately has no `queued` filter.
  So "a populated queue" for acceptance means a Hold or a Quarantine, not merely a 202.
- **Quarantine is reachable by traffic**, index 0.9315 on the `quarantine` profile with a local
  provider. The lever no other fixture reached was `payment_redirection`, triggered by an explicitly
  **changed** payment destination. This is a **reachability** proof, not a prevalence claim: 6.8 of
  7.3 with nearly every dimension firing is the vehemence shape, and nobody should read 0.9315 as
  "typical invoice fraud scores 0.93".
- **A window makes the continuity dimension answerable; it does not make it answer.** Measured on
  the paired fixture: turn 1 with no window reports `NotApplicable` / `null`; turn 2 with the window
  reports `Available` / origin `Semantic`, on **both** the changed pair and the control. The
  availability flip is the corpus's claim.
  **What the value does changed at version 3, and it is recorded as a change, not as a correction.**
  Under version 2 the window was turn 1's body text and both arms reported `0`. Under version 3 the
  window is turn 1's raw message as received, and the arms are no longer the same: the changed pair
  reports `0`, the control reports `1`. So the window's **content is load-bearing for the answer**,
  which is itself the result worth having, and a consumer comparing continuity values across corpus
  versions is comparing answers to different questions.
  **Not claimed: what `0` and `1` mean.** `conversation-` labels the axis A (the body's content is
  contained in the supplied window) and B (it advances past it); which integer is which is theirs to
  say and this tool does not guess it. Reported alongside so it can be checked rather than assumed:
  the changed turn shares 6 long tokens with turn 1 (it cites the details it supersedes), the control
  shares 2 (`invoice`, `payment`, topical to the subject).
- **The risk index is a weighted mean over semantic weight only.** `campaign.*` and `behavioural.*`
  evidence contributes to neither numerator nor denominator, so history can make unweighted evidence
  Available and still move the score by nothing. A batch of near-duplicate messages from one sender
  scored identically before and after the campaign dimension appeared.
- **`seed` refuses to report success when nothing was accepted.** An under-count that reports success
  is the defect this tool exists to catch, so a run with zero accepted messages exits non-zero.

## Check semantics

`check` reads each message's decision back from the ledger and, for every planted fact, compares the
**predicate**, not the row's presence:

- a deterministic row can be `Available` with value `0` when the property is absent, so "the row is
  there" is not the same assertion as "the fact was planted";
- an envelope fact is not a risk finding at all: it is verified before the semantic provider runs, so
  it is checked through the action (`Reject`) and the reason code (`policy.verified_violation`);
- a `reasons` fact is checked against the reason codes and **before** the evidence, because the
  message it is about is one that has no evidence rows at all;
- a row that comes back `NotApplicable` fails an availability claim of `true`, and satisfies an
  availability claim of `false`.

It exits non-zero and lists every missing fact with its site. A planted fact is in the message by
construction: if the findings do not report it, that is the evidence, not a tolerance.

## The operator datasets

`emails.zip` and `spamham.zip` live **outside** the repository and are read where they lie, named by
`STYLOMAIL_CORPUS_DIR`. Nothing copies them into the tree, and when the directory is unset the
ingest **skips honestly** (exit 2) rather than passing quietly. There is no default path into a home
directory.

`ingest` reconstitutes `emails.zip` rows into raw MIME:

```sh
STYLOMAIL_CORPUS_DIR=/path/to/datasets python3 tools/corpus/corpus.py ingest \
    --source-archive emails.zip --limit 500 --out .styloagent/scratch/reconstituted
```

The body and subject are the source's; the envelope, headers and MIME structure are this tool's, so a
header-derived finding is a finding about this template rather than about the source. No derived
column of the source (`label`, `phishing_probability`, `spf_status`, `dkim_status`, `dmarc_status`,
`urgency_score`) is read into any pipeline input, and no authentication result is synthesised:
fabricating one would assert the source's own `spf_status` and `dkim_status` columns back to the
pipeline through this tool's template. The source's label is carried as corpus intent and nowhere
else.

`spamham.zip` is **not** reconstituted. It is a short-text corpus with no mail headers, so adopting
it would produce a corpus whose shape is not the shape it claims to be. Its only honest use is as a
short-text control, reported with its dropped-row count.

## Where this stands

Built 30 Sep 2026 against the manifest shape confirmed by `overview-`; **corpusVersion 2** and its
measurements on 1 Oct 2026, superseded the same day by **corpusVersion 3** (window content and the
two character counts; see the changelog above). The `corpusVersion` here versions this tool's
manifest and what a conversation window contains. It is not the decision-ledger schema version,
which moves independently and is owned by the Host.

- `generate`, `seed`, `check` and `ingest` are implemented. Determinism is verified across separate
  processes: two runs into two directories are byte-identical, `manifest.json` included, re-verified
  after every fixture edit.
- Measured states at **corpusVersion 3** on a Nimble-backed throwaway Host, all with `check`
  reporting **OK: every planted fact was reported as planted** and the seed and check exits at 0:

  | batch | result |
  |---|---|
  | `mixed --count 8 --coverage full` | 6 accepted, 2 refused 422 at intake, Allow 5 / RejectedAtIntake 2 / Hold 1; `?state=held` 1 row, `?state=awaiting_decision` 1 row |
  | `quarantine --count 2 --coverage full` | 2 accepted, 2 Quarantine; `?state=quarantined` 2 rows, `?state=awaiting_decision` 2 rows |
  | `phishing --count 2 --coverage reduced` | 2 accepted, 1 Allow + 1 Hold; `?state=held` 1 row, `?state=awaiting_decision` 1 row |
  | `pair --count 4 --coverage full` | 4 accepted, all Allow, every listing 0 rows |
  | `pair-control --count 4 --coverage full` | 4 accepted, all Allow, every listing 0 rows |

  Every batch above ran on a loopback Host on a port chosen free per run, so no port is recorded
  here: a number in this table would be wrong on the next run. The port in use is printed by
  `run_batch.py` and by `probe_pair.py`, and is what a report should carry.

  `awaiting_decision` is a superset of `held` and `quarantined` here, not a third bucket: the three
  profiles above read 1/1/0, 2/0/2 and 1/1/0 for `awaiting_decision`/`held`/`quarantined`, which is
  only consistent if it counts the undecided rows of both states. A row that reads 0 in a state the
  decision never reaches is the point, not an omission: an Allow populates no listing, so the pair's
  zero rows are how the control stays quiet.

  The pair rows carry the one number that moved at version 3 and is not explained here: the
  `semantic.conversational_continuity` row reads **0** for `pair` and **1** for `pair-control`, where
  under version 2 both read `0.0`. This tool records the reading and declines to interpret which end
  is A and which is B; that mapping is `conversation-`'s to state (see the window section above).

- **The invocation the console harness uses is verified compatible**: `ux-scripts/console-harness.sh`
  `console_seed_corpus` calls `seed --base-url … --key-file … --batch …`, which is this tool's
  signature, and writes its principal key as a bare hex string, which is what this tool's reader
  takes. The file carries the executable bit, so the harness's direct-invocation branch works as
  well as its `python3` fallback.
- **Reconstituted `emails.zip` mail does not populate the queue**: 40 rows, all 40 Allow, every
  listing 0 rows. Reduced coverage plus a local model scores below the allow threshold. That batch
  measures agreement; the synthetic profiles are what populate a console state.
