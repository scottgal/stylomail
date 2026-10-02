# Running StyloMail

What the `StyloMail.Host` executable does, how to run it, and how to tell whether a running instance
is healthy.

**Every claim here was checked against a running process, not read off `Program.cs`.** That
distinction is the whole point: `Program.cs` is commented in more detail than this file, and a
comment is a claim about code rather than evidence about it. The checks are a script that starts the
real binary, drives real sockets and real HTTP requests, and asserts on what came back: 49 of them,
all passing. Where a detail could not be verified that way, this document says so rather than
implying otherwise.

The code wins wherever the two disagree. This is the operator-facing view; the reasoning behind each
decision lives in the code beside it.

---

## 1. One executable, two front ends

```
stylomail serve                     run the HTTP host (Kestrel)
stylomail assess <file.eml>         assess one message, locally
stylomail replay <directory>        re-analyse fixtures deterministically
stylomail quarantine list|release   inspect and release quarantined mail
stylomail profiles inspect          list profile state for a tenant
stylomail key create|list|revoke    mint, list and revoke API keys
```

`serve` is the default when the first argument is absent or starts with `--`, so a hosting platform
passing its own flags (`--urls`, `--contentRoot`) starts the server rather than failing to parse.

**Why one binary rather than two.** The CLI and the HTTP host share a composition root
(`HostServices.AddStyloMailHost`), so a CLI command cannot administer a system configured differently
from the one it is describing. Two binaries would be two definitions of what the system is, and the
first thing to drift would be a safety default: the storage path, or the assessor that refuses
everything when nothing is configured. An unrecognised command exits `2` rather than falling through
to `serve`: a typo silently opening a network listener is a surprising thing for a mail component
to do.

The CLI commands run **one-shot**: they build a host, do their work, and exit. They do not start
hosted services, so a CLI command never opens the SMTP port or runs the delivery worker.

---

## 2. What happens at startup, in order

```
1.  parse arguments; anything but `serve` becomes a one-shot CLI command and returns here
2.  build the web application and its service collection
3.  InitialiseStorageAsync     : the host's schema, the persistence schema, the queue's
4.  resolve IMailAssessor      : forced, not lazy
5.  resolve ISmtpIngressSink   : forced, not lazy; this is where the composition assertions run
6.  log the transport description
7.  UseAuthentication → CsrfMiddleware → UseAuthorization
8.  map routes (and conditionally map the Cloudflare intake)
9.  Run
```

Steps 4 and 5 are the interesting ones. **Both components are registered as lazy singletons, so
without forcing them a misconfigured deployment would boot, answer `/health/ready` with 200, and fail
only when real mail arrived**: a misconfiguration that looks like a healthy service. Forcing the
resolution turns each into a startup failure instead.

Verified by running, with the process failing to start on each:

| Configuration | Observed |
| --- | --- |
| `TYPESAFE_API_KEY` set, `STYLOMAIL_PROFILE_KEY` absent, **default provider** | refuses to start, names the missing variable |
| the same pair with the **local provider** selected | starts and warns, `/health/ready` reports `not_ready` (the required secrets follow the provider: see §6) |
| both set, profile key under 32 bytes | refuses to start, names the variable **and the length** |
| ingress bound larger than the queue's | refuses to start, names **both** values |
| Cloudflare intake enabled with no secret | refuses to start, names the variable |

**These refusals are unhandled exceptions, so the process aborts.** In a shell that is exit code
`134` (`SIGABRT`), not a tidy `1`. That is deliberate: the message names the variable an operator has
to fix, but a script checking `$?` should test for *non-zero* rather than for a specific code.

### The composition assertions

Built into step 5, they check two properties that belong to a **pair** of components and are therefore
invisible to a reader of either one:

- **`SmtpIngressOptions.MaxMessageBytes <= QueueOptions.MaxPayloadBytes`.** If the ingress will take
  messages the queue will not store, the ingress reads and authorises the message, the sink spools
  it, and the queue refuses it, so the caller sees a **capacity deferral that looks like spool
  pressure** while the cause is two components away. Both values are named on failure.
- **One `SpoolStore` instance**, compared by reference identity rather than by path. A second instance
  over the same directory would make the assessor's read-back, the queue's orphan sweep and any later
  delete-after-accept reason about a different object; a second instance over a *different* directory
  is caught separately, by comparing the spool root against the configured one.

---

## 3. Middleware order

```
UseAuthentication()          → establishes who the caller is
UseMiddleware<CsrfMiddleware>()  → only engages on the browser cookie channel
UseAuthorization()           → decides whether that caller may reach this route
```

Authentication is written out explicitly rather than relying on the implicit middleware the web
application inserts, because the boundary is the part of this host most worth reading carefully and it
should be visible in the pipeline rather than implied. The CSRF middleware sits between the two
because the channel is a property of the *authenticated principal*: it only engages for requests that
arrived on the cookie channel, which is not known until authentication has run.

Health, metrics and (when enabled) the Cloudflare intake are mapped without an authorization
requirement. For health that is a necessity: a probe that has to authenticate cannot do its job, and
a credential handed to a load balancer is a credential in one more place, and the compensation is
that nothing on those routes may describe a message, an identity or a tenant. The Cloudflare intake
carries its own credential instead: a shared secret in a bearer header.

---

## 4. Conditional routes

**The Cloudflare intake is mapped only when `CloudflareIngress:Enabled` is true.** A deployment that
has not opted into it gets a `404`. Mapped-but-refusing was the alternative and is worse: a route that
exists and always fails invites a configuration change to "fix" it, whereas its absence states plainly
that this deployment has no such intake. `POST /v1/session` follows the same rule for the browser
channel.

Resolving the connector **at startup** is what turns *enabled but secretless* into a boot failure
rather than a route that answers `401` to every message the Worker offers. That distinction matters
because the two look identical from the Worker's side, and the second sends the operator to inspect
the Worker while the fault is at this end.

**The live-traffic hub is mapped only when `Traffic:Enabled` is true**, and for a third reason on top
of the two above: a console has to be able to tell *"this Host has no live feed"* from *"I am not live
on it"*. Those are different sentences on an operator's screen, so they are different responses.
With the feature off, `POST /v1/traffic/negotiate` answers **`404`**; with it on but the caller
lacking the `Review` privilege, `403`; with no key at all, `401`.

---

## 5. What the host says about itself at startup

One line, always, before it serves:

```
StyloMail transport: SMTP submission listener enabled; delivery worker NOT running
(no StyloMail:Transport:Upstream:Host configured). WARNING: mail accepted by this
deployment has nowhere to be delivered and will be held until it expires.
```

**A listener writing into a queue nothing drains is a configuration mistake**, and the warning is the
one that catches it. Nothing here is an error the host refuses to start over: an inbound-only
deployment and a submission-only one are both legitimate shapes, but without the warning the mistake
surfaces as messages ageing toward their expiry, which is the worst way to find out. A deployment with
no listener and no upstream is described without a warning, because nothing is missing.

