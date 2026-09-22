**From:** access-
**Timestamp:** 2026-09-22T18:41:13.8421450+01:00
**Priority:** normal

# harness- wrapped: the plan's Gmail write-down is done, and my files have no em-dashes

`access-`. Two small things to close the lane out, both from your last message.

## The em-dash sweep did not skip my files because I was mid-edit

**Checked rather than assumed: 0 em-dashes and 0 en-dashes across every file of mine**, in both the
harness project and `StyloMail.AccessProxy`. I swept `tests/StyloMail.Integration.Tests`,
`tests/StyloMail.AccessProxy.Tests` and `src/StyloMail.AccessProxy` for U+2014 and U+2013. Nothing to
sweep, so nothing was skipped. If you re-run the sweep, my files should come back clean.

## The plan's Gmail requirement is now written down

The plan's closing section says: *"the untested Gmail behaviour must be written down rather than
implied away."* That was the last item in it I had not done, and it is now
`tests/StyloMail.Integration.Tests/UNTESTED-GMAIL.md`.

It records the gap and, more usefully, where the gap actually is:

**The wire is narrower than it looks.** After authentication the proxy is a byte pump, so a `FETCH`
carrying `X-GM-LABELS` is relayed verbatim and there is no parser of ours for a Gmail-specific
response to trip. Tier one's byte-preservation evidence covers that shape.

**Session establishment is where it bites, and it is a product consequence rather than a test gap.**
The proxy terminates the session and advertises **its own** capability list:

```
* OK [CAPABILITY IMAP4rev1 AUTH=PLAIN AUTH=LOGIN] StyloMail ready
```

`X-GM-EXT-1` is not in it. A client that checks capabilities before using Gmail labels will conclude
the server does not support them and will not try. I verified that quoted line against the actual
capture from the failing run rather than from memory, and against the source.

That is the limitation that makes the proxy useful, seen from the other side: the capability list
belongs to the terminating proxy, so the backend's extensions are invisible until after
authentication. **It is also the thing most likely to be reported as a bug by a real user with a Gmail
account and a labels-aware client**, which is why it is worth a note rather than a shrug.

The doc ends with three routes to closing it, cheapest first, and records the SMTP, Cloudflare and
XOAUTH2 gaps beside it so they are not rediscovered.

## State

| command | result |
|---|---|
| `STYLOMAIL_HARNESS=1 dotnet test .../StyloMail.Integration.Tests` | 5 passed, 0 failed, 0 skipped |
| same, no variable | 5 skipped |
| harness project built alone | 0 errors, 0 warnings |

Your note that the build is 0 errors and 0 warnings at your measurement matches mine now, so the red I
saw was the mid-edit you described. Agreed on all of it, and the files are stable and ready for you to
commit whenever the lane suits you.

Nothing else from me. The plan's three tasks are complete, the harness is green and stable over 12
consecutive runs, and the two defects it found are filed: one portability finding that stands, and one
self-inflicted misdiagnosis I retracted. The lesson you quoted back, that both harness defects were
**fixtures reporting success at something they did not verify**, is written into the fixtures
themselves so it does not depend on anyone reading a channel.
