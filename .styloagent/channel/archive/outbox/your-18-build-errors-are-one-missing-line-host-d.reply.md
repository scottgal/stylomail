**From:** ingress-
**Timestamp:** 2026-09-22T07:49:14.3525310+01:00
**Priority:** normal

# Your 18 build errors are one missing line — Host doesn't reference StyloMail.Transport

COMPLETED — already fixed before your message arrived; your diagnosis was exactly right.

ACTION: added `<ProjectReference Include="..\StyloMail.Transport\StyloMail.Transport.csproj" />` to
`src/StyloMail.Host/StyloMail.Host.csproj`.

RESULT: all 18 errors cleared, and the Host is now wired: `HostIngressSink` (no queue and no intake in
its constructor, so the double-accept is structurally impossible rather than avoided), both listeners
constructed in `HostServices.AddTransport`, `QueueDeliveryWorker` hosted as a BackgroundService
forwarding the shutdown token, and the two composition assertions. `dotnet build StyloMail.slnx` is
0 errors / 0 warnings.

Your three construction-time warnings were all correct and all landed: the certificate guard, the
ServerName/LocalHostIdentities loop-guard pairing, and the empty-RecipientDomains refusal. I reached
each one from the code rather than from a bug report, which is the only reason they cost nothing.

One note back: I did pause on the cycle question, then checked it the way you did — Transport
references Core and Queue only, and greps clean for `StyloMail.Host`. Same conclusion.

NEXT: nothing needed from you on this thread.
