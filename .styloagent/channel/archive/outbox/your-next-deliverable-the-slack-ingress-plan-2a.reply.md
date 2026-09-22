**From:** chat-
**Timestamp:** 2026-09-22T18:09:03.3390590+01:00
**Priority:** normal

# your-next-deliverable-the-slack-ingress-plan-2a

Plan 2a is complete: both tasks, tests first and seen to fail, frozen tree, uncommitted per your rule. **SlackNet resolves: version 0.18.0**, recorded as asked.

## Numbers measured on the frozen tree

`dotnet build StyloMail.slnx`: 0 warnings, 0 errors.
`dotnet test StyloMail.slnx`: **1330 passed, 0 failed, 21 skipped**, 14 projects.

New: `StyloMail.Chat.Tests` **28 passed** (9 verifier, 16 reader, 3 retry guard).
Solution before this: 1302 passed, 18 skipped. Now 1330 passed, so exactly **+28**, and every pre-existing project's number is unchanged (Core 20, Policy 19, Persistence 20, Desktop 182/18 skipped, Jev 15, Mime 91, AccessProxy 61, Transport 192, Assessment 124, Adaptive 181, Queue 97, Host 300).
Files: `src/StyloMail.Chat/` (4 sources, 1 csproj), `tests/StyloMail.Chat.Tests/` (3 test files, 1 csproj), `StyloMail.slnx`.

## Four deviations, and the first one needs a decision from you

**1. The SlackNet contradiction. This is the stop-and-report item, and I stopped rather than deciding.** Step 1 adds `SlackNet` and Tech Stack says it is "for the wire types", but **no code in the plan uses it**. The reader hand-rolls the wire types with `System.Text.Json`, and its whole shape (a `bool TryRead` with a reason enum that never throws) exists to avoid a deserializer that throws. I checked the package: SlackNet 0.18.0 does ship a typed event model (`SlackNet.Events.EventCallback`, `MessageEvent`, `MessageEventBase`, `BotMessage`, `UrlVerification`), so this is precisely the case you told me to stop on rather than hand-roll past. I did **not** substitute a library and did **not** remove the reference, so the tree matches the plan literally and the call is yours:
   - **(a) My recommendation:** keep SlackNet for plan 2b's Web API calls and rate limiting, and record that the reader deliberately bypasses its typed events. The never-throws contract is the right property for an unauthenticated surface, and the typed model would need a try/catch around every `Deserialize` to provide it, which is the hand-rolling with extra steps.
   - **(b)** Drop the reference now, so an unused dependency does not ship.
   - **(c)** Rewrite the reader onto SlackNet's types, accepting that the never-throws contract is then something you wrap rather than something you have.
   I left it in place because removing a dependency the plan deliberately chose is a decision, not a cleanup.

**2. Task 1's test contradicted its own implementation.** The plan's theory asserts `MalformedSignature` for all four inputs including `null` and `""`, but the implementation returns `MissingSignature` for those, so it fails 2 of its own 4 cases as written. I split it into two theories: absent (2 cases) asserting `MissingSignature`, malformed (2 cases) asserting `MalformedSignature`. Reasoning: if an absent signature counted as malformed, `MissingSignature` would be unreachable dead code, which contradicts the enum's own documented purpose of telling "not from Slack" apart from "from Slack, replayed". **Your predicted count of 8 is wrong; it is 9.** That is the same class of error as plan 1's, so it may be worth the same correction treatment.

**3. The plan omits the project reference that makes it compile.** `ChatMessage` carries `ChannelKind` from `StyloMail.Core`, but Step 1 never adds `dotnet add src/StyloMail.Chat reference src/StyloMail.Core`. Without it the build stops at `CS0234: The type or namespace name 'Core' does not exist in the namespace 'StyloMail'`. Added.

**4. The reader did not uphold its own documented contract, and I fixed it.** Its doc comment says "Every path returns rather than throws. This runs on an unauthenticated HTTP surface." I probed seven malformed-but-valid-JSON bodies and **all seven threw**: `InvalidOperationException` out of `JsonElement.GetString()` for a field of the wrong JSON type (and for an element that was never found), and `FormatException` out of `double.Parse` for a non-numeric `ts`. Valid JSON says nothing about whether those calls are safe, and every one turns a malformed body into a 500. Fixed by checking `ValueKind` before every read, `double.TryParse` with `InvariantCulture`, and range-checking against `DateTimeOffset`'s representable bounds. I also added a finiteness check, because `NaN` compares false against both bounds and would have silently become the epoch rather than a refusal. Tests first: nine new cases seen to fail, now green.
   One honest correction inside that fix: two of my nine new assertions were wrong and I changed the tests, not the code. `text` and `thread_ts` are optional here (the plan's own reader already defaults an absent `text` to empty and `thread_ts` to null), so a wrong-typed optional field degrades to absent rather than costing the whole message. I split the theory by field class to say so: required fields refuse, optional fields degrade.

## What I could not verify

- **Commits.** I ran neither Step 6 nor Step 8 (`git add`/`commit`), per your rule.
- **`StyloMail.Integration.Tests` reports 3 skipped, not passed.** It is `access-`'s, it is in the solution, and its tests are gated behind `STYLOMAIL_HARNESS=1` plus a container runtime. I observed the skip and did not enable the harness, so I am not claiming those pass. Untouched by me.
- **`dotnet sln add` rewrote `StyloMail.slnx`.** I confirmed `access-`'s Integration.Tests entry survived and the alphabetical ordering is intact, but that file is `M` in git status and they may edit it concurrently. If we collide, mine is the Chat pair and theirs is line 25.
- The 18 skipped Desktop tests are pre-existing; I did not confirm why they skip.
- Minor: the plan writes `Core.ChannelKind.Slack` rather than `StyloMail.Core.ChannelKind.Slack`. It compiles, but only because `StyloMail.Chat.Tests` happens to be nested under `StyloMail`, so the name resolves by enclosing-namespace walk. Worth knowing if that namespace ever moves.

Next step: I am holding for your call on the SlackNet question (1), then plan 2b. The reader's fix is self-contained if you would rather review it before 2b builds on it.
