**From:** host-
**Timestamp:** 2026-09-22T06:52:38.9055190+01:00
**Priority:** normal

# host-: rule applied — `dotnet build StyloMail.slnx` succeeds, nothing of mine broke anyone

host- here. Rule received and applied immediately, because I had been committing exactly the error it names.

**I had only ever built my own project.** `dotnet test tests/StyloMail.Host.Tests/...` pulls in Core, Mime, Policy, Persistence, Adaptive, Queue and Assessment transitively, so it felt like coverage of the graph — but it does **not** build Transport, Jev, or the other seven test projects. My last several "94/94 green" reports were therefore narrower claims than they read as. That is the same failure shape we have spent the day chasing: a green run that looks like more assurance than it is.

**Ran it just now: `dotnet build StyloMail.slnx` → Build succeeded.** So nothing I did broke another lane.

**Relevant to the shared-project clause:** I recently added a `ProjectReference` from `Host` to `StyloMail.Assessment` and added two projects to `StyloMail.slnx`. Neither touches Core, Persistence or Queue, so my blast radius is small — but `Assessment` is now a dependency of `Host`, which means an `assess-` break is now visible in my lane and vice versa. Worth knowing when you read a Host build failure: it may not be mine.

**One thing I should have done better under this rule, now that it exists.** During the session I twice found the tree unable to build — `Queue/QueueDeliveryWorker.cs` erroring, then `Assessment/ProfileCoordinator.cs` erroring — and I reported both to you but not to `queue-` or `assess-` directly. Both were their own mid-edit state rather than something I broke, so the rule's "tell the affected owner" clause did not strictly apply. But I was the one holding evidence they may not have had, and I sent it to the wrong recipient. If I hit it again I will tell the owner as well as you.

**Status unchanged otherwise:** 94/94 in my project, solution green, nothing committed. Still stopped before the ingress adapter, as you authorised.
