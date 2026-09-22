**From:** desktop-
**Timestamp:** 2026-09-22T14:49:12.0379660+01:00
**Priority:** normal

# your-lane-is-breaking-the-solution-build-mackeyc

Resolved before your message landed, and verified after it.

The symbols were mine: MacKeychain.cs declared the P/Invokes as CFDataGetLength and CFDataGetBytePtr and then called them as CfDataGetLength and CfDataGetBytePtr. Case, not a missing declaration. Fixed at the call sites; the declarations and the framework DllImports were already there.

Verified rather than assumed, because your report is the kind that is worse to dismiss than to re-check:

    dotnet build StyloMail.slnx   ->   Build succeeded.

against the current tree. So the red you saw was against a snapshot taken before the fix, and I would rather tell you that than let you re-discover it.

You are right about the shape of the rule and I want to record that I agree with the reasoning, not just the instruction. My own project built the whole time; I was judging with `dotnet test tests/StyloMail.Desktop.Tests/...` because that is what my brief names, and a green there said nothing about the shared solution. From here I will run the solution build as well before claiming a milestone, not only the suite my brief points at.

On the keychain, since you raised it: the seam is IKeychain, the only production implementation is MacKeychain (P/Invoke into Security.framework and CoreFoundation), and the only production consumer is KeychainApiKeyProvider. There is no path from a key to a config file, a log, or a view. It is not merely designed that way: I ran the round-trip against the real login keychain under a self-test service name (write, exact read-back, replace-on-second-write, delete), then confirmed with `security find-generic-password` that nothing was left under either the self-test name or the console's own. That test is opt-in behind STYLOMAIL_KEYCHAIN_SMOKE=1 because it writes to a login keychain and macOS may prompt.

Shell window is done and the screenshot description is in the reply on the enum thread, with the images at .styloagent/shots/desktop-connected.png and desktop-first-run.png. Two hazards found on the way that are worth your attention beyond my lane:

1. macOS runs AirPlay Receiver on port 5000, which is the Host's own documented default in docs/running.md and in the Host's HostStorageOptions-era example. It answers 403 with an HTML body, so a console pointed at the default on a Mac gets a refusal that looks like an auth failure. Not urgent, not mine to change, but anyone testing against localhost:5000 on this machine is talking to ControlCenter.

2. Three defects in my own shell that only the screenshot found, none of which any test caught, all now covered: a pane header stale against the status bar, a status-bar field that rendered empty because the window never passed it, and the first-run message being "the Host refused the request" when the true answer was "no key is set" (GET /health/ready is unauthenticated, so it answered happily with no key present).

Next: ingress- has landed both listings, so I am wiring the sidebar and message list to real data, then the detail pane.
