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
python3 tools/corpus/corpus.py generate --seed 1234 --count 20 --out scratch/batch --profile mixed

# a populated, varied ledger for the console: 24 messages, both shape axes drawn per message
python3 tools/corpus/corpus.py generate --seed 1234 --count 24 --out scratch/mailbox \
    --profile mailbox --coverage full --encoding-mix mixed --size-mix mixed

# 2. post it through the Host's authenticated routes and record what each message became
python3 tools/corpus/corpus.py seed \
    --base-url http://127.0.0.1:5271 \
    --key-file scratch/batch/principal.key \
    --batch scratch/batch \
    --resolve-join

# 3. read the decisions back and compare them against the planted facts
python3 tools/corpus/corpus.py check \
    --base-url http://127.0.0.1:5271 \
    --key-file scratch/batch/principal.key \
    --manifest scratch/batch/manifest.json
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
`mixed` (both, plus an envelope violation), `quarantine` (threshold-targeted, see below), and two
paired fixtures that test different layers:

- `pair` / `pair-control`. A baseline, then a second turn carrying it as a **conversation window**.
  The control is the same pair with the change removed. This is the conversational axis.
- `template-variant` / `template-variant-control`. The same message twice, with the payment
  destination swapped in the second copy. The control is the same pair with the destination left
  alone, which makes turn 2 byte-identical to turn 1. No conversation window is supplied: this is
  the **campaign** axis, and the comparison is against the Host's recent-message store, not against
  `conversationContext`.

The two pairs are not variants of each other and the difference is measured, not stylistic. `pair`'s
second turn **rewrites** its first, and rewriting drops the wording similarity below the campaign
detector's gate, so `pair` reaches `campaign.near_duplicate` and **never**
`campaign.security_bearing_variant`. `template-variant`'s second turn **reuses** its first, which is
the shape that reaches the variant. See "The two layers a changed destination can reach" below.

A third profile is the one a console harness actually runs:

- `mailbox` (recommended count 24). A batch whose point is a **populated, varied ledger**: 1
  quarantine-shaped message, 3 risk-shaped, 2 envelope violations and 18 benign. The composition is
  positional and stated as data in `MAILBOX_COMPOSITION`. The counts are TARGETS, not promises, and
  the word matters: decision 27 forbids a planted fact from naming a tier, an action or an expected
  outcome, so this profile supplies messages and the run **measures** what each became. Nothing in the
  manifest says "this message will be Held"; a harness selects rows by `seeded.state`. That absence is
  asserted in the lane's harness over a non-empty manifest, because an absence over an empty one
  would be free.

`--coverage full` supplies authentication provenance and a connecting IP; `reduced` omits both on
purpose. There is no third value: a batch whose coverage was an accident cannot be told from one
whose coverage was planted, and only one of those is checkable, so `generate` refuses to default it.

### The two shape axes: `--encoding-mix` and `--size-mix`

Each axis is drawn **per message** from `(seed, index, purpose)`, and the drawn value is recorded in
the manifest, so a harness can select a fixture by the shape it claims without re-deriving it from
the bytes. `mixed` is a rule for drawing rather than a value a message can carry.

| axis | values | what it changes | what it must never change |
|---|---|---|---|
| `--encoding-mix` | `plain` (default), `quoted-printable`, `rfc2047`, `mixed` | which transfer encoding a leaf body uses, and whether the Subject is an encoded-word | the DECODED bytes. Every variant decodes back to the same text, so a planted fact is carried by every variant |
| `--size-mix` | `small` (default), `medium`, `large`, `mixed` | the size of the **html part and the attachment** | the text body, which is the model's turn and is truncated by the adapter at 2,000 characters |

Two constraints on the size axis are load-bearing rather than stylistic:

- **Bulk rides the html part and the attachment, never the body.** The adapter truncates a turn at
  2,000 characters and refuses an over-long rendered prompt outright, so a fixture grown through the
  body surfaces as a *provider refusal* rather than as the size it claims. `check` asserts this
  directly: no message in a batch may have `turnCharacters >= 2000`.
- **A message with no html and no attachment is REFUSED, not faked.** `pair`, `template-variant` and
  the envelope-violation messages are text-only, so there is no part to grow that is not the turn, and
  silently attaching a file would move `has_attachments` as a side effect of *length*, which is what
  a coverage flag must never be. The tool refuses the whole batch, naming the index, and writes
  nothing at all. Under `mixed` such a message draws `small`, so the manifest records what the message
  IS.

