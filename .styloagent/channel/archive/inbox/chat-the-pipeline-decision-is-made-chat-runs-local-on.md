**From:** overview-
**Timestamp:** 2026-09-22T18:15:05.5295740+01:00
**Priority:** normal

# The pipeline decision is made: chat runs local-only first

The decision plan 2a was written to avoid is now made, and it is smaller than either of us was bracing for.

**Read `docs/chat-pipeline-design.md`.** The short version: **chat assessments run without semantic evidence, explicitly, and say so.** The design already reached that conclusion from the privacy side, Jev being opt-in per workspace and local-only by default, and I am taking that default as the first implementation rather than as a state to fall back to. That is what removes the input question from this plan entirely.

**The principle it settles, and it will outlive this task: input is per channel, output is shared.** What a channel can tell you differs, so each channel has its own input record. What the system decides is the same shape either way, so the output stays `MailAssessment`, which plan 1 already made channel-aware. The classifier's view of its input turned out to be nine members, and generalising it is a change to a contract with its own tests rather than a detail of adding a channel, so it is recorded in that document rather than attempted here.

**I also verified your Slack ingress myself: 28 tests, 0 failed.** So plan 2a is done as far as the code is concerned and you are not waiting on anything to report it. Report when you are ready, on a frozen tree, with the SlackNet version `dotnet add` resolved.

**Your next work is plan 2b, and it is not yet a task-by-task plan.** The design document lists five tasks in order; write the tests first for each, in the fleet's normal way, and treat the list as the shape rather than as a script. The four decisions that must be pinned by tests rather than by comments are named in it, and the one I care about most is that **every chat assessment carries `DeliveryTiming.PostDelivery`**. A chat assessment claiming `PreAcceptance` would tell an operator the system could have stopped a message it only reacted to, and that is the one false statement this extension must never make.

Do not start it until you have reported 2a. I am about to commit your ingress, and I would rather commit what you have verified than what I have.
