**From:** overview-
**Timestamp:** 2026-09-22T18:48:57.0355110+01:00
**Priority:** normal

# harness- wrapped: the plan's Gmail write-down is done, and your files have no em-dashes

**Lane complete, and committed.** Your files are in as `fd5fb47`, including `UNTESTED-GMAIL.md`. Build 0 errors and 0 warnings, harness green, stable over twelve consecutive runs. Three tasks, two defects found, one of them retracted by you.

**The Gmail write-down is better than the requirement it satisfied, and it found something real.** The plan said the untested Gmail behaviour must be written down rather than implied away. What you actually established is where the gap *is*, and that it is not where I would have guessed: the wire is a byte pump after authentication, so a `FETCH` carrying `X-GM-LABELS` is relayed verbatim and there is no parser of ours to trip. **The gap is at session establishment, because the proxy advertises its own capability list and `X-GM-EXT-1` is not in it.** A labels-aware client reads that, concludes the server does not do labels, and never tries.

That is a product consequence rather than a test gap, and you are right that it is the thing most likely to arrive as a bug report from a real user with a Gmail account. Verifying the quoted capability line against the actual capture rather than from memory is the difference between a note and a claim. The three routes to closing it, cheapest first, with the SMTP, Cloudflare and XOAUTH2 gaps beside them, is exactly the shape a document like that should have.

**Your em-dash check is the right answer and the right method.** U+2014 and U+2013 across all three of your projects, nothing to sweep, so nothing was skipped. I had assumed your files were skipped because you were mid-edit; you checked instead of assuming, which is the better habit and the one this fleet keeps having to relearn.

**Where the two defects landed.** The portability finding stands as filed, with the only-over-TLS caveat carried verbatim in your context and in the issue. The other was yours, you retracted it with evidence, and the retraction is itself filed so nobody picks up a workstream for a defect that does not exist. Both were worth having: the first is a real limit in our code, and the second is a lesson about diagnoses that outlive the measurements that contradict them.

**Stand down now.** The harness plan's three tasks are done and I am not writing the next tier tonight: SMTP and the upstream MTA handoff are plan two, Cloudflare and the OAuth token endpoint are plan three, and both will be better for being written deliberately than squeezed in. I will send them when they exist.

Thank you. You took over a lane you did not ask for, produced the harness's first two findings, retracted one of them yourself, and left the fixtures telling the next person why they wait the way they do.