The html padding is an HTML comment filled with `=`. That is deliberate: padding with prose, or with a
long run of letters, would move `deterministic.html_text_disagreement`, whose whole question is how
many tokens the text and html representations share.

**`size` is a band, not a floor.** A message declaring `small` is also asserted to be *under* the
medium target. A one-sided bound would let a batch that secretly grew keep declaring `small` and pass,
so the claim could only ever fail upward.

## The manifest schema, version 4

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

Version 3 also added two fact ids and a `where` value, which is additive rather than a shape change:
`campaign.security_bearing_variant` and `campaign.near_duplicate` are declared by the template
fixtures, and both carry `"where": "batch"`. **A consumer that switches on `where` must handle
`batch`**, which means "this fact is a comparison against the messages before this one, not a
property of this message alone"; `mime`, `submission` and `reasons` keep their existing meanings. The
number did not move for this, because no field changed name, type or arity: an unrecognised value in
an existing string field is exactly what a consumer's default case is for.

The same version added a per-message `notPlanted` list, the fifth shape of claim (beside the four
`expected` predicates). It is additive on the same reasoning: a new optional field, absent from every
manifest that does not make an absence claim, so a version 3 manifest without it reads exactly as it
did before the field existed.

### Version 4: the drawn shape, and why this one is a bump

Version 4 adds **`encoding` and `size` to every message**, plus `encodingMix` and `sizeMix` at the top
level beside `profile` and `coverage`. See "The two shape axes" above for what each value means.

This is a version bump and not an additive field, and the difference is worth stating because the
section above just argued the opposite for two other changes. `notPlanted` is **absent** from a
manifest that makes no absence claim, so its absence is information. `encoding` is present on **every**
version 4 message including the default, so a consumer that has to tell "plain" from "the field is
missing" would be reading a decision out of an absence. Under the old number that decision is silently
wrong; under a new one it is a version it can refuse.

`check` enforces the same distinction, and the gate is the reason it is a version test rather than an
absence test:

- a **version 3** manifest makes no shape claim, so the shape pass is **skipped** rather than failed. A
  check that demanded the new fields of an old batch would call a correct batch broken;
- a **version 4** manifest makes them mandatory, so a message **missing** `encoding` or `size` is
  reported as a defect. Skipping it would let a truncated manifest pass by omission.

The shape pass runs **before any network call** and reports **every** mismatch rather than the first,
so a batch whose manifest does not describe its own bytes is reported as that rather than as a
connection error from a Host that was never worth asking.

**The reconstituted path is covered too, and it is the case that nearly broke.** `ingest` writes
`corpusVersion: 4` like everything else, so its messages must carry the shape fields or `check` fails
every one of them. It records `encoding: "plain"` and `size: "small"`, which are **true of the bytes
it wrote** (it calls the builder at its defaults, and a reconstituted message has no html and no
attachment, so the `small` band holds by construction), and it sets `encodingMix` and `sizeMix` to
**`null`** at the top level, which says that no axis was *drawn*: that verb has no mix flags and
reconstitution applies none. A generated batch always writes a real value in both, so a null mix and a
null `source` cannot both be true of a batch this tool built.

That asymmetry is worth stating because it is where a check goes wrong: a gate that demanded the new
fields of an old batch would call a correct batch broken, and a gate that skipped them for a
reconstituted one would let the claim go unverified. Both are avoided by making the values real
rather than by loosening the gate.

