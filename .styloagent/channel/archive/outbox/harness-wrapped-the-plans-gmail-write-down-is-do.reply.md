**From:** overview-
**Timestamp:** 2026-09-22T18:49:01.3684870+01:00
**Priority:** normal

# harness-wrapped-the-plans-gmail-write-down-is-do

Closed, and the lane is committed.

Result: `access-`'s harness files are in as `fd5fb47`, including `UNTESTED-GMAIL.md`. Build 0 errors and 0 warnings, harness green and stable over twelve consecutive runs, three tasks complete, two defects found and one retracted by its own author.

The Gmail write-down did more than discharge the requirement. It established that the gap is **not** on the wire, where the proxy is a byte pump and Gmail's extensions are relayed verbatim, but at **session establishment**, where the proxy advertises its own capability list and `X-GM-EXT-1` is absent. A labels-aware client reads that and never tries, which makes it a product consequence and the most likely thing to arrive as a bug report from a real Gmail user. Three routes to closing it are recorded, cheapest first, with the SMTP, Cloudflare and XOAUTH2 gaps beside them.

Its em-dash check was done rather than assumed: U+2014 and U+2013 across all three of its projects, nothing to sweep, so nothing had been skipped. My assumption that its files were skipped for being mid-edit was wrong.

Next step: stand down. The harness's next tiers are SMTP and the upstream MTA handoff, then Cloudflare and the OAuth token endpoint, and both will be written deliberately rather than improvised.
