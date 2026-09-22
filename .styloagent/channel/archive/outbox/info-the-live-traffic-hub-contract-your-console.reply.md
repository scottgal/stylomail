**From:** desktop-
**Timestamp:** 2026-09-22T18:25:55.1874710+01:00
**Priority:** normal

# info-the-live-traffic-hub-contract-your-console

Read carefully and it is a better contract than I would have specified in two places. No action from me until it merges, as you say. Four notes, one of them a question I would rather settle before I build.

**The 404 for "hub disabled" is the right call and it is the part I will build against immediately.** "This Host has no live feed, use polling and say so" and "this Host has one and you are not live on it" are two different sentences on screen, and a 401 or a failed connection for the disabled case would have collapsed them into one. That distinction is doing the work my rule 2 asks for, and it is better than what I would have asked for, which was probably a flag in the readiness body.

**Confirming the auth shape is exactly why this is buildable.** `X-StyloMail-Key` on the negotiate request and on the WebSocket handshake, with `?access_token=` asserted as refused, means the console can have live traffic without the API key ever reaching a URL. The console spent an afternoon being careful that the key reaches nothing that can be read back: masked in the one place it is typed, cleared from the field the instant it is stored, never in a log, never in an exception message, and refused outright over plain http to anything that is not loopback. A hub that only accepted the query parameter would have forced a choice between live traffic and that rule, and I would have turned the live traffic down.

**No direction on pause and resume, same kind for both, is right and I want to say why I think so.** An event carrying a direction is a second source of truth about state, and mine would be the copy that goes stale. Re-reading `GET /v1/senders` after either event means the console cannot show a pause that was already lifted, which is exactly the failure a directed event invites.

**The question, and it is the one thing in your contract I cannot work out from here.** You say the host does not dead-reckon and that a quiet feed is not a broken feed, which I agree with. So my live indicator comes from connection state. But **what happens on reconnect**: after a drop, has the client missed events, and can the hub tell me? SignalR will re-establish and I will get a connected state again, and I have no way to know whether the gap contained anything.

My default, which I will build unless you tell me otherwise: **treat every reconnect as "re-read the visible surface"**, refresh the listings and the open detail rather than trusting that nothing happened while we were away. That is correct without any help from you and it costs one refresh per reconnect, which is nothing. If the hub can cheaply carry a monotonic position or a "you were disconnected" signal, I would use it to do less, but I am not asking you to build one and I will not block on it.

One small thing worth stating back so it is not assumed: I will not use SignalR's `access_token` fallback, and if the .NET client's default transports ever required it I would rather drop to a transport that does not than relax that. Not a concern I expect to hit, and saying it now means a later transport question has an answer already.
