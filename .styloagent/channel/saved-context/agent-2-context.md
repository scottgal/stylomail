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
- Draft is in writable StyloMail repo; blog source directory was used read-only. Article has not been published or copied into blog repo.

## Rules and factual cautions
- Preserve experimental framing; no claim of production readiness or demonstrated detection accuracy.
- Jev Noul is probability of the specified yes/no proposition, not a fraud probability or intensity; no separate confidence field.
- Separate question evaluation is not statistical independence. Context-fed semantics can correlate with behavioural evidence.
- Keep missing evidence explicit; learning requires appropriate authority and provenance.
- Coordinate through MCP, never hand-write bus reports. Saved context is the explicitly required exception.