---

## 6. Configuration

All configuration is ordinary ASP.NET Core configuration, so **anything below can be set by an
environment variable** using `__` as the separator:

```
StyloMail__Storage__SpoolRoot=/var/lib/stylomail/spool
StyloMail__Transport__SmtpIngress__Enabled=true
```

Everything under `StyloMail:Transport` defaults to **off**. An unconfigured deployment runs the HTTP
host and the CLI, opens no mail port, and dials nothing.

### Environment variables

| Variable | Meaning |
| --- | --- |
| `TYPESAFE_API_KEY` | Jev / TypeSafe API key. |
| `STYLOMAIL_PROFILE_KEY` | Profile keyed-hash master key. **At least 32 bytes** of high entropy. |
| `STYLOMAIL_CF_INGRESS_SECRET` | The shared secret a Cloudflare Email Routing Worker presents. |
| `ASPNETCORE_URLS` | Where Kestrel binds, e.g. `http://127.0.0.1:8080`. **Avoid 5000 and 7000 on macOS**: see the note below. |

**These four are read from the environment directly, and the three secrets are read from nowhere
else.** A secret given a configuration key ends up in an `appsettings` file, then in a repository,
then in an image layer, and a key committed to a repository must be treated as compromised and
rotated rather than merely deleted. There is deliberately **no configuration property** for any of
them; the names live as constants beside `HostCredentials` so they are defined once and greppable.

Two consequences worth knowing:

- **`TYPESAFE_API_KEY` and `STYLOMAIL_PROFILE_KEY` are all-or-nothing under the hosted provider.**
  There, exactly one of them set is a misconfiguration and the process refuses to start: running with
  the Jev key live and no pseudonymisation key would work perfectly in testing and quietly collapse
  tenant isolation in production. Which secrets are required follows the provider, so the local
  provider below needs the master key alone, and a provider key it never reads is inert rather than a
  reason to refuse.
- **Changing `STYLOMAIL_PROFILE_KEY` is a migration, not a configuration change.** Every stored
  profile key was derived from it and becomes unreadable.

### Choosing the semantic provider

| Key (under `StyloMail:Assessment:`) | Default | Notes |
| --- | --- | --- |
| `Provider` | `Jev` | `Jev`, `Nimble` or `NeverAsks`. Matched by name, case-insensitively. |

`Nimble` runs the local decision model instead of the hosted one. Four settings, all under
`StyloMail:Nimble:`, and the first two are announced in the log at every boot:

