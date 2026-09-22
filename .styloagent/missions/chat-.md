# chat- : the chat channel extension

You own StyloMail's second channel family. The operator asked for Slack and Discord support with an
algorithmic checking layer, the design is agreed and committed, and your first deliverable is the Core
contract that the rest of it builds on.

## Read first, in this order

1. **`docs/chat-channels-plan-01-core-contract.md`**. This is your first deliverable, written as a
   task-by-task plan with the code in it. Execute it in order, test first.
2. **`docs/chat-channels-design.md`**. The design of record for everything after plan 1.
3. **`.styloagent/spec.md` section 13**, for the identity and boundary statement, and §4 for the
   privacy stance you will be held to later.
4. `.styloagent/architecture.md` for the component view and who owns what.

## The one fact that shapes everything you build

Email lets StyloMail decide before delivery. A normal Slack app does not: it receives a `message`
event **after** Slack has delivered the message. So this is an **observer with post-hoc
interventions, never a proxy**, and every chat assessment records `DeliveryTiming: PostDelivery` as a
stated fact rather than something a reader infers. That is why plan 1 makes the property `required`
rather than defaulted, and it is the claim you must never let the system make by accident.

## Your lane, and the files you may touch while the owners are parked

Your first deliverable is a contract change that crosses four projects. **The owners of those
projects are parked**, so you may make the mechanical changes the plan specifies:

- `src/StyloMail.Core` (the three new types, `MailAssessment`, `MailAnalysisInput`)
- `src/StyloMail.Assessment/MailAssessor.cs` (the one construction site)
- `src/StyloMail.Mime/BoundedMimeMessageAnalyzer.cs` and `src/StyloMail.Host/Hosting/HostIngressSink.cs`
- the four test files the plan names

**Change nothing else in those projects.** No refactoring around yourself, no renaming, no cleanup you
were not asked for. If you find something that needs changing outside the plan, report it rather than
doing it.

**Do not touch:** `src/StyloMail.Desktop` (owned by `desktop-`), anything under `src/StyloMail.Host/Traffic/`
or `tests/StyloMail.Host.Tests/Traffic*` (owned by `hub-`, which is still finishing), or
`src/StyloMail.Queue`, `src/StyloMail.Adaptive`, `src/StyloMail.Policy`, `src/StyloMail.Transport`,
`src/StyloMail.AccessProxy`. Run `git status` before you start and if anything you planned to edit is
already modified by someone else, stop and tell `overview-`.

## The rules that bind every lane here

- **Do not run `git add` or `git commit`.** Leave your work in the tree and report; I verify the
  solution and commit the lane. **Never `git commit --amend` and never `git reset`**: several agents
  share this tree and I have already destroyed one commit that way today.
- Build with `export DOTNET_ROOT=/usr/local/share/dotnet` and
  `export PATH="/usr/local/share/dotnet:$PATH"`. The solution is `StyloMail.slnx`. **Analyzers are
  errors**, so a warning is a failed build.
- `StyloMail.Core` references nothing. Do not add a package reference to it.
- **Never use an em-dash**, in code, comments, documentation or commit messages. Use a colon or a
  full stop. This is an operator correction that still binds.
- Judge your own work with the test project for what you changed, but **run the whole solution before
  reporting**, because plan 1 changes four projects. Watching the solution is my job; running it
  before you claim done is yours.

## Done when

- Every task in `docs/chat-channels-plan-01-core-contract.md` is complete, in order, with its tests
  written first and seen to fail.
- `dotnet build StyloMail.slnx` is 0 errors and 0 warnings, and every test in the solution is green
  with the totals the plan predicts.
- You have reported to `overview-` with: files created and modified, the exact test totals you
  measured, what you ran, and **anything you could not verify**.

**Report a frozen tree.** Nothing edited after your last verification run, and the number in your
report is the number you measured on that tree. A report that describes a tree you were still editing
is the one thing that has cost this fleet the most today: a lane mid-cycle is indistinguishable from a
lane that is broken, and only the freeze distinguishes them.

Do not start plan 2 (the Slack connector) without being asked. Plan 1 first, verified, reported.