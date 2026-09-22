# Running StyloMail

What the `StyloMail.Host` executable does, how to run it, and how to tell whether a running instance
is healthy.

**Every claim here was checked against a running process, not read off `Program.cs`.** That
distinction is the whole point: `Program.cs` is commented in more detail than this file, and a
comment is a claim about code rather than evidence about it. The checks are a script that starts the
real binary, drives real sockets and real HTTP requests, and asserts on what came back — 49 of them,
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
first thing to drift would be a safety default — the storage path, or the assessor that refuses
everything when nothing is configured. An unrecognised command exits `2` rather than falling through
to `serve`: a typo silently opening a network listener is a surprising thing for a mail component
to do.

The CLI commands run **one-shot** — they build a host, do their work, and exit. They do not start
hosted services, so a CLI command never opens the SMTP port or runs the delivery worker.

---

## 2. What happens at startup, in order

```
1.  parse arguments; anything but `serve` becomes a one-shot CLI command and returns here
2.  build the web application and its service collection
3.  InitialiseStorageAsync      — the host's schema, the persistence schema, the queue's
4.  resolve IMailAssessor       — forced, not lazy
5.  resolve ISmtpIngressSink    — forced, not lazy; this is where the composition assertions run
6.  log the transport description
7.  UseAuthentication → CsrfMiddleware → UseAuthorization
8.  map routes (and conditionally map the Cloudflare intake)
9.  Run
```

Steps 4 and 5 are the interesting ones. **Both components are registered as lazy singletons, so
without forcing them a misconfigured deployment would boot, answer `/health/ready` with 200, and fail
only when real mail arrived** — a misconfiguration that looks like a healthy service. Forcing the
resolution turns each into a startup failure instead.

Verified by running, with the process failing to start on each:

| Configuration | Observed |
| --- | --- |
| `TYPESAFE_API_KEY` set, `STYLOMAIL_PROFILE_KEY` absent | refuses to start, names the missing variable |
| both set, profile key under 32 bytes | refuses to start, names the variable **and the length** |
| ingress bound larger than the queue's | refuses to start, names **both** values |
| Cloudflare intake enabled with no secret | refuses to start, names the variable |

**These refusals are unhandled exceptions, so the process aborts.** In a shell that is exit code
`134` (`SIGABRT`), not a tidy `1`. That is deliberate — the message names the variable an operator has
to fix — but a script checking `$?` should test for *non-zero* rather than for a specific code.

### The composition assertions

Built into step 5, they check two properties that belong to a **pair** of components and are therefore
invisible to a reader of either one:

- **`SmtpIngressOptions.MaxMessageBytes <= QueueOptions.MaxPayloadBytes`.** If the ingress will take
  messages the queue will not store, the ingress reads and authorises the message, the sink spools
  it, and the queue refuses it — so the caller sees a **capacity deferral that looks like spool
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
requirement. For health that is a necessity — a probe that has to authenticate cannot do its job, and
a credential handed to a load balancer is a credential in one more place — and the compensation is
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
one that catches it. Nothing here is an error the host refuses to start over — an inbound-only
deployment and a submission-only one are both legitimate shapes — but without the warning the mistake
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
| `ASPNETCORE_URLS` | Where Kestrel binds, e.g. `http://127.0.0.1:8080`. **Avoid 5000 and 7000 on macOS** — see the note below. |

**These four are read from the environment directly, and the three secrets are read from nowhere
else.** A secret given a configuration key ends up in an `appsettings` file, then in a repository,
then in an image layer — and a key committed to a repository must be treated as compromised and
rotated rather than merely deleted. There is deliberately **no configuration property** for any of
them; the names live as constants beside `HostCredentials` so they are defined once and greppable.

Two consequences worth knowing:

- **`TYPESAFE_API_KEY` and `STYLOMAIL_PROFILE_KEY` are all-or-nothing.** Exactly one of them set is a
  misconfiguration and the process refuses to start. Running with the Jev key live and no
  pseudonymisation key would work perfectly in testing and quietly collapse tenant isolation in
  production.
- **Changing `STYLOMAIL_PROFILE_KEY` is a migration, not a configuration change.** Every stored
  profile key was derived from it and becomes unreadable.

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
| `StyloMail:Jev:Endpoint` | the hosted TypeSafe endpoint | **Redirects message content**, so a non-default value is announced in the startup log. Setting it is a legitimate operator decision — a local classifier, staging, or deliberately unreachable to exercise the semantic-unavailable path — and the announcement is what keeps it a decision rather than an accident. |
| `StyloMail:Jev:Model` | the pinned versioned id | Bound so it can be changed deliberately. An alias here would move without notice and silently invalidate memoised assessments. |