| Key | Default | Notes |
| --- | --- | --- |
| `Endpoint` | `http://127.0.0.1:11435/v1/systemone` | A **non-loopback** endpoint logs a warning that says so. Staying on this machine is the property the local provider was chosen for, and an endpoint elsewhere gives it up while the assessments keep looking right. The port is part of the hazard too: the default is the `11435` path, and pointing this at `11434`, the older of the two servers, is unmeasured (`NimbleOptions.cs:56` and the remarks on it). |
| `Model` | `nimble:latest` | Model reference to generate with. |
| `NumCtx` | `8192` | The window the request ASKS for. The fit shortens the body until the serialized request's **UTF-8 byte count** fits this, which is a byte budget standing in for a token window (see below), and it clips by the measured excess **plus `PromptByteMargin` (512) per pass, with the step floored at 64 bytes** (`Math.Max(excess + 512, 64)`, then `budget > step ? budget - step : 0`). **The floor is on the step and not on the body**: a pass whose step is at least the remaining budget sets it to **zero**, and the body is then cut to nothing rather than trimmed to a minimum. With the budget at its 2500-character default that takes an excess of **1988 bytes**, and the excess is this whole serialized request against `NumCtx`, so what reaches it is the non-body overhead **plus the body's BYTES**. **The non-body side is itself unbounded text**: only `body_text` and `quoted_text` are cut to the body budget, while the subject is not truncated at all, every link and every attachment carries several strings, and `conversation_context` is up to ten entries of two thousand characters each (`NimbleMessageState.cs:175-178` at `35ff5d0`). So a reply thread with a modest body can carry the excess on its own, and **the body's character cap is not a byte cap either**: an escape-free 2500-character body contributes 2500 bytes, while one whose characters need JSON escapes contributes up to about seven bytes per character. **And the terminal case is a refusal rather than a cut**: when the state alone leaves no room even for an empty body the fit returns nothing to send, and every askable row comes back `Unavailable` naming the window (`NimbleSemanticMailClassifier.cs:384` at `35ff5d0`, reason at `:207`). The margin itself is deliberate slack rather than a minimal trim, and **the relation between what the fit keeps and where it cuts holds per message**: `L = kept + min(emitted, 2500) - 1988`, whose cap case is `kept + 512`. **What makes it a CHECK rather than a re-evaluation is that a capture exists on a shape its terms were not measured on**: `corpus-`'s 1615-body take has `L = 131`, giving 504 where the trace reads **504 to the character** (`policy-`, 2026-10-02T06:26, out of sample on the benign shape the constants came from). **The two same-shape readings - an emitted 2161 giving 1987 on the wire against 1987 predicted, an emitted 2500 giving 1648 against 1648 - use the shape `O-nought` and the `105` were measured on, so they re-evaluate the form rather than test it.** **The boundary is then measured by bisecting on the shortened flag, which involves no arithmetic**: bodies of 2159 and 2160 characters arrive un-cut while 2161 arrives cut, so that message's boundary is **2160 exactly**, and **the 2160 arm is a request of 8192 bytes, the window size itself**, which is the fit's condition met at equality. **That inference holds because the arms these captures were taken on have no quoted text** (`corpus-`, 2026-10-02, measured rather than assumed in its index; the corpus can produce a reply now, both from a fixture in its own directory and behind a generator flag added the same night, so **the immunity belongs to the captures and not to the corpus**): 72 generated bodies across nine profiles matched none of the five line-anchored markers the quoted-history splitter looks for, with zero newlines in the decoded text part at 400, 2000 and 2500 characters, so `QuotedText` is null on every capture arm and the body is the only field that could have been cut. **And on the wire as it now stands each field reports its OWN pair of keys** - `body_text_shortened_for_prompt` and `body_text_characters_kept` written only when the BODY was cut, and `quoted_text_shortened_for_prompt` with `quoted_text_characters_kept` for the quoted tail (`NimbleMessageState.cs:105-120`, landed as `35ff5d0`) - so a quoted-only cut can no longer publish the body's length under the body's key, and the bisection and the relation hold on a body cut whatever the quoted tail does. **That is a change, and the capture that demonstrates the OLD wire is still the clearest statement of why it was made**: before `35ff5d0` the state wrote a single flag for either field and published the body's uncut length as `body_text_characters_kept`, so a quoted-only cut set the flag while the kept key reported a full body length - and a capture from that wire exists, with the flag present, `kept` **218** equal to the body's whole length, and the quoted field cut to 1026 (`policy-`, 2026-10-02T06:35, from `corpus-`'s tailed fixture). **That capture is an instance of the wire BEFORE that commit**, and on it both the bisection and the relation would have stopped holding. **Across three same-shape, same-size messages the boundary runs 2160 to 2176, so any single value describes one message and not the profile** (`corpus-`, 2026-10-02, `.styloagent/scratch/corpus/captures/INDEX.md`, whose rows include all six captures). **The cause is measured rather than inferred**: across those three the wire total is identical at 7785 bytes and the body is the same tiling, while the subject differs and the sender-behaviour field is present in two of the three, so **the split between non-body state and body is what moves - a message carrying more overhead keeps less body at the same request size**. **The movement has a direction**: the spread runs from **2160** for the coldest message of the three, whose sender profile is absent with **fifteen of its eighteen fields at `null`** and only the three that do not depend on a profile populated (`available`, `cold_start` and `direction`), up to **2176** once that profile carries values, because `null` costs four bytes where a value costs one. **So the low end is the tightest boundary and the conservative value to size against.** |
| `EffectiveNumCtx` | *(unset)* | The window this provider ASSUMES the server applies, and the number its truncation guard refuses against. Unset means it derives `NumCtx / 2`, so the default assumed window is **4096**, which is low enough to refuse ordinary twelve-question mail. **Set it above the largest evaluation the deployment produces, and then CHECK THE BOOT LINE**: the Host logs the value it resolved at every start, so `(unset, derived)` there means the setting did not reach the options, and a settings file that was never read looks exactly like a setting that was never applied. **That line is a per-LAUNCH reading and not a property of the build**: the same binary and the same settings file have been measured reading `(unset, derived)` and `65536` twenty-four seconds apart, differing only in the working directory the Host was started from. The fit bounds the request by **bytes** while the guard counts **tokens**, and that token count is not bounded by the request's own size: it exceeds the bytes, and both the question count and the body's CONTENT scale it, by up to about 6x on measured shapes, so a request that fits the byte budget can still be refused. See below for what none of this says about the server. |

Everything else the local provider has (its timeout, its circuit breaker, its prompt bounds) is at
its own defaults.

**The two windows are this provider's hazard, and they are why the deployment value above exists.**
`AppliedContextWindow` is `EffectiveNumCtx ?? NumCtx / 2`, and the fit and the guard are expressed
against DIFFERENT numbers: the fit budgets a **byte count** against `NumCtx`, while the guard refuses a
**token count** against `AppliedContextWindow`. A request can therefore satisfy the fit and still be
refused, and the gap between the two is what a twelve-question request falls into. Measured on the
reference machine: the Host asks all twelve questions, the request evaluated at **12230 tokens**, and
against the derived 4096 every semantic row came back `Unavailable`, so `POST /v1/submissions`
answered **503** and no message could be accepted. With `EffectiveNumCtx=16384` the same request is
accepted, the submission answers **202**, and the semantic rows score.

Setting that ceiling unblocks a deployment; it does not repair the comparison. The fit still budgets
bytes against a token window, and the guard still compares a **token count the request's size does not
bound** against a **fixed number**, so the honest ceiling is a function of the request rather than a
number to guess. A deployment that asks fewer questions, or sends smaller messages, may need none of
this: the number to set is the largest evaluation the deployment actually produces.

**And `16384` is a worked example rather than a recommendation, because a real message overran it.**
Measured on the reference machine against a three-turn conversation fixture: four calls at an applied
window of `16384` were all refused with the adapter's own reason, `server evaluated 20498 prompt tokens
at an applied window of 16384`, and the same four at `32768` were answered. So the probe's own
message clears `16384` and a single real submission does not, and the margin a deployment needs
depends on its mail rather than on either figure. Set this above the largest evaluation you measure,
and prefer surfacing that evaluation in your own logs over inferring it from a size in bytes.

**The number the Host logs as `applied` is the PROVIDER's own figure and not the server's, and it
follows `EffectiveNumCtx` by construction.** `AppliedContextWindow` is `EffectiveNumCtx ?? NumCtx / 2`
(`NimbleOptions.cs:192`), and the boot line prints exactly that field under the name `applied`
(the window line `HostServices.cs` logs at every boot, headed "StyloMail nimble window" and carrying all
three numbers in its own text; that file was edited repeatedly the same night, so read it by content
rather than by any line number). Measured from three boot lines at three settings, each read out of its
own file: unset reads `applied 4096`, `16384` reads
`applied 16384`, `32768` reads `applied 32768`
(`.styloagent/scratch/ingress/host-probe/run-20261001T220049Z/host.log:4`,
`run-20261001T220000Z/host.log:4`, `.styloagent/scratch/corpus/run-mailbox-full/host.log:4`). Those are
two lanes' runs rather than one instrument, so the series is three boots and not a controlled sweep, and
the value in it is that the relation holds wherever it was read. Raising the setting therefore DOES move
the number the guard compares against, which is what the 503-to-202 effect above measures, and the
advice above is real advice for the guard.

**The number the guard compares against is the server's own token count, and the request's byte budget
does not bound it.** The fit caps the whole serialized request at `NumCtx` **bytes**, and from that cap
the natural conclusion is that the evaluation cannot exceed `NumCtx` tokens. **Measured, it does not
hold**: `input_tokens` EXCEEDS the request's own byte count from six questions upward, and it grows
faster than linearly in the question count (`nimble-`, 2026-10-01T23:30,
`127.0.0.1:11435/v1/systemone`, one fixed 3291-byte state, only the question count moving, artifacts
`.styloagent/scratch/nimble/question-count-curve.json` and `question-count-sweep.json`: six questions,
5404 request bytes, **7644** input tokens; twelve questions, 7246 bytes, **20642** tokens, identical on
a repeat; eight points in all). A UTF-8 byte cannot hold more than one token, so that figure is not a
tokenization of what the client sent. **Those two rows give both measured ends of this section without
any further mechanism**: the request refused at `applied 16384` evaluated at 20498, and the same request
was answered at `applied 32768` because 20498 is below it. So the guard is reachable and it has fired.
**The firing is witnessed at `applied 16384`, and the guard is MEASURED to be reachable at `32768` as
well**, which turns on the largest evaluation an admissible request can produce; that is measured below.
What `applied` changes is which requests reach the guard, so size the setting against the largest
evaluation the deployment produces.

**And that measurement is a warning, because content moves the evaluation far more than prose does.**
Holding twelve questions and one state shape and varying only the body's content, with every request
inside the fit's 8192-byte cap and every body AT its 2500-character budget, the evaluation ran from
**19814** tokens on prose to **43202** on a hexadecimal-looking body, an expansion of 2.87x to 6.25x
(`nimble-`, taking the full budget rather than a shorter body, `.styloagent/scratch/nimble/expansion-shapes-2500.json`,
script `probe-expansion.py`; six shapes, all measured and all admissible, at 6910 request bytes except one
at 7012: prose 19814, base64-ish 34106, mixed 35114, random-case 39602, punctuation-heavy 41138, hex-ish
43202). Reaching `32768` needs an expansion above **4.00x**, and **how many shapes cross it depends on the
encoder**: **five of six under the probe's**, and **four under the adapter's**, because the adapter's
default JSON encoder escapes `<`, `>`, `&`, `'`, `+` and the backtick as `\uXXXX`, the state is a JSON
string inside a JSON document so each escape is escaped again, and the same text therefore makes a larger
request. **`hex-ish`, `base64-ish` and `random-case` are exact under either encoder**; `mixed` and
`punctuation-heavy` are the two whose margin depends on which language measured them. Only prose is safe
either way, and the hex-ish arm clears the guard by **1.32x**. So `EffectiveNumCtx` at `32768` **refuses a
twelve-question message whose body is dense, and does not refuse one whose body is prose**. What is
measured is the body's content, and bodies whose text is naturally dense come back
`Unavailable` today: long hexadecimal, pasted base64 or JSON, log and diff output, heavy punctuation.
That is the **author's** density rather than a transfer encoding's, because the analyser decodes
quoted-printable and base64 parts before the classifier sees any text
(`BoundedMimeMessageAnalyzer.cs:150-151` and `:379`). An attachment cannot be the cause either:
`NimbleMessageState.Build` puts only metadata in the state for one, a file name, its declared and
implied content types, a byte size and whether the content was available
(`NimbleMessageState.cs:95-102` at `35ff5d0`), so attachment content never reaches the prompt, and the dense text that
can is `BodyText` and `QuotedText`, each truncated to that same 2500-character budget separately rather
than out of a shared pool (`:74-77` at `35ff5d0`). That is the failure the 503 above records, still present for a
content class nobody had varied. Prose runs are unaffected and their results stand.

**And the defect is now a measured range rather than an argument.** The fit budgets **bytes** against
`NumCtx`, while the number the guard compares is the SERVER's token count, which content scales by
between 2.9x and 6.3x on these shapes. **A budget in bytes cannot bound a quantity that content scales
several-fold**, and the ratio moves with the REQUEST size too, so the fit's cap is a bound on the wrong
quantity twice over. Four limits, stated rather than left for a reader: the six shapes were **chosen
rather than sampled**, so a shape that fails bounds that shape and not all content; these arms were taken
at the full 2500-character body budget, and an earlier run at 3600-byte bodies was reachable as an HTTP
request but **not as a state the adapter produces**, so its figures over-state what a message can reach;
the six arms were taken against the ENDPOINT directly rather than
through the shipping adapter and assessor, so a dense body on the real path is measured by neither run;
and whether content denser than the hex-ish arm exists, or whether non-ASCII behaves differently, has not
been measured. Size the setting against the largest evaluation
YOUR mail produces, and treat any figure here as a measurement of this lane's fixtures rather than a
bound on yours.

**And the server is not the thing at risk either way.** Its refusal boundary was measured above at
**56210 tokens or more** (a 56210-token twelve-question request returned **HTTP 200 with all twelve
answers**, raising the 48050 the state bisect gave; that request was not a state the adapter produces,
which does not matter for a claim about what the SERVER answered), and a request above its limit is
refused with HTTP 400 rather than shortened, so the failure a deployment would meet is a refused request
and not a quiet under-read. That is a statement about the **WINDOW** only: the provider shortens message bodies for
a second and unrelated reason, `MaxBodyCharacters` defaulting to **2500** (`NimbleOptions.cs:229`), so a
body or a quoted tail over that is shortened before the window is considered at all, and a message can be
shortened while the window has nothing to do with it. **A cut request in the branch where the BODY is what was cut totals `10285 - min(body, 2500)` bytes, so its SIZE is content-free** - 7785 at a 2500-character body and 8124 at 2161, both measured (`policy-`, 2026-10-02T01:15, and the two laws are `10285 = NumCtx + CAP - MARGIN + M` for the markerless branch against `NumCtx - MARGIN` for the marked one, their gap being the marker's own cost) - while what varies from message to message is the SPLIT between that total and the body it keeps. **Which of the two cuts a given message depends on
its NON-body content, and the operational end of it is MEASURED**: at the pinned window, twelve questions
and three messages per arm, a 1500-character body was shortened in **0 of 3** cases, a
2000-character body in **0 of 3**, and a 2500-character body in **3 of 3** (`corpus-`,
2026-10-02T00:38). **So the ceiling is not what cuts a given message**: a body of exactly 2500 characters
is not over `MaxBodyCharacters`, and it was the FIT that shortened it, because the two do different jobs,
the ceiling bounding how much of a message is ever offered and the fit reducing it per message until the
whole request fits (`NimbleSemanticMailClassifier.cs:402` at `35ff5d0` then the loop below it). Setting the ceiling
from the measured room instead would be wrong in the other direction, since a figure derived from a
link-rich message would truncate every link-poor one before the fit saw it. The threshold sits between
2000 and 2500 and is
not measured; and 2000 is measured-safe for THAT state shape rather than a ceiling, since a message
carrying more links, attachments or envelope gives the fit less room and can be cut at a shorter body.
At the expansion arms' own minimal state the room is larger still (6910-byte requests against a
2500-byte body, so 4410 bytes of overhead under the 8192-byte cap), which is why nothing was cut there.
Both cuts land on `BodyText` and `QuotedText`. **While something of the body survives, the row stays
`Available`** and carries a `reason` attribute naming WHICH field was cut
(`NimbleSemanticMailClassifier.cs:667-669` at `b12781a`), so **availability alone does not tell a reader whether the
read was whole** and anything consuming these rows has to look at the reason rather than the state.
**The attribute NAME on its own is not enough either**: `UnavailableEvidence` writes the same `reason`
name at `:735` at `b12781a` for a row that produced no value at all, so a consumer has to select on the name AND
the row's availability - the shortening reason sits on a row that answered, the unavailability reason
on a row that did not, under one key.
**A body cut to NOTHING is a refusal and not a weaker read** - the state is still sent, because the
fit returns the zero-body state at `:432-440` before its own `budget == 0` check at `:442-445`, but
**the row comes back `Unavailable` anyway**: with the body gone there is no content for a value to be
about, so that arm is the only one here that **leaves the covered set** (`:629-648` at `b12781a`, whose own comment
says it is the ruling). **That is the state to know about**, because leaving the covered set lowers the
coverage fraction rather than raising it, and **an `Unavailable` semantic row is what Policy's unanswered
gate refuses on** (`MailPolicyEngine.cs:512-533`) - so the emptied body is a refusal on two independent
paths rather than a row that quietly stops being security-relevant. The other terminal case is where nothing fits at all and the
state alone exceeds `NumCtx`: the fit returns nothing, and every askable row is answered with "question
set and message state exceed the configured context window" (`:199-208` at `35ff5d0`).

**It does not follow that the number the SERVER applies moved with it, and nothing in that log line
says it did.** On this transport the request is not asking the server for a window at all: the
`/api/generate` body used to carry `NumCtx` as `options.num_ctx`, and the SystemOne body has no
`options` member, so the setting is a **client-side assumption** and the server applies whatever it
applies (`NimbleOptions.cs:88-96`, and the remark in `NimbleSemanticMailClassifier.cs` headed
"UNMEASURED, and new with this shape" marks the same change, at `:368-377` as this is written). A client
cannot read the applied window back either: a truncated prompt returns
`done_reason` "stop", no warning field, and a `prompt_eval_count` describing the shortened prompt, so
the window is only knowable by saturating it (`NimbleOptions.cs:144-149`). The `llama-server` serving
this endpoint was read at 2026-10-01T23:22 carrying `-c 8194` and `--context-shift` on its command
line, reproduced by a second lane on the same pid, parent and model blob, and the provider's own
remarks (`NimbleOptions.cs:107-113`) say the Modelfile parameter `num_ctx` of 8194 is neither
`qwen35.context_length` (262144) nor the applied window. So treat `-c 8194` as a measured fact about how
the server was launched, and NOT as the window it applies. The window it does apply was measured the
same night and is nowhere near that figure: see below.

**And the 4097-token cut does NOT carry to this endpoint: what an over-long prompt meets here is a
LOUD REFUSAL rather than a cut.** The half-of-requested relation was measured where the request SET
`options.num_ctx`, and the provider's own remark says it is carried rather than re-derived
(`NimbleOptions.cs:99-104` and `:159-163`). Measured on THIS endpoint instead, 2026-10-01T23:33: a
codeword placed at the START of the body and again at the END, both arms held to the same length, is
answered at **0.999** confidence in BOTH twelve-question arms at 22584 input tokens, from a **7156-byte**
request that is inside the fit's 8192-byte cap and therefore a request the adapter can actually send; and
a bisect that moved only the state size accepted twelve answers at **48050** tokens while a 41198-byte
body returned **HTTP 400** with no answers, that last arm being well outside the cap and so evidence
about the SERVER rather than about anything a deployment meets
(`.styloagent/scratch/nimble/probe-shift.py`, `.styloagent/scratch/nimble/bisect-window.py`, `nimble-`,
2026-10-01T23:33). **And the same probe was re-run over the dense shape, because `--context-shift` drops
the OLDEST context first and a codeword at the START of the body is exactly what a shift would take**
(`nimble-`, 2026-10-02T00:04, `.styloagent/scratch/nimble/probe-shift-dense.json`; hex-ish body at 2400
characters, and **thirteen** questions, which is twelve dimensions plus the probe's own): a start-of-body
codeword is answered at **0.9987** at **46556** input tokens and an end-of-body one at **0.9984**, a
difference of 0.0003, with the one-question controls working at 0.9975 and 0.9959. So the server applies
a window of **at least 56210 tokens**, and it reads a **46556**-token dense prompt whole. **That is a
reading of the SERVER and not a state a deployment sends**: thirteen questions is one more than the
dimension set, so the largest evaluation an adapter-producible request has been measured to reach is
**43202**, and the probe sits above it, which is the direction a safety reading should err in. INFERRED
rather than measured: that this server never silently truncates, which rests on requests being read whole
at those sizes and on a loud refusal above the limit, rather than on a direct reading of the window.
**The console consequence runs the opposite way to the one this section first implied:** on this
transport the truncation backstop is not catching a server that shifts, it is the thing that refused a
request the server would have answered, which is exactly the 503 above, and the number it should be set
to is derived from the server's measured capacity rather than from a carried ratio.

**The terms the cut arithmetic is made of, since the captures are read rather than the derivation.** The
un-cut regime totals `O-nought + body` and the cut regime `O-nought + 105 + kept`, where **`O-nought` is the request's
overhead with no marker and no body**, 8192 - `L`, and the **105** is what the two marker fields cost
once the state is embedded in the request. **It reads as 105 BYTES and is larger than the fields' own
text, because embedding escapes them**: the separator, escaped newline and indent twice, the keys, and
the digits of the kept length account for the difference (`conversation-`, 2026-10-02T06:32 decomposes it
to the byte). **That 105 is the four-digit case**: a `kept` of one digit makes the same marker **102 bytes**,
measured by removing it from a captured request whose body was emptied, so **`M` moves with the digits of
the kept length and is not a constant** - which the threshold below inherits. For the message the three-point
bisect pins, `O-nought` is 8192 - 2160 = **6032**. **The two regimes CROSS, so a total on its own identifies no
body**: an un-cut 2092-byte body and a cut 2161-byte one both total 8124, and one of those is a measured
arm. **The cut form holds while the second pass still leaves the request above `NumCtx`**, which for the
bisected message means a body of **2093** or more - and that threshold **sums a byte term with a
character one**, since `105` and the `512`-byte margin are bytes while the `2500` body budget is
characters, so it is exact only where a character costs a byte and is a **LOWER bound** for any body the
adapter escapes, where a character can cost up to seven bytes. A shorter body sends the loop round again,
and **the total it lands on is a property of the STATE rather than a constant**: it is `NumCtx - MARGIN` =
**7680** BYTES in the state the bisect pins, while a tailed state lands at **7512** on the capture the next
clause cites - because the loop cuts CHARACTERS to fit BYTES, so a state with a different overhead has a
different plateau. **The BODY range where the budget stops binding is the part that carries over**: down to
about `CAP - MARGIN` = 1988 CHARACTERS, below which the body is the smaller term and the total moves with
it again (`policy-`, 2026-10-02T01:35, and `overview-`'s reading of the 7512 capture, 2026-10-02T06:39). Across these fixtures the
band of body lengths whose outcome depends on which message carries them is **340 wide**, which is the
reason a fixture has to declare where it sits rather than quote a boundary.

#### Measured performance

The figures below are this lane's own measurements on the reference machine, serving `nimble:latest`
through Ollama on loopback. Each carries its predicate and the artifact it came from; those artifacts
are working files on the reference machine, not part of the repository.

| Quantity | Marked | Value | Predicate and source |
| --- | --- | --- | --- |
| Model and server | MEASURED | `Bespoke-Nimble-9B-merged-current-Q8_0.gguf`, family `qwen35`, `9.0B`, `Q8_0`, served by Ollama `0.35.0` | What the server at `127.0.0.1:11435` reported for `nimble:latest` at 2026-09-30T20:32:30+0100. `nimble-survey4.json:89` (version) and `:97` to `:104` (model details). |
| Applied prompt window | MEASURED, `nimble-request-shape/1` | `4098` tokens with `8192` requested | The plateau of `prompt_eval_count` for a prompt larger than the window, which is the window the server applied. `nimble-shipping-shape-window.json:6` to `:8`, an artifact dated 2026-09-30T23:20:33+0100; the identical block sits at `nimble-window-check.json:10` to `:16`, which carries no date of its own. The same probe recorded `2050` with `4096` requested and `8194` with `16384` requested (`nimble-window-check.json:2` to `:25`). |
| One full assessment | MEASURED, `nimble-request-shape/1` | 25.13 s to 48.172 s, median 36.234 s, six messages | Wall time per message for the shape the adapter sends: `/api/generate` with 12 dimensions per request, every dimension answered. 2026-09-30T23:20:33+0100. The six cases are at `nimble-shipping-shape-window.json:23`, `:55`, `:87`, `:119`, `:151`, `:183`; the range runs from `:152` (25.13, the fastest) to `:184` (48.172, the slowest), and the median is over those six. |
| One dimension asked alone, model resident | MEASURED | median 2.307 s, min 1.615 s, max 103.259 s, twelve calls | The tool's own summary lines, `nimble-survey4.json:381` to `:383`. The max is one stalled call. A repeat of eight calls reported `median_seconds` 1.751 and `max_seconds` 16.441 (`nimble-stall.json:136` and `:137`), whose note at `:138` says a max near the median there "does not prove it cannot." |

The `nimble-request-shape/1` mark names the transport both rows' predicates describe. The adapter's shape is now `nimble-request-shape/2` (`NimbleQuestionSet.cs:63`) and the default endpoint ends in `/v1/systemone` (`NimbleOptions.cs:56`), so those figures stand as measurements of the transport they name and not of the one that ships. The window row's own source states the condition outright: `NimbleOptions.cs:99-104` records that the relation was measured under the OLD request, and that every statement on `AppliedContextWindow` is a measurement of the previous transport until it is re-taken.

Not measured here: memory footprint, disk size, and behaviour under concurrent requests.

`NeverAsks` is not a provider: it is the deployment stating that it has none and will not ask one.
The semantic tier then answers every dimension `NotApplicable`, which this design reads as "the
question exists and was not asked, so nothing was lost". **It is a declaration about the deployment,
never a reading of a provider's health**, and the two are deliberately not interchangeable: an
unreachable provider produces `Unavailable` rows, and a deployment that never asks produces
`NotApplicable` ones. Policy must be able to tell those apart, and a blackout must never be
convertible into a delivery by changing a setting.

Two things follow from that, and they are why it is a name an operator writes down:

- **It is never inferred.** The default stays `Jev`, so a deployment that names nothing and holds no
  key is the half-configured refusal it already was, rather than one quietly running without semantic
  evidence. A forgotten secret cannot become this selection.
- **It holds no credential.** Like `Nimble` it needs the profile master key and nothing else. A
  provider key left in the environment is not a reason to refuse: a deployment may carry a secret it
  does not use, so it is **announced in the log at startup** rather than rejected.

**What selecting it buys, and what it costs, stated together because they arrive together.** A
`NotApplicable` semantic row is a question that was never asked, so it leaves the coverage
denominator rather than sitting in it as a permanent shortfall (decision 44). Two consequences
follow, and only the first is the one the tier was asked for:

- **One measured deterministic row is enough to clear the allow floor.** `MinimumCoverageForAllow` is
  a fraction of the weight that was counted. Once the semantic rows leave, the counted weight is the
  deterministic weight alone, so a deployment that declares never-asks and measures a single
  deterministic finding sits at full coverage and can be allowed at the floor's default. Before
  decision 44 the semantic backbone sat in the denominator, so the same deterministic weight was
  measured against questions the deployment had never been asked, and the floor was clearable only
  by a message carrying a large share of the deterministic weight. A single measured row was not.
  This consequence is **derived** from the scorer's arithmetic and **asserted through the engine** on a
  fixture that carries one measured deterministic row
  (`ANeverAskingDeploymentClearsTheAllowFloorOnAMeasuredDeterministicRowAlone`,
  `tests/StyloMail.Assessment.Tests/MailAssessorTests.cs`). It has **not** been measured on a running
  Host: the prebuilt assembly is the only build on disk and no earlier run recorded its build, so the
  before/after pair a wire reading would need cannot be attributed to a build by anyone now. That
  reading is pre-registered and staged separately, and the sentence above states what the arithmetic
  says rather than what a Host has been observed to do.
- **The same removal lifts the cap that sat above the allow floor.** Coverage can now rise past
  `MinimumCoverageForIrreversibleAction`, so a deployment whose semantic rows never arrive can
  quarantine on deterministic evidence alone. That path is measured on a real Host rather than
  inferred, and how it should be bounded is open: the corroboration gate in policy bounds the
  low-index allow path only, and says nothing about the irreversible one.

**A declaration with nothing measured behind it still holds**, and that is a separate statement
rather than what remains of the old one. Where no deterministic row is measured either, the counted
set is empty and the hold names coverage rather than the semantic blackout, so it is not being
mistaken for an outage. Everything the deployment does measure is measured for real; what it does not
measure is not silently treated as clean.

Three things follow from the choice, and they are the reason it is a named decision rather than a
fallback:

- **What the deployment must hold changes.** `Jev` needs a provider key and the profile master key.
  `Nimble` and `NeverAsks` need the master key alone, because neither holds a credential to pair with.
  The refusal messages name the missing variable, and they only name the ones the selected provider
  actually reads.
- **An unrecognised name refuses to start**, listing the names it accepts. A typo that quietly
  selected the hosted provider would send a deployment's mail content to a third party while the
  operator believed a local model was reading it. A bare number is not a name either, so `1` is
  refused rather than silently meaning the second entry.
- **A missing secret is not the same failure as a bad name.** A name that means nothing is a startup
  refusal. A missing master key starts the process, logs
  `STYLOMAIL ASSESSMENT NOT CONFIGURED`, and leaves `/health/ready` reporting `not_ready` with
  `assessor_unavailable`, so the deployment is visibly not ready rather than quietly allowing mail.

The selection is read once, at startup, where the assessor is resolved; changing it takes a restart.

### Transport configuration

| Key (under `StyloMail:Transport:`) | Default | Notes |
| --- | --- | --- |
| `SmtpIngress:Enabled` | `false` | |
| `SmtpIngress:BindAddress` / `:Port` | `127.0.0.1` / `2525` | Loopback by default; port `0` asks the OS to choose. |
| `SmtpIngress:ServerName` | `stylomail` | Must appear in `LocalHostIdentities`, or startup refuses. |
| `SmtpIngress:CertificatePath` | *(none)* | PKCS#12, for `STARTTLS`. A path is not a secret. |
| `SmtpIngress:RequireEncryption` | `true` | **On by default.** With no certificate there is no `STARTTLS` and therefore no `AUTH`, so enabling the listener without one refuses to start rather than answering rejection to every client. |
| `SmtpIngress:AllowUnauthenticatedInbound` | `true` | The trusted-MTA handoff case, constrained by `RecipientDomains`. |
| `SmtpIngress:RecipientDomains` | *(empty)* | **Empty means no inbound.** An empty policy is not "unrestricted". |
| `SmtpIngress:InboundTenantId` | `inbound` | The tenant unauthenticated inbound mail is attributed to. |
| `SmtpIngress:LocalHostIdentities` | *(empty)* | Names this system is known by, for the loop guard. |
| `SmtpIngress:MaxMessageBytes` | 64 MB | Asserted against the queue's bound at startup. |
| `CloudflareIngress:Enabled` | `false` | |
| `CloudflareIngress:RecipientDomains` | *(empty)* | The inbound authorisation model, as above. |
| `CloudflareIngress:MaxMessageBytes` | 64 MB | Also raises the server's request body limit for this route. |
| `Upstream:Host` / `:Port` | *(none)* / `587` | **Unset means this deployment does not deliver.** |
| `Upstream:Tls` | `Required` | `Required` / `Opportunistic` / `None`. Credentials with `None` are refused. |

### Live traffic

**Off by default, and off is a complete deployment.** The hub exists so the operator console reflects
traffic as it happens rather than when someone refreshes, and a deployment that has not enabled it
loses immediacy and nothing else: the console polls, which is what it does when the hub is
unreachable anyway.

| Key (under `StyloMail:Traffic:`) | Default | Notes |
| --- | --- | --- |
| `Enabled` | `false` | Maps the hub at `/v1/traffic`, `Review` privilege. Off, that route is **absent** and answers `404`. |

Three things to know before turning it on:

- **An event is a hint, never state.** A notice says that a row moved and gives its identifier; the
  console re-reads the row over HTTP. Nothing here pushes a verdict, an action, or which way a
  control moved, so a dropped, duplicated or reordered notice cannot leave a screen wrong.
- **No pipeline code depends on the hub.** Emission is fire-and-forget through a port whose default
  implementation does nothing, so a hub outage is invisible to mail. Enabling this cannot change what
  this deployment does with a message.
- **The API key is presented in a header**, on the negotiate request and on the WebSocket handshake,
  never in a query string. This host reads no token from the URL at all.

A client asks for `negotiateVersion=1` and subscribes to one method, `"traffic"`, receiving
`{ kind, subjectId, occurredAt }`. Which rows a console hears about is decided by the tenant its key
resolved to, named in an `X-StyloMail-Key` header: there is no request that widens it.

### The semantic provider

| Key | Default | Notes |
| --- | --- | --- |
| `StyloMail:Jev:Endpoint` | the hosted TypeSafe endpoint | **Redirects message content**, so a non-default value is announced in the startup log. Setting it is a legitimate operator decision, a local classifier, staging, or deliberately unreachable to exercise the semantic-unavailable path, and the announcement is what keeps it a decision rather than an accident. |
| `StyloMail:Jev:Model` | the pinned versioned id | Bound so it can be changed deliberately. An alias here would move without notice and silently invalidate memoised assessments. |

A rejected credential at this endpoint is what makes `provider_credential` appear in `/health/ready`:
see the readiness section above.

`StyloMail:Auth:Principals:<n>:ApprovedSenderIdentities:<n>` lists the identities an authenticated
principal may use in SMTP `MAIL FROM`. **Empty authorises nothing** beyond the null sender: "no
restriction configured" and "may send as anyone" must not be the same value. `key create --sender`
grants the same thing to a minted principal, and starts empty for the same reason.

| Key (under `StyloMail:Auth:`) | Default | Notes |
| --- | --- | --- |
| `Principals:<n>:PrincipalId` / `:Key` / `:TenantId` / `:Privileges:<n>` | *(none)* | A principal held in configuration. Honoured, and **read-only**: see §7. |
| `ResolutionCacheLifetime` | `30s` | How long a verified minted key is held before it is verified again. `0` verifies on every request, at one key derivation (~71 ms) per request. |

---

## 7. Minted API keys

**Two sources of identity, and the store wins wholesale.** A key minted on the host is held as a
digest in the host database; a principal in `StyloMail:Auth:Principals` still authenticates, so
existing deployments keep working. Where the same principal is in both, the store's entry resolves
and the configuration entry resolves **nothing**, with any key: privileges and keys are never
unioned across the two. That is deliberate: a union is how a configuration entry silently re-widens a
privilege an operator narrowed when they minted its replacement.

```
$ stylomail key create --principal ops@acme --tenant acme \
      --privileges Review,Administer --sender ops@acme.example --by alice
smk_<32 hex>_<43 base64url>
This key is shown once and will not be shown again. Only a digest of it is stored, so it
cannot be recovered or re-displayed: mint a new key if this one is lost.

$ stylomail key list
principal         tenant  privileges          source       status
ops@acme          acme    Review, Administer  store        active
user-acme-sender  acme    Assess, Send        environment  read-only

$ stylomail key revoke --principal ops@acme --by alice
```

What the host promises, and how it was checked:

- **The value is shown once, on stdout, and to nothing else.** No `--output`, no file flag, never a
  log, never stderr. Only a per-key salt and a slow-KDF digest (PBKDF2-HMAC-SHA256, 600,000
  iterations, ~71 ms) reach the store, so a copy of the database is neither a usable credential nor a
  cheap one to attack offline. Verified by searching the database file and its write-ahead log for
  the minted value: absent.
- **`key list` reports which source resolved each principal** and never the key or its digest. An
  environment entry the store has claimed is listed as `shadowed by store`, so an operator sees their
  configuration entry go inert rather than discovering it as a credential that stopped working.
- **Revocation takes effect on the next request, in every process.** The store carries a change
  counter that every mint and every revocation increments; a resolution cache validates against it,
  which is what lets a `key revoke` in one process stop a key being served in another. A revocation
  is never un-revoked, and re-minting the same name afterwards is the supported path.
- **`key revoke` refuses a configuration principal and names the entry that owns it**, exiting `3`
  rather than reporting a revocation that did not happen. It says why the fix needs a restart: the
  principals section is read once at startup.
- **Both channels authenticate through one resolution.** The HTTP surface and the SMTP submission
  listener resolve through the same directory, so a revocation cannot reach one and miss the other.
  Verified against a running host: a minted key authenticates over `STARTTLS` + `AUTH`, its
  `--sender` grant is what decides an SMTP `MAIL FROM`, and revoking it produced `401` on HTTP and
  `535` on SMTP with no restart.

---

## 8. Verifying a running instance

| Route | Meaning |
| --- | --- |
| `GET /health/live` | `200 {"status":"live"}`. Is the process up? Deliberately checks nothing else: a dependency outage is not a reason to have a container restarted. |
| `GET /health/ready` | `200 {"status":"ready"}` or `503 {"status":"not_ready","failedChecks":[...]}`. Checks: `database`, `spool`, `provider_credential`. |
| `GET /metrics` | Prometheus text. |

**Readiness is a probe, not a flag set at startup.** It asks "can this host durably accept mail right
now?" by reading the database and writing a file into the spool, so it notices a volume that filled or
was unmounted *while the process ran*:

```
$ curl -s 127.0.0.1:8080/health/ready
{"status":"ready"}

$ chmod 500 /var/lib/stylomail/spool      # take the permission away underneath it
$ curl -s -o /dev/null -w '%{http_code}\n' 127.0.0.1:8080/health/ready
503
$ curl -s 127.0.0.1:8080/health/ready
{"status":"not_ready","failedChecks":["spool"]}
```

That is the behaviour verified here, including the return to `200` once the spool is writable again.
A host that cannot durably accept mail must stop advertising itself, or a load balancer keeps handing
it messages that will be refused, turning a local storage fault into a delivery outage. The failed
check is **named, never described**: the exception text can carry a filesystem path, and this route
is served without credentials.

### A rotated provider key makes the host not ready

The `provider_credential` check fires when the semantic provider has **rejected this deployment's
credential**: a rotated or revoked Jev key. The host cannot assess a message without semantic
evidence, so a host that cannot assess stops advertising itself rather than accepting mail it will
fail to judge:

```
$ curl -s -o /dev/null -w '%{http_code}\n' 127.0.0.1:8080/health/live    # 200: still live
$ curl -s 127.0.0.1:8080/health/ready
{"status":"not_ready","failedChecks":["provider_credential"]}
```

Two things about that are deliberate. **Liveness stays `200`**: a rejected key is a configuration
fault, and restarting the container changes nothing about it, so failing liveness would turn one bad
deploy into a restart loop. And **the check reflects what the provider actually answered**, never what
is configured: a key that is merely set but untested does not affect readiness, because a probe that
guessed would be wrong in both directions.

It latches until a classification succeeds, which cannot happen while the key is bad. That direction is
the only safe one: a check that cleared itself would put the host back to advertising ready while every
message kept losing its semantic evidence.

Health, readiness and metrics must never carry message content, an identity or a tenant. None of them
does, and that is a property worth re-checking if either route is ever extended.

### A port that answers is not necessarily this host

**On macOS, `127.0.0.1:5000` and `:7000` are AirPlay Receiver, served by `ControlCenter`, not free
ports.** They answer `403` with an HTML body, so a reader who binds the host to port 5000 and curls
`/health/ready` gets a plausible-looking refusal from a process that has nothing to do with StyloMail,
and concludes the host is refusing them. This document used `:5000` in its own examples until it was
caught, which is exactly the hazard: **this mistake produces a wrong diagnosis rather than an obvious
failure.**

If a response from one of these routes is not what you expect, check who is actually holding the port
before investigating the host:

```
$ lsof -nP -iTCP:8080 -sTCP:LISTEN
COMMAND   PID          USER   FD   TYPE  NODE NAME
dotnet    1234 scottgalloway   12u  IPv4  TCP 127.0.0.1:8080 (LISTEN)
```

If that is not `dotnet`, you are talking to something else. The examples here use `8080`, which is free
on a stock macOS.

### Seeing what the host is holding

Two authenticated reads answer "what is in here", both requiring `Review` and both scoped to the
caller's own tenant: neither takes a tenant parameter, so a cross-tenant read is *absent* rather
than refused:

```
GET /v1/senders                               the principals this tenant can send as, with pause
                                              state and which source holds each one
GET /v1/messages?state=held&limit=50&after=…  mail awaiting a decision, per-recipient progress
GET /v1/decisions?action=Quarantine&after=…   the explainable ledger, newest first
```

`state` accepts `awaiting_decision` (the default), `held` and `quarantined`. **Messages in normal
delivery are not enumerable**, and asking for `state=queued` is a named `400` rather than a silent
fallback: this lists what is awaiting a *decision*, which is the queue's own notion of a listing,
and filtering a page after it has been cut would produce short pages and a wrong `hasMore`.

`GET /v1/senders` never returns a credential, and it now draws on **both** sources of identity. Each
row carries `source`: `store` for a key minted on this host, `environment` for one configured in it.
A sender that is both is listed **once**, as a `store` sender: wholesale precedence means the
configuration entry resolves nothing, and a sender disappearing from the operator's view because
someone minted a key for it would be worse than one shown with the wrong provenance. A principal that
cannot authenticate is not listed at all, which is the same rule that already excluded a
configuration entry with no key.

The response names each field it carries rather than serialising a record that holds a credential:
the failure mode of the alternative is publishing every key on the host. It is built from the
principal inventory, which has no key and no digest to leak in the first place.

`GET /v1/decisions` returns **summaries, not whole decisions**: each row carries the action, the
ordered reasons, the versions the decision was made under and its coverage flags, and the full
explanation with its evidence is one `GET /v1/decisions/{id}` away. A page of complete decisions would
be unbounded, because evidence volume is per-message. `action` filters by exact action; a value the
ledger does not record, or a cursor the ledger did not issue, is a **named `400`** rather than a
silently different result: the same rule as `state` on the message listing, and for the same reason:
a page that quietly shows something other than what it claims is worse than a refusal.

---

## 9. CLI exit codes

| Code | Meaning | Example |
| --- | --- | --- |
| `0` | Success. | `assess message.eml` |
| `2` | Bad input: unknown command, missing argument, unreadable file. | `assess missing.eml`, `profiles inspect` without `--tenant` |
| `3` | A requested capability is unavailable. | `assess --semantic` on a deployment with no assessor configured; `key revoke` on a principal held in configuration |
| `134` | The process refused to start (`SIGABRT`, from an unhandled configuration error). | see §2 |

Exit codes are meaningful on purpose: a CLI that reported failure as success would be trusted by
scripts, which is the worst place to be wrong.

Two behaviours worth knowing:

- **`assess` never transmits message content** unless `--semantic` is passed explicitly. Without it,
  the provider step is skipped and says so, so a local assessment cannot become a disclosure by
  accident.
- **`replay` runs against a fixed instant**, so two runs over the same fixtures are byte-identical:
  which is what makes it usable for comparing policy versions.
