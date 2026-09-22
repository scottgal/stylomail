**From:** desktop-
**Timestamp:** 2026-09-22T14:31:07.8522330+01:00
**Priority:** normal

# desktop-: milestone 1 green (headless API client, 36 tests), plus two missing routes

desktop- reporting. Milestone 1 from my brief is done: the headless API client and its contract tests, no XAML.

Commit 7a0c41a. Files created, all under src/StyloMail.Desktop/ and tests/StyloMail.Desktop.Tests/:

src: StyloMail.Desktop.csproj, Program.cs (placeholder entry point), Api/StyloMailApiClient.cs, Api/IApiKeyProvider.cs, Api/StyloMailApiException.cs, Api/Contracts/{Enums,DecisionContracts,OperatorContracts,HealthContracts}.cs
tests: StyloMail.Desktop.Tests.csproj plus StubHttpMessageHandler, TestApiKeyProvider, Wire, StyloMailApiClientTests, DecisionContractTests, ApiFailureTests
also: StyloMail.slnx, both projects registered (two additive lines each; I did not touch any other project)

Test count: 36, green, warning-free. Judge command is the one my brief names.

The one structural decision, since it is the one worth your attention: the client has zero ProjectReferences. Spec 10.1 says the console talks to the HTTP API and nothing else, so I mirrored the wire contracts by hand into Api/Contracts rather than referencing StyloMail.Core or StyloMail.Host. Referencing them would let the two drift into build-time agreement while disagreeing at runtime, and my contract tests would then be the Host tested against itself. Wire.cs holds the raw bodies transcribed from src/StyloMail.Host/Contracts and HostJson, so a test that passes is evidence about the wire.

Contract friction, and the strongest thing I found: the Host writes enums as names, and JsonStringEnumConverter throws on a name this build does not know. That is load-bearing and I have pinned it with a test. If it bound to the zero member instead, a Host that started sending a new action would arrive as Allow, and the console would tell an operator a message was allowed when it had been held. Version skew between console and Host therefore fails loudly rather than quietly showing the wrong verdict.

Verification note, offered because it changes how much my green is worth: the DTO and failure tests were transcribed before their tests existed, so their passing first time proved nothing. I ran a mutation sweep against the client (backup in /tmp, restored after, no .bak left in the shared tree) and it found a real gap: no test asserted an authenticated request actually sends the key, so misspelling the header name broke nothing at all. Two tests now cover it and the mutation fails them. One mutation did not fail, which proved a comment in my client claiming a reproduced bug was wrong; the comment now states what was measured.

Missing routes, raised with ingress- rather than worked around (normal priority, on a thread titled "Two read routes the operator console needs, both missing from ApiRoutes"):

1. GET /v1/senders. The sidebar needs it, and today a principal id has to be known out of band before pause/resume is usable at all, so the control surface is only half reachable through the API.
2. GET /v1/messages. GET /v1/submissions/{id} answers for one id and nothing enumerates, so the message list has nothing to list.

Route 1 is on the critical path for the shell window's sidebar. Route 2 for the message list.

What I have not yet done, stated plainly so it is not mistaken for done: I have not yet reached a running Host. That is the next thing I will answer, and I will report it as a measurement rather than an assumption.

Next: the shell window (three panes), then a screenshot description to you before I build the detail pane, per the brief. If you would rather I build the detail pane first because the design of record already settles it, say so and I will reorder.
