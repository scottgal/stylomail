**From:** desktop-
**Timestamp:** 2026-09-22T15:00:58.4427950+01:00
**Priority:** normal

# desktop-: decision pane done, plus two findings outside my lane and one missing link

desktop- reporting. The decision pane is built, the order you set is complete: shell, screenshot description, detail pane.

Commit de7ad59. 105 tests (96 hermetic, 9 opt-in). Solution build green. Screenshot at .styloagent/shots/desktop-decision-pane.png.

What the pane shows, top to bottom, for a quarantine at risk index 0.82:

- "Quarantine" as a word, not a colour, with the assessment id beside it and "risk index 0.82 (an index, not a probability)" underneath. The wording is pinned by a test. A verdict rendered as red or green invites a decision made from the corner of an eye, and the reasoning underneath is the point.
- **Why**, in policy's order, never resorted: three reason cards, each showing its code, its sentence, and the evidence that produced it inline (signal id, origin, value). One card carries an amber line reading "Evidence referenced by this reason was not returned: sig_absent", because that reason names a signal the response did not include.
- **Dimensions**: semantic 0.91, behavioural 0.42 with "Scored over reduced input coverage, so it is real but weaker", and reputation reading "not measured (unavailable)" with "Could not be produced. This is not a negative result."
- **Coverage**: html_text_disagreement and conversation_context_missing, each with a sentence. Only the true flags, because BodyParsed is true on the ordinary path and listing it would train an operator to ignore the section.
- Then versions and the semantic cache provenance.

The one I would draw your attention to, because it is the project's rule in the place it would actually mislead someone: an unavailable dimension arrives with a score of 0.0, because that is what an enum-shaped payload does. The pane renders "not measured (unavailable)" and has no path to render the zero at all. A zero-length bar would tell an operator the semantic layer looked and found nothing, when it never looked. Opposite conclusion, same pixels. A semantic signal with no confidence field renders "not reported" rather than an empty control, because the live API was measured returning no confidence on a Noul at all.

**Honest limit on that screenshot.** It is rendered by the real window, but the decision body is the transcribed wire fixture rather than a response from a live Host, and that is stated in the commit rather than implied. The reason is finding 1 below: producing a real decision needs a working semantic provider key, and the operator's key is not something this harness may hold. The client itself is verified against a running Host; the rendering is verified by 15 tests plus this render.

## Two findings outside my lane, filed as issues

Both found while trying to produce that decision, both filed with reproduction, neither patched.

1. **A rejected Jev API key 500s every assessment while /health/ready still says ready** (medium). POST /v1/assessments returns 500 with an empty body and JevContractException in the log; GET /health/ready returns 200 status ready in the same breath. So a deployment whose provider key has been rotated looks healthy to a load balancer and fails every message, and no decision is recorded at all, so the message is neither assessed nor explained. That is the failure shape this project keeps naming: a misconfiguration that looks healthy. Worth noting the spec says the semantic step degrades to an explicit unavailable state, and a 401 currently escapes that entirely.

2. **JevOptions.Endpoint and Model are not configurable** (low). HostServices constructs `new JevOptions { ApiKey = jevApiKey! }` with IConfiguration in scope as a parameter, so Endpoint is always the hosted default and Model is always the compiled pin. The defaults are correct today, which is why this is latent rather than live. It becomes real the first time a deployment needs a different endpoint or model pin, at which point setting StyloMail__Jev__Endpoint appears to be ignored with no error anywhere. I hit it directly: pointing the endpoint at an unreachable address is how I would have exercised the semantic-unavailable path end to end.

## One missing link, which is a missing route

**Nothing connects a listed message to its decision.** `assessmentId` appears only on the response to POST /v1/submissions. It is not on the listing rows and not on GET /v1/submissions/{id}, and the queue carries no assessment id. So a reviewer looking at a quarantined message can find no route to the explanation for it, which is the console's headline use case and the one the whole pane is built for. The pane says this plainly rather than looking empty, and I am asking ingress- for it rather than working around it.

That is the one genuine gap left on my side. The backlog after it: sender controls (pause and resume, Administer) and quarantine release (Review), both of which are the console's first write actions rather than reads, and both of which already exist as routes.

## Fleet-level suggestion

That is now six defects across this work that a rendered screenshot found and no test did: four in my own console, plus these two, which I only reached by trying to drive a real end-to-end path. I would suggest a rendered screenshot be treated as a verification step in its own right for anything with a surface, not as documentation produced afterwards. The harness is Debug-only, about eighty lines, writes a PNG with no display and no stolen focus, and I am happy to hand the pattern over.
