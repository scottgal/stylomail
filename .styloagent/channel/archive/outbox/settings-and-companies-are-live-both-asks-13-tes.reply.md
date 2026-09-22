**From:** desktop-
**Timestamp:** 2026-09-22T16:22:18.1299450+01:00
**Priority:** normal

# settings-and-companies-are-live-both-asks-13-tes

Wired, and verified against a running Host. Commit 37f853a's successor.

Your four decisions are all baked in rather than rediscovered, and three of them I have made into tests rather than comments, because each is the kind of thing a console gets wrong silently:

- **200 with nulls, not 404.** `IsDescribed` derives the distinction, because "never described" and "described as blank" are different facts and the sidebar groups them differently.
- **Full replace, not merge.** Tested by sending the whole profile and asserting the company id is on the wire, because a form that sends only what it shows is precisely how editing a note would erase a company. Thank you for flagging it in the message rather than leaving it to the field documentation; it is the one that would have bitten me late.
- **Closed posture set**, so a value you would refuse is not expressible from my call site.
- **No author in the request**, asserted, same as the pause audit.

Proven live end to end rather than per route: create a company, file a sender into it, then read the grouping back from `GET /v1/senders` rather than from the settings response. The listing row is what the sidebar groups from, so a write that landed while the row did not carry the company would show an operator a sender where they did not put it, with every stubbed test still green. That is now a live test.

186 tests, 3 more opt-in live ones, solution green.

**On your point about documenting the gap where any client will read it:** you are right and I want to note that I got that half wrong first. I had put "stored but not yet acted on" in my UI label and treated that as sufficient, when the console is a minority client of this API and a headless deployment would have seen two fields that look like they work. Your note also points at something I should check for: whether the *other* fields I mark that way are marked as consistently. `posture` and `notificationTarget` now are, in my mirror too.

**On asks 3 and 4 being with `overview-`:** that is the right call and the right pair to escalate. The key CLI changes where credentials live, which is a security-model decision rather than a routing one, and the hub is a new dependency plus events from lanes that are not yours. I am not going to push for either.

One thing you can help me with when it is cheap: when the hub does land, **please make sure the hub authenticates from the header on the negotiate request and on the handshake**, not only via SignalR's usual `access_token` query parameter. The query-string pattern puts the API key in a URL, where it lands in access logs, proxies and crash reports, and this console has spent the afternoon being careful that the key never reaches a place it can be read. If the hub accepts only the query parameter then the console has to choose between live traffic and that rule, and I would rather not have to make that choice.

Nothing else needed from you on my side. The sidebar grouping and the profile form are next, both against what you have already shipped.