A rejected credential at this endpoint is what makes `provider_credential` appear in `/health/ready` —
see the readiness section above.

`StyloMail:Auth:Principals:<n>:ApprovedSenderIdentities:<n>` lists the identities an authenticated
principal may use in SMTP `MAIL FROM`. **Empty authorises nothing** beyond the null sender — "no
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
and the configuration entry resolves **nothing**, with any key — privileges and keys are never
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
| `GET /health/live` | `200 {"status":"live"}`. Is the process up? Deliberately checks nothing else — a dependency outage is not a reason to have a container restarted. |
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
check is **named, never described** — the exception text can carry a filesystem path, and this route
is served without credentials.

### A rotated provider key makes the host not ready

The `provider_credential` check fires when the semantic provider has **rejected this deployment's
credential** — a rotated or revoked Jev key. The host cannot assess a message without semantic
evidence, so a host that cannot assess stops advertising itself rather than accepting mail it will
fail to judge:

```
$ curl -s -o /dev/null -w '%{http_code}\n' 127.0.0.1:8080/health/live    # 200 — still live
$ curl -s 127.0.0.1:8080/health/ready
{"status":"not_ready","failedChecks":["provider_credential"]}
```

Two things about that are deliberate. **Liveness stays `200`**: a rejected key is a configuration
fault, and restarting the container changes nothing about it, so failing liveness would turn one bad
deploy into a restart loop. And **the check reflects what the provider actually answered**, never what
is configured — a key that is merely set but untested does not affect readiness, because a probe that
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
caller's own tenant — neither takes a tenant parameter, so a cross-tenant read is *absent* rather
than refused:

```
GET /v1/senders                               the principals this tenant can send as, with pause
                                              state and which source holds each one
GET /v1/messages?state=held&limit=50&after=…  mail awaiting a decision, per-recipient progress
GET /v1/decisions?action=Quarantine&after=…   the explainable ledger, newest first
```

`state` accepts `awaiting_decision` (the default), `held` and `quarantined`. **Messages in normal
delivery are not enumerable**, and asking for `state=queued` is a named `400` rather than a silent
fallback — this lists what is awaiting a *decision*, which is the queue's own notion of a listing,
and filtering a page after it has been cut would produce short pages and a wrong `hasMore`.

`GET /v1/senders` never returns a credential, and it now draws on **both** sources of identity. Each
row carries `source`: `store` for a key minted on this host, `environment` for one configured in it.
A sender that is both is listed **once**, as a `store` sender — wholesale precedence means the
configuration entry resolves nothing, and a sender disappearing from the operator's view because
someone minted a key for it would be worse than one shown with the wrong provenance. A principal that
cannot authenticate is not listed at all, which is the same rule that already excluded a
configuration entry with no key.

The response names each field it carries rather than serialising a record that holds a credential —
the failure mode of the alternative is publishing every key on the host. It is built from the
principal inventory, which has no key and no digest to leak in the first place.

`GET /v1/decisions` returns **summaries, not whole decisions**: each row carries the action, the
ordered reasons, the versions the decision was made under and its coverage flags, and the full
explanation with its evidence is one `GET /v1/decisions/{id}` away. A page of complete decisions would
be unbounded, because evidence volume is per-message. `action` filters by exact action; a value the
ledger does not record, or a cursor the ledger did not issue, is a **named `400`** rather than a
silently different result — the same rule as `state` on the message listing, and for the same reason:
a page that quietly shows something other than what it claims is worse than a refusal.

---

## 9. CLI exit codes

| Code | Meaning | Example |
| --- | --- | --- |
| `0` | Success. | `assess message.eml` |
| `2` | Bad input — unknown command, missing argument, unreadable file. | `assess missing.eml`, `profiles inspect` without `--tenant` |
| `3` | A requested capability is unavailable. | `assess --semantic` on a deployment with no assessor configured; `key revoke` on a principal held in configuration |
| `134` | The process refused to start (`SIGABRT`, from an unhandled configuration error). | see §2 |

Exit codes are meaningful on purpose: a CLI that reported failure as success would be trusted by
scripts, which is the worst place to be wrong.

Two behaviours worth knowing:

- **`assess` never transmits message content** unless `--semantic` is passed explicitly. Without it,
  the provider step is skipped and says so, so a local assessment cannot become a disclosure by
  accident.
- **`replay` runs against a fixed instant**, so two runs over the same fixtures are byte-identical —
  which is what makes it usable for comparing policy versions.
