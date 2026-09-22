# `desktop-` — the Avalonia operator console

## Your scope
Own `src/StyloMail.Desktop/` and `tests/StyloMail.Desktop.Tests/`. Create them.
Do not modify any other project. Other agents own them.

## Read these first, in this order

1. **`.styloagent/spec.md` section 10** — the design of record for this console. It contains the
   decisions; do not re-derive them.
2. **`.styloagent/architecture.md`** — where the console sits.
3. **The sibling project you are modelling on**, which is local and readable:
   **`/Users/scottgalloway/RiderProjects/lucidview/LucidReader`** (the app, shipped as `mylo`) and
   **`/Users/scottgalloway/RiderProjects/lucidview/LucidReader.Core`** (its library).
   Study `App.axaml`, `Views/MainWindow.axaml`, `Styles/Reader.axaml`, `ReaderServices.cs`, and the
   `Services/` and `Models/` folders. **Reuse its structure and idioms rather than inventing your
   own.** Note especially: the Coreplusapp split, `Models/` for view models, `Services/` for
   platform seams, `UserManual.cs` as an in-app document, and a macOS-aware notification sink.

## What you are building

An Avalonia desktop application, visually modelled on **Apple Mail**, for managing senders, messages
and decisions. The three-pane shape is the target: sidebar (senders, queues, saved views), a message
list, and a detail pane showing the decision.

## The decision that constrains everything

**The console talks to the Host HTTP API and nothing else.** It references no other StyloMail project
except, at most, DTOs. Reasons are in spec section 10.1; the practical consequences are:

- It needs no local database and holds no credential but its own API key.
- If the console cannot do something through the API, **the API is missing a route** and you should
  `send_message` `ingress-` (who owns the Host) rather than reaching past it. That is a feature, not
  an obstacle: a headless deployment needs the same route.
- Everything the console does lands in the same decision ledger as everything else, because it is the
  same requests.

## The API you are consuming

Existing routes are in `src/StyloMail.Host/Endpoints/`. Read them rather than guessing:

- `POST /v1/assessments`, `POST /v1/submissions`, `GET /v1/submissions/{id}`
- `GET /v1/decisions/{id}` — evidence, reasons, versions, coverage
- `POST /v1/feedback`
- `POST /v1/quarantine/{id}/release` — audited, requires `decidedBy`
- `POST /v1/controls/senders/{id}/pause` and `/resume`
- `/health/live`, `/health/ready`, `/metrics`

Authentication is an API key or a cookie session channel; read `Auth/` before designing the client's
auth. **Missing surface you will likely need: a sender listing.** Ask `ingress-` for it rather than
reading the database.

## Hard constraints

1. **Nothing in the console may be the only way to do something.** Every action it offers must exist
   as a route, so a scripted deployment is not second-class.
2. **Never render a credential.** The API key is entered once and stored in the platform keychain
   (see mylo's `MacUserNotificationSink` for the platform-seam idiom). Never a config file, never a
   log, never a view.
3. **Explainability over decoration.** The decision pane answers "why was this held": evidence and
   ordered reason codes, not a single score. This console is for operators; do not simplify it into
   something sender-facing.
4. **It is not a mailbox.** It manages StyloMail's own queue and ledger. Do not add mail reading.
5. **No em-dashes** in any UI string, comment, or document. Use a colon or full stop.
6. **Injected `TimeProvider`** if you display or compute times; no `DateTimeOffset.UtcNow` in logic.

## Environment note

`dotnet` is **not on PATH**. Every shell:
`export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`
SDK is 10.0.201. Judge your work with
`dotnet test tests/StyloMail.Desktop.Tests/StyloMail.Desktop.Tests.csproj`, never the solution build.
Analyzers run as **errors**.

## Start here

**First deliverable: a headless API client plus a test, before any UI.** A typed client over the
routes, with a test that runs it against a stubbed handler. That proves the contract and gives you
the DTOs the views will bind to. **Do not start with XAML.**

Then the shell window: three panes, sidebar populated from the API, and a message list. Get that
running and send me a screenshot description before building the detail pane.

## Report to `overview-`
Files created, test count, whether the console can reach a running Host, contract friction, and
anything you could not do because a route is missing. If blocked, `send_message` immediately. Do not
yield silently.