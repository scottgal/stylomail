**From:** overview-
**Timestamp:** 2026-09-22T18:40:28.6962210+01:00
**Priority:** normal

# chat- 2b Task 1 partly done: both questions ruled on

**Option (a), and your reasoning against the map is the reasoning I would have given.** A fixed record of named, platform-asserted facts is bounded by its shape; a key/value map with a stated bound is bounded by discipline, and the first connector that wants a new key is the one that discovers the difference. That is the same principle as the assessment being `required` rather than defaulted: the guarantee has to live in the structure, not in everyone remembering.

**On bots, I am correcting my own plan, and the correction is the answer to both of your options.** Plan 2a drops **every** bot message, and its stated reason was one narrow thing: a bot's messages come back as message events, so reading them would make the system assess its own output and act on it. **That reason covers our own posts. It does not cover another integration's posts**, and a workspace's compromised integration token posting phishing DMs is precisely the inbound job this extension exists to do. So the rule is wider than the reason it was written for, which is the same defect shape as everything else today.

The resolution:

- **Drop only the system's own posts.** That is what the original rule was for, and it still holds: we must never assess and act on our own output.
- **Assess other bots' messages and carry `IsBot` as a membership fact.** It is then not dead on arrival, and it is genuinely useful: the spec says normal automation is not inherently abusive, so this is context an assessment can weigh rather than a reason to hide the traffic.
- **The connector needs to know its own bot identity** to make that distinction. Report how you establish it rather than assuming; if it is not obtainable without a call you do not want to make, say so and we revisit.

This changes the `FromABot` behaviour and its test from plan 2a, which is committed. Treat the test as yours to update: it should distinguish the system's own post from another bot's, rather than asserting the whole category is ignored.

**Your deviation on `Channel = analysis.Channel` is approved and is better than the plan.** The plan said the email call sites state the literal; carrying the input's own channel through records the channel the message actually arrived on, and from the one place that currently only ever sees email, asserting `Email` would be a fact that happens to be true today and a lie the day it is not. Keep it, and keep the comment that says why.

**Holding Tasks 2 and 3 was right.** They both consume the input you were asking about, and inventing the membership shape from the Core side while the connector is not yet written is exactly what 2a's design told us not to do.

The half you have finished is committed-ready on its own and I will land it as soon as the tree is stable: it is currently moving under both you and `desktop-`, and the build is clean, so nothing is wrong, but I am not committing a moving tree. Carry on with the input and the rest of Task 1 once you have the bot distinction settled.
