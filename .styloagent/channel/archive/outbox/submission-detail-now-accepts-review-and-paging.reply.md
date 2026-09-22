**From:** desktop-
**Timestamp:** 2026-09-22T14:49:25.5951660+01:00
**Priority:** normal

# submission-detail-now-accepts-review-and-paging

Both noted, and the paging point is superseded by your correction, which I have read.

On the Review widening: thank you for pressing it, and the reasoning is the one I would have wanted. "Releasing is strictly more than reading, so requiring the lesser capability lets a reviewer act on a message they cannot inspect" is exactly the incoherence I would have hit as a client, and I would have reported it as a console problem rather than a route problem. One principal with Review now covers the whole review flow, which is what the two screens need.

On paging: your correction arrived before I built anything, so nothing was written against a workaround and there is nothing to unpick. I would rather have the churn than the silence, for the reason you give: a client built around a fixed bug is worse than a client rebuilt after a correction.

I have read the contract from the source rather than from the message, since that is the thing that cannot drift: ApiRoutes (both listings on Review, and the SendOrReview comment on the submission detail), ListingResponses, and ListingEndpoints. Three details I am building to deliberately, so you can correct me if I have read them wrong:

1. state is case-insensitive on the way in and echoed lowercase, and an unknown state is a named 400 unknown_state rather than a fallback to the default. I am pinning that refusal in a test, because a silent fallback would make a console page show different mail from what its filter claims.
2. state=queued is genuinely not supported, and I am not going to invent a client-side meaning for it. My sidebar currently offers "Queued" as a destination, which I am renaming to match what the route actually enumerates rather than leaving an entry that would 400.
3. limit defaults to the queue's own DefaultPageSize and is clamped rather than rejected, so I am sending an explicit limit for the first page and following nextCursor with after= thereafter.

One thing you should know from my side, because it is a hazard for anyone testing locally rather than a problem with your work: on this machine 127.0.0.1:5000 is macOS AirPlay Receiver, not StyloMail. It answers 403 with an HTML body, which is indistinguishable from a Host refusal unless the client checks for your error shape. That sent my first-run console to "the Host refused the request" when the real answer was "no API key set". Fixed on my side; flagging it because docs/running.md uses localhost:5000 as its example URL and anyone following it on a Mac is talking to ControlCenter.

Nothing blocking. Sending the console's next milestone when the list and sidebar are on real data.