```json
{
  "corpusVersion": 4,
  "generatedBy": "tools/corpus/corpus.py",
  "seed": 1234,
  "profile": "mixed",
  "coverage": "full",
  "encodingMix": "plain",
  "sizeMix": "small",
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
      "encoding": "plain",
      "size": "small",
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

Four shapes, because four kinds of claim can be a predicate on an evidence **row**, and an integer
can carry only the first:

| predicate | the claim it makes |
|---|---|
| `{"value": 1}` | a firing dimension, or a count (trusted authentication failures report 3, not 1) |
| `{"available": true, "value": 0}` | present and **not** firing: a benign message's whole claim |
| `{"available": true, "origin": "Deterministic"}` | provenance rather than a value |
| `{"available": true \| false}` | whether the signal was computable at all (what a window decides) |

A boolean cannot say "present and not firing", and it cannot say "the claim is provenance, not a
value". Both of those are real, and both appear in a single batch.

### `notPlanted` is the claim that has no row

A fifth kind of claim is real and is **not** a predicate here, because it asserts that there is no
row to carry a value. A per-message `"notPlanted"` list, sibling to `"planted"`, names fact ids that
must be **absent from the evidence list entirely**:

```json
"planted": [ { "id": "campaign.near_duplicate", … } ],
"notPlanted": [ "campaign.security_bearing_variant" ]
```

Absent, not present-and-NotApplicable. A row that is there with `availability: Unavailable` says the
pipeline considered the signal and found nothing to say; a signal absent from the list says it was
never emitted. Those are different observations, and `{"available": false}` asserts the first, which
is why an absence claim needs this mechanism rather than a fifth predicate. The field is present only
when the message makes such a claim, so a missing `notPlanted` means "nothing asserted absent" and
never "asserted empty".

Two guards, both falsified rather than trusted (`probe_notplanted.py`):

- **`generate` refuses** a plan that names one id in both `facts` and `not_planted`. A fact cannot be
  planted and asserted absent at once, and a manifest that said so would force `check` to pick a side.
- **`check` fails** a manifest whose `notPlanted` id **is** in the evidence list, and also fails a
  `notPlanted` id that is not a fact this corpus knows (a typo would otherwise assert nothing), and
  fails the whole claim on a message refused at intake, where absence is true only because no
  evidence was gathered and so tests nothing.

**A note for whoever next edits that probe**, because the mistake is easy to reintroduce. The first
version of the second case moved an id into `notPlanted` but left it in `planted` as well, so the run
tripped the **contradiction** guard while reporting that the **absence** guard had been exercised.
The absence guard went untested and the output still said a guard fired: a check that fails for the
wrong reason is the same defect as a test that passes for the wrong reason, and here it was hiding a
claim nobody had falsified. To drive the absence path deliberately the id must be REMOVED from
`planted` first, not merely copied into `notPlanted`. `overview-` ruled (1 Oct) this belongs in the
document rather than in a message, since the next editor of the probe is the person who needs it.

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

The *change* in a pair is recorded in `undescribedChange` and deliberately **not** in `planted`, for a
temporary reason and a durable one. No signal id for it exists in the tree today, so a `planted` entry
would be a claim nothing can report and `check` would fail a correct pipeline for it. More
importantly, a `planted` entry is a promise about a **deterministic** property, and the ids this change
could surface as are the semantic dimensions (`semantic.payment_redirection` going true, or the
continuity answer itself), whose values are the model's judgement and which the corpus declares as
availability only. So this does not become plantable when the conversation lane lands more semantic
ids. It becomes plantable when a **deterministic** finding reports that a turn differs from its
supplied window. The field says what the change is, where it is, and why it is not assertable. A
`planted` list is a list of promises; this one is not made.

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
| the durable-route mechanism and the gate in both directions | `probe_gate.py` | `probe-gate/run.txt`, `probe-gate/result.json` |
| the window flipping continuity to Available, measured on the pair **and** its control | `probe_pair.py` | `probe-pair/both-profiles.json` |
| the campaign rows on all four paired arms | `probe_pair.py` | `probe-pair/both-profiles.json` |
| the v2 batch table and the reconstituted `emails.zip` result | `run_batch.py` | `scratch/corpus/logs/` |

## The two layers a changed destination can reach

A changed payment destination is not one fact. `overview-` ruled the `destinations` identifier does
not exist anywhere in `src/`, and named the two things that do, by layer:

- `semantic.payment_redirection`, a semantic dimension (`Core/SemanticDimension.cs:65`, weight 1.0 at
  `Policy/PolicyContracts.cs:132`). A model's judgement, so it is not a planted fact and this corpus
  does not declare it: the corpus plants what is really in the bytes, never what a model will say
  about them.
- `payment.identifier`, a deterministic fingerprint component (`Assessment/Semantic/
  SecurityBearingFingerprint.cs:100`), extracted from the body with no model and no network, feeding
  the digest that `campaign.security_bearing_variant` is computed from.

So the corpus CAN plant the deterministic half, and now does. Measured 1 Oct, seed 77, count 4, all
four arms through the real seed-and-check path:

| arm | turn 2 wording | destination | `campaign.security_bearing_variant` |
|---|---|---|---|
| `pair` | rewritten | changed | absent |
| `pair-control` | rewritten | unchanged | absent |
| `template-variant` | reused | changed | **Available, value 1** |
| `template-variant-control` | reused | unchanged | absent |

Both reused-wording arms have second-turn messages of **identical length (497 bytes raw, headers
included; 232 bytes of body)** differing in exactly one line, the sort code and account number, so
the destination is the only variable between them. Between `pair` and `template-variant` the only
variable is whether the wording is reused. The two numbers are both given because they measure
different things and a reader comparing against `manifest.json` will find the body length as
`turnCharacters` (232) and not the file length.

Two things this table is for:

1. **It corrects a claim this file would otherwise have made.** `pair` was the only paired fixture,
   and its change was recorded under `undescribedChange` as "real in the bytes, not yet assertable".
   That was true and incomplete: the change is not assertable on the CONVERSATIONAL layer until the
   conversation lane lands, but it was assertable on the campaign layer all along, and `pair` cannot
   reach it because a rewrite is not a reuse. The layer was reachable; the fixture was wrong.
2. **It is a control, not a demo.** `template-variant-control` reuses the wording and keeps the
   destination, and the variant does not fire. A detector that fired on any repeated wording would
   pass the `template-variant` claim while being wrong about ordinary repeated mail, and the control
   is where that shows.

`campaign.security_bearing_variant` therefore carries `"where": "batch"`, a value no other fact uses.
Every other fact is a property of one message; this one is a comparison against the messages before
it, so the same bytes report differently at different positions in a batch. Measured: in a
`template-variant` batch of four the signal fires on both turn 2s AND on the second turn 1 (index 2),
which follows the previous pair's changed turn 2 and reverts the destination, and not on index 0,
which has nothing behind it. The manifest **plants** the fact on each index where it was measured to
fire and **asserts it absent** at index 0. The rule is positional (index 0 versus the rest) rather
than by parity: an earlier version planted it on odd indices only, which left index 2 firing, true
and unasserted, and the manifest claiming less than it knew. The rule is stated for any batch length,
so the assertion at indices past the measured four is an extrapolation: it was tested rather than
assumed, by seeding a batch of six and letting `check` fail it if the fifth message behaved
differently. It did not, and both arms pass at count 4 and count 6.

**The absence is now asserted, not merely observed.** In every arm above where the variant does not
fire, its row is **absent from the evidence list**, not present-and-zero. That was a gap this file
used to report and could not close: `fact_present` has no predicate for absence, and
`{"available": false}` asserts the different and false claim that the row is there and NotApplicable.
The control asserted the checkable positive (`campaign.near_duplicate` fires) and only *reported* the
variant's absence. The gap is closed by `notPlanted` (above): `template-variant` asserts the variant
absent at index 0, where it was measured not to fire, and **planted** at the indices where it was; and
`template-variant-control` asserts it absent on every message. This was not an interface change to
hold back for another lane: `overview-` checked the tree and confirmed nothing reads a manifest, so
the interface between this lane and the console is the `seed` CLI, not the file.

- **The durable route is the only route whose evidence reaches the decision.** `seed` therefore uses
  `POST /v1/submissions`. The mechanism is one step further in than it looks, and an earlier version
  of this file stated it wrongly: the MIME analyser **does** run on both routes
  (`Hosting/MessageIngress.cs:98`, shared by both endpoints) and `Prepare` **discards** its evidence,
  returning only `result.Message` (`:118`). What differs is that `MailAssessor` resolves the original
  bytes itself through `IRawMessageSource` (`_rawMessages.TryGetAsync`, `Assessment/MailAssessor.cs:270`),
  and when they resolve it adds the parsed evidence (`evidence.AddRange(parsed.Evidence)`, `:284`);
  when they do not, `evidence.Add` at `:303` adds `assessment.deterministic_extraction` instead.
  `SpoolRawMessageSource.TryGetAsync` returns null for any non-durable reference
  (`Assessment/Ports.cs:73-76`), and the assessment-only endpoint passes
  `PayloadReferences.Ephemeral` (`Endpoints/AssessmentsEndpoints.cs:79`).

  **Re-read these line numbers rather than trusting them.** All thirteen `file:line` references in
  this file were checked against HEAD `ef2f57b` and **five had drifted** from what an earlier reading
  recorded, by up to eighteen lines, one of them landing on an unrelated statement
  (`AssessmentsEndpoints.cs:64` is an assessor-unavailable guard, not the `Ephemeral` line it was
  cited for). Each drifted reference is now paired with the symbol it points at, for the reason
  `ingress-` gave about a message: a line number quoted without the commit it was read at cannot be
  told apart from a wrong claim tomorrow. A file is not exempt from that.
  (The count was first written as twelve, from a line-based scan that could not see the one reference
  whose backtick pair spans a line break. It is thirteen: ten carrying a path, three written as a
  bare `:NNN` continuation of a path named earlier in the same paragraph. Recorded because an audit
  that miscounts its own scope is the thing it is auditing for.)

  So, stated properly: **the assessor's only route to the original bytes is the spool, and the
  assessment-only route deliberately does not spool. A planted MIME fact can therefore never appear
  as a finding there.**

  **This is a corroboration, not a discovery, and the credit is not this tool's.** The fact that the
  route cannot corroborate an allow has been known of the composition root since **30 Sep 2026**,
  measured by `conversation-`'s 5-arm run: a benign message at
  index 0.0000 came back `Hold` with `policy.allow_uncorroborated_by_deterministic_evidence`, on all
  15 assessments. What the probe above adds is an independent re-measurement from a different
  fixture, and what the paragraph above adds is the source-level cause, which the earlier note
  did not have (it says the deterministic layer "never runs there", which is not the mechanism). The
  reason code is also worth naming: it reads as a claim about the message when on this route it is a
  claim about the route.
- **That difference is the corroboration gate, measured in both directions** from identical bytes
  (`probe_gate.py`, which writes its own `probe-gate/run.txt` transcript
  and `probe-gate/result.json`): the same benign message is `Allow` on `/v1/submissions` with 14
  deterministic `Available` rows, and `Hold` on `/v1/assessments` with
  `policy.allow_uncorroborated_by_deterministic_evidence` and **zero deterministic `Available`
  rows**. The qualifier is the claim, and this is not the same as "the list holds no row of
  deterministic origin". Origin and availability are separate fields and give separate answers:
  `assessment.behavioural_context` is a row whose `Origin` is `Deterministic` and whose
  `Availability` is `Unavailable` (the `BehaviouralContextUnavailable` marker in
  `src/StyloMail.Assessment/MailAssessor.cs`), **and its source is the sender profile store, not the
  message**. The label is the sharper half of the problem: `EvidenceOrigin.Deterministic` is defined
  in `src/StyloMail.Core/EvidenceAvailability.cs` as "Computed locally from the message and envelope,
  reproducible, no provider involved", so this row carries the one origin whose own definition
  excludes it, while the same enum holds `Behavioural`, "Derived from profile comparison and temporal
  behaviour", which is the origin that describes it. That is an observation about the Assessment
  lane's row, reported to them and not changed here.
  A filter on origin alone keeps it, so a whole-list comparison of two routes, two runs or
  two call orders can show it moving and read that as a difference in the thing being compared.
  **The probe's call order decides the answer for the same reason**: it posts `/v1/submissions` before
  `/v1/assessments` for each message, and only the submission route warms the store
  (`UpdateObservedState` returns early on `context.AssessmentOnly`). Submit-first suppresses that
  marker on the assessment that follows; an assess-first order emits it, and the assessment list then
  carries one deterministic-*origin* row while still carrying zero `Available` ones. Name the row and
  assert the shape of the difference rather than filtering it out: filtering by availability would
  also drop byte-derived rows that are legitimately `Unavailable`, and filtering by origin keeps the
  profile-store row. (Broadcast trap from `ingress-`, 1 Oct, verified by me at source.)
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
  reports `0`, the control reports `1`. So the window's **content decides the answer**,
  which is itself the result worth having, and a consumer comparing continuity values across corpus
  versions is comparing answers to different questions.
  **The mapping, supplied by the lane that owns it (1 Oct), not guessed here.** `conversation-`
  states it: **`value == 1.0` is A** (consistent with the prior exchange) and **`value == 0.0` is B**
  (inconsistent with, or unrelated to, the context), criterion text at `Core/SemanticDimension.cs:130-131`.
  So on this fixture the **changed turn reads B (advancing)** and the **control reads A (contained)**,
  which is the couplet behaving as built: the changed turn moves past the window and the control does
  not. An earlier version of this file said the two arms had "the same value at version 2", which was
  true of the number and misleading about the fixture: both arms read `0` under version 2, which under
  this mapping means both read B, so version 2 could not distinguish them at all.
  **And the danger that was flagged is retracted, because it rested on a guess.** This tool had warned
  that if `0` were A the fixture would be inverted. `0` is B, so it is not inverted. The conditional
  was honest at the time and it is now resolved, which is the point of not having asserted it.
- **The token count is not a containment check, it is anti-correlated with the answer.** Measured:
  the changed turn shares **6** long tokens with turn 1 (it cites the details it supersedes) and the
  control shares **2** (`invoice`, `payment`), and the arm that overlaps MORE is the arm that reads B.
  So this tool's containment boolean is worse than non-discriminating: it prints `True` for both arms
  and its count runs **opposite** to the reading. Neither may be quoted as evidence about containment.
  `conversation-` reached the same conclusion independently from the other direction (their arm holds
  the overlap up while changing the asserted state, and also reads B), so the criterion is containment
  of the asserted state and not shared words. A token metric cannot reproduce it.
- **Consequence for reading the batch table, and it inverts the intuition.** `conversation-`'s ruling:
  a non-confirming continuity row is **masked** on the shipping path and contributes nothing to the
  index, while a confirming one enters the numerator at full weight. So in the pair, the **control arm
  is the arm that carries risk** for the ordinary property of agreeing with its own thread, and the
  changed arm carries none. A reader who assumes the control is the quiet half has it backwards. Both
  arms still return `Allow` here, because the dimension's weight alone does not reach a threshold.
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
    --source-archive emails.zip --limit 500 --out scratch/reconstituted
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
two character counts; see the changelog above), and then by **corpusVersion 4** the same evening (the
two shape axes and the `mailbox` profile, from the desktop-harness design the operator approved at
18:53). The `corpusVersion` here versions this tool's manifest and what a conversation window
contains. It is not the decision-ledger schema version, which moves independently and is owned by the
Host.

**What the version 4 work has and has not been held to, stated apart because the difference matters.**
The DEFAULT path is byte-identical to version 3: `HEAD`'s `corpus.py` was extracted and run beside the
new one for six profiles, and every `.eml` is unchanged by `diff -r`, so the state table below still
describes those batches and their bytes exactly. Everything NEW is verified **offline** and not
against a Host: the shape claims are asserted about the BYTES by `check`, determinism is re-verified
across processes per axis, and the size axis's 2,000-character turn constraint is asserted directly.
What has **not** happened is a seeded run of the `mailbox` profile or of any non-default axis, so
**its state targets are targets and nothing here has measured them**. The two runtime items the design
flags (section 4.7: that the assessor path is really taken for a seeded message, and that the queue
round-trip returns the joined id) are in the same position and want a build window.

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
  | `template-variant --count 4 --coverage full` | 4 accepted, all Allow, every listing 0 rows; `campaign.security_bearing_variant` verified on both turn 2s |
  | `template-variant-control --count 4 --coverage full` | 4 accepted, all Allow, every listing 0 rows; `campaign.near_duplicate` verified, no variant anywhere |

  Every batch above ran on a loopback Host on a port chosen free per run, so no port is recorded
  here: a number in this table would be wrong on the next run. The port in use is printed by
  `run_batch.py` and by `probe_pair.py`, and is what a report should carry.

  `awaiting_decision` is a superset of `held` and `quarantined` here, not a third bucket: the three
  profiles above read 1/1/0, 2/0/2 and 1/1/0 for `awaiting_decision`/`held`/`quarantined`, which is
  only consistent if it counts the undecided rows of both states. A row that reads 0 in a state the
  decision never reaches is the point, not an omission: an Allow populates no listing, so the pair's
  zero rows are how the control stays quiet.

  The pair rows carry the number that moved at version 3: the `semantic.conversational_continuity`
  row reads **0** for `pair` and **1** for `pair-control`, where under version 2 both read `0.0`. By
  the mapping `conversation-` supplied, `1.0` is A (contained) and `0.0` is B (advancing), so the
  changed turn reads B and the control reads A, which is the fixture working as designed. See the
  window section above, which also records that the control arm is the one carrying risk and that the
  token count runs opposite to the reading.

- **The invocation the console harness uses is verified compatible**: `ux-scripts/console-harness.sh`
  `console_seed_corpus` calls `seed --base-url … --key-file … --batch …`, which is this tool's
  signature, and writes its principal key as a bare hex string, which is what this tool's reader
  takes. The file carries the executable bit, so the harness's direct-invocation branch works as
  well as its `python3` fallback.
- **Reconstituted `emails.zip` mail does not populate the queue**: 40 rows, all 40 Allow, every
  listing 0 rows. Reduced coverage plus a local model scores below the allow threshold. That batch
  measures agreement; the synthetic profiles are what populate a console state.
