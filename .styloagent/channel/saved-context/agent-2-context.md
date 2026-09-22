# article — agent-2-

## Scope and user intent
- Display name article; stable bus prefix agent-2-. Cockpit rename acknowledged via reply_to_thread.
- Assigned to write a blog article in Scott's style about StyloMail as an experiment in how simple behavioural inference can be using Jev.
- User explicitly corrected scope: conceptual discussion of Jev with behavioural inference, not a product manual.
- Style sources: /Users/scottgalloway/RiderProjects/mostlylucidweb/Mostlylucid/Markdown, especially behavioural-inference-systems-blog.md, constrained-fuzziness-pattern.md, constrained-fuzzy-context-dragging.md, reduced-rag.md, stylobot-fingerprint.md, zero-pii-customer-intelligence-part1.md and stylobot-release-learning.md.

## Repository and work
- Repository /Users/scottgalloway/RiderProjects/stylomail; shared main; HEAD observed 90baa52e3fc40c8cfe2536e0084d1097379831c9. No commits created by this agent.
- Draft: docs/blog/stylomail-behavioural-inference-with-jev.md, approximately 2,200 words, matching blog category/date/TOC conventions and one Mermaid diagram.
- Rewritten from initial implementation-heavy draft into conceptual discussion. Covers named semantic dimensions, probability versus fraud, temporal evidence, bounded behavioural context, observed versus trusted memory, uncertainty and deterministic policy.
- Six distinct internal blog links verified against existing source files; fences balanced; no trailing whitespace. Official Jev introduction and Noul documentation consulted and linked.
- No application code, deployment or runtime changes. Other agents have unrelated working-tree changes; leave them alone.

## Coordination and remaining work
- Asked overview- for technical/conceptual guidance and factual review via Styloagent messages. Latest thread sent: Conceptual Jev article ready for factual review.
- Overview completed claim-by-claim review: accurate; no factual corrections required. Flagged private repository and no selected license. Draft now explicitly marks repository link as currently private; no open-source claim or licensing change.
- Overview reports main advanced to 822d476 during review; this agent has not committed.
- Article ready for user handoff. No application tests needed for prose-only change.
- User corrected repository visibility: repository is public. Removed private label and copied corrected article, with approved filesystem escalation, to /Users/scottgalloway/RiderProjects/mostlylucidweb/Mostlylucid/Markdown/stylomail-behavioural-inference-with-jev.md. Compared source and destination successfully. Not published.

## Rules and factual cautions
- 2026-09-23: Added nine real C# excerpts from committed app/tests at 916aaa936eab706b4623d97efc63af8c898a3bde, with pinned GitHub source links: dimension definition, BuildQuestions, request assembly, HTTP creation/send/deserialization, evidence mapping, profile fields, and mapping assertions. Excerpts explicitly described as fragments using app types, not a standalone console sample.
- Added selected JSON entries derived from SuccessBody test fixture (.93 credential / .11 others, usage 296/20), clearly MOCKED and not a live prediction. Synthetic fixture input subject/body quoted from tests. Separately added all 11 recorded live synthetic-phishing values from spec §6; no raw-live JSON capture claimed, no new provider call or mail transmission. Asked overview for any persisted safe raw live capture; no response received at completion.
- Verified all 9 C# blocks match committed source after whitespace normalisation; JSON matches fixture construction; source links pinned; glossary anchors/internal links/fences/whitespace checked. Both article copies identical. No app tests run or needed for excerpts; no publication/commit.
- Latest scope supersedes pre-release-product framing: user says StyloMail is personal research material, not a product. Article now frames time-boxed tools and AI-augmented Agile spikes: build a whole idea to experiment with, rapidly iterate, then pursue/change/extract/stop based on evidence. Distinguishes code-writing AI from Jev evaluated inside the software.
- Reviewed for mid-level developers unfamiliar with ML. Added linked first-use explanations for LLMs, classification, training/inference, calibration, Choice/Score/Noul, features/vectors/dimensions, EWMA, independence, parameters, false positives/negatives. Explained baseline, drift/velocity/acceleration, coverage/support, policy and provenance; simplified diagram labels. Links use official TypeSafe, Google ML glossary, scikit-learn, NIST, Penn State, plus existing blog articles. Internal blog links and Google glossary anchors checked; Markdown fences and whitespace checked.
- Canonical blog article and local docs copy synchronized; approximately 4,100 words after conceptual and educational additions. No publishing or commits.
- Latest user edits applied to BOTH blog destination and local copy with approved filesystem escalation: title is "On The Jev Bandwagon: Building a Behavioural Bidirectional Spam Blocking Proxy with Jev & .NET Core". Core angle now explicitly compares Jev with general LLMs (acknowledges schema-constrained output) and Stylo.Bot inference (computed signals, learned patterns and optional LLM enrichment). Explains replacements versus complements, bidirectional rationale and comparative evaluation. About 3,000 words.
- Added user-supplied experience near opening: recently built an outgoing spam system for a customer using Azure Service Bus and an interceptor pattern. No client identity, outcomes or claim that it used Jev.
- Official TypeSafe launch and Anthropic structured-output docs consulted and cited for comparison. Existing review preceded these additions; new conceptual guidance requested from overview.
- Preserve experimental framing; no claim of production readiness or demonstrated detection accuracy.
- Jev Noul is probability of the specified yes/no proposition, not a fraud probability or intensity; no separate confidence field.
- Separate question evaluation is not statistical independence. Context-fed semantics can correlate with behavioural evidence.
- Keep missing evidence explicit; learning requires appropriate authority and provenance.
- Coordinate through MCP, never hand-write bus reports. Saved context is the explicitly required exception.
