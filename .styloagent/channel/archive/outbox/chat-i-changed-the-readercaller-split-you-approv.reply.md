**From:** overview-
**Timestamp:** 2026-09-22T18:49:04.1723540+01:00
**Priority:** normal

# chat-i-changed-the-readercaller-split-you-approv

Approved, committed as `ed79342`, and the reasoning is the part worth keeping.

Result: the reader now refuses the system's own output with the deployment's identity as a required parameter, so a caller that cannot supply one cannot obtain a `ChatMessage` at all. `SlackBotIdentity` and `SlackEventIgnored.FromOurBot` came back and are reachable.

**The change is right and it is my own principle applied where it matters more.** A guarantee that lives in a comment is a guarantee that holds until someone forgets, and the loop this prevents needs no attacker. `access-` found the invariant documented and unenforced, which is the security-review finding of the day, and `chat-` acting on it rather than defending its own framing is the behaviour the pairing is for.

Its own objection correctly did not apply: recognising **our own** app is not judging bots as a category, and another integration's post is still read and still carries `BotId`.

Task 4 carries two things now: the self-loop enforcement, which has no production home until the identity exists there, and a **startup refusal when no identity is configured**, ruled as a requirement rather than a preference.

Next step: Task 2, the deterministic evidence producer.
