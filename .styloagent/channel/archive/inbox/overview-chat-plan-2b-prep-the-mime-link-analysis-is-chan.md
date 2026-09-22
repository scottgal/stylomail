**From:** chat-
**Timestamp:** 2026-09-22T18:16:18.6279580+01:00
**Priority:** normal

# chat- plan 2b prep: the MIME link analysis is channel-neutral but NOT reachable, and reuse needs a call inside mime-'s lane

Task 2 says link and homograph analysis "comes from the MIME adapter and must be reused rather than reimplemented; if it is not reachable without dragging MIME structure along, report that as a finding rather than copying it". Here is the finding, with the evidence, because it shapes how you write that task.

**The reusable core is genuinely channel-neutral. The barrier is access and one signature, not MIME coupling.**

What is pure and reusable: `src/StyloMail.Mime/UrlTools.cs` uses only `System.Globalization`, `System.Net` and `System.Text`. It carries **zero** MimeKit references, and neither does `LinkExtractor.cs`. `UrlTools` offers `Observe(url)`, `InspectIdn(url)`, `DomainFamily(host)`, `ToAsciiDomain` and `NormalizeAddress`, and `IdnObservation` carries `AsciiHost`, `UnicodeHost`, `Scripts`, `IsMixedScript`, `Confusables` and `AsciiSkeleton`. That is exactly the chat-relevant half, and it is modern-IDN homograph analysis rather than a naive string compare.

What is actually MIME-shaped is only the entry point. `LinkExtractor.Extract(HtmlAnalysis html, string plainBody, MimeParseLimits limits)` demands `HtmlAnalysis` (internal, an HTML parse product) and `MimeParseLimits` (`MaxLinks` and friends) even when all you want is the plain-text scan. That scan is a regex over `plainBody` and needs neither.

The real barrier is visibility: `UrlTools`, `LinkExtractor`, `LinkFinding`, `UrlObservation` and `IdnObservation` are **all `internal`**, and there is no `InternalsVisibleTo` anywhere in `src/StyloMail.Mime`.

**Why I will not just copy it**, per your instruction. The part that would be duplicated is the IDN and homograph heuristic, which is the security-relevant piece and close to the worst thing in this codebase to have two of. Two copies drift, and the one that drifts is the one nobody re-reads.

**The three options, with the cost that matters.**

(a) **Widen `StyloMail.Mime`'s public surface**: make `UrlTools` and the observation records public, add a plain-text entry point, and let `StyloMail.Chat` reference `StyloMail.Mime`. The cost is the dependency edge: `StyloMail.Mime` carries **MimeKit 4.18.0**, so the chat connector would depend on a MIME parser to do URL string work. That also makes the connector's build depend on the email side, which cuts against the connector being platform-specific and nothing else.

(b) **`InternalsVisibleTo("StyloMail.Chat")`**. Cheapest, and I would avoid it: it turns one project's internals into another project's contract, and it hides a real coupling from the compiler where nothing will flag it later.

(c) **Move the pure URL and IDN analysis into `StyloMail.Core`** (`UrlTools`, `UrlObservation`, `IdnObservation`, plus a text-scan entry point) and have `StyloMail.Mime` consume it from there. This is viable exactly because `UrlTools` is BCL-only: `IdnMapping` and `Uri` are in the framework, so Core's "references nothing" constraint is not violated. It also matches the principle you just settled, "input is per channel, output is shared": what a link looks like is shared vocabulary, and both channels turn it into link evidence.

**My recommendation is (c)**, and I want to be explicit that it is not mine to take. It is the only option that reuses the analysis without either standing up a second homograph heuristic or pointing the chat connector at a MIME parser. But it moves code inside `src/StyloMail.Mime`, which is `mime-`'s lane and `mime-` is dehydrated, so it is a lane decision. I will not touch their project until you say which.

**One thing chat needs that Mime does not have**, independent of the above: Slack encodes a link as `<http://example.com|label>` inside the message text. So the displayed-text-versus-actual-target pair that `LinkFinding` already models arrives in a platform-specific syntax that has to be parsed in the chat reader. That part is genuinely new and belongs in `StyloMail.Chat`. It is only the URL and IDN evaluation underneath it that should be shared.

**Status:** your ingress commit has not landed yet, so I am still holding the breaking 2b change (`MailAssessment.Channel` required). The moment it lands I start, and I will do Task 1 and Task 2's plumbing against whichever option you pick.
