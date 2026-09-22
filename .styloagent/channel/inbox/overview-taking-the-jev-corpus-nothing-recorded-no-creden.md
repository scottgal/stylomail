**From:** access-
**Timestamp:** 2026-09-23T00:38:07.4281870+01:00
**Priority:** normal

# Taking the Jev corpus. Nothing recorded (no credential, as you said). Two flags before I build.

`access-`. **My lane is clear, so I am taking this.** The harness is committed, I stood down, and nothing is outstanding. Answering your "tell me either way" first, and raising two things I want settled before I touch anything.

## Confirmed: no credential

`jevkey.pvt` is **absent** and `TYPESAFE_API_KEY` is **unset**. Checked by existence and by `-n` only, so neither value was ever read, let alone printed. The six `.eml` inputs and the README are present and committed.

So: I build everything that does not need a credential and report, exactly as you said.

## Flag 1: my earlier mission forbade even referencing `jevkey.pvt`, and this brief tells me to read it

My `access-` mission says verbatim: *"Never read, print, or reference `jevkey.pvt`."* Your brief says the credential may be supplied *"as `TYPESAFE_API_KEY` in the environment or as `jevkey.pvt` at the repository root"*.

**I read that as lane-scoped rather than contradictory** (the old rule kept me out of the key while building the proxy; this brief is explicitly about recording with it), and it is moot while the file is absent. But I am not going to quietly decide that a hard "never reference" from one brief is overridden by an "or" in another.

**My proposal, which I will follow unless you say otherwise: prefer the environment variable, treat `jevkey.pvt` as the fallback, and never let either value reach a log, a command line, a fixture, an exception or my report.** If you would rather I never touch the file at all even when it appears, say so and the recorder will name only the environment variable.

## Flag 2: the fixtures are `.eml`, so recording needs a MIME parser in the Jev test project

The capture has to be *"what the adapter actually receives"*, which means the request has to come from the real sample messages. Those are `.eml`, and `StyloMail.Jev.Tests` currently references only `StyloMail.Jev`. It has no parser.

**So I intend to add a `ProjectReference` to `StyloMail.Mime` in `tests/StyloMail.Jev.Tests`**, which is inside my lane but is a new dependency for that project and worth telling you about rather than discovering in a diff.

The reason it matters for correctness: the replay test must assert on the response produced by *the same input* that recorded it. If the recorder parses the `.eml` and the replay test hand-builds an input, the two can drift and the corpus silently stops corresponding to its own fixtures. So **both will share one case-loading helper** that parses the `.eml`, and the replay test cannot diverge from the recording.

## One design note, because it shapes what I build

`StyloMail.Jev` grants no `InternalsVisibleTo`, so `JevResponse` and friends are internal and the existing tests build response JSON **as text** through a fake handler. That is exactly right for this corpus: the README requires the response *"as received, not reformatted"*, and raw text is what gets recorded and replayed. No `src/` change needed, which I know you would want to hear.

## What I will build, in order

1. **The recording harness**, opt-in and skipping without a credential, driving the real adapter through an `HttpMessageHandler` that tees the raw response bytes. It writes each response plus provenance (model requested and reported, question schema version, date, and that it came from a live call) into `tests/fixtures/jev/`.
2. **Its loud failure**: with no credential it names both places it may be supplied and records nothing, rather than writing an empty corpus that looks like a result.
3. **The replay tests**, which are the load-bearing half: drive the adapter over each recorded response and assert the dimensions, availability and values, **including `NotApplicable` and `Unavailable`**, since those are the states a corpus of confident answers never reaches.
4. **A recording script** under `.styloagent/tools/` so a recording is reproducible.

Proceeding with 1 to 4 now. Reporting when they are in and green, or sooner if the environment blocks something I cannot work around.
