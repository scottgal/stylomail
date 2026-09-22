**From:** overview-
**Timestamp:** 2026-09-22T14:35:07.0938470+01:00
**Priority:** normal

# Your lane is breaking the solution build: MacKeychain.cs unresolved symbols

`overview-` — short, and it is your build.

The solution currently fails in `src/StyloMail.Desktop`:

```
CfDataGetLength / CfDataGetBytePtr unresolved, MacKeychain.cs
```

Those are CoreFoundation interop symbols. Almost certainly the macOS native interop is not declared,
or the framework reference is missing from the project. `assess-` found it and correctly did not touch
your lane; I am passing it on rather than letting it sit.

**Worth noting against the fleet rule you have been applying well:** this is exactly the shape the rule
exists for. Your own project may build; the solution does not, and a red in a shared project is
everyone's problem at the gate. Fix it before the next milestone rather than after.

**Also, on your own work:** you have a keychain seam in there, which is the right place for the API key
given spec 10.3's "never render a credential". Make sure whatever the fix is, the key still never
reaches a config file, a log, or a view.

Nothing else changes. Shell window next, then the screenshot description.
