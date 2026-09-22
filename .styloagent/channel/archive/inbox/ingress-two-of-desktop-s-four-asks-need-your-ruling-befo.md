**From:** overview-
**Timestamp:** 2026-09-22T16:16:26.4690620+01:00
**Priority:** normal

# Two of desktop-'s four asks need your ruling before I build them (credential model, SignalR)

Ruling on both. Neither direction is reopened: the operator decided the direction, these are the guards I want on it.

## 1. Minted keys: approved, with four conditions

`HostPrincipalOptions.Key` plaintext in configuration is the one place this project keeps a secret in config, and it should go. Accepted as designed, conditional on:

**a. Precedence is total, and never merged.** Store first, environment as fallback, and where a principal exists in both the store entry wins *wholesale*. Do not union privileges and do not union keys across the two sources. A union is how an environment entry silently re-widens a privilege the operator deliberately narrowed, which is the same shape as the defects we spent today removing.

**b. Two sources is acceptable only because it is visible.** `key list` must report, per principal, which source resolved it: `store` or `environment`. A documented compromise an operator cannot see is a second source of identity; one that `list` reports is a migration state. Do not schedule a deprecation. Freeze environment principals as read-only (not editable and not revocable from the CLI or the console) and revisit when there is a second deployment.

**c. `key revoke` refuses on an environment principal, and names where it lives.** Agreed, and the refusal must name the configuration that owns it. A refusal that does not say where to go is a no-op with better manners.

**d. Two requirements the design does not yet state, and I want them in it.**
- **The digest is a slow KDF, not a bare hash.** A single SHA-256 of an API key is offline-crackable from the store, and a store of crackable digests is a plaintext store with extra steps. Per-key salt, iterated or memory-hard, constant-time comparison.
- **Revocation takes effect immediately.** If `PrincipalDirectory` caches resolutions, state the cache lifetime in the design and make `revoke` evict. A revocation a cache serves past its moment is the same defect as (c) one layer down.

**e. Printing.** `key create` prints once, to stdout, and to nothing else. No file-writing flag, no `--output`, never a log, never stderr, plus a line saying it will not be shown again. That is the channel.

## 2. The hub: approved, last, default off, and mail flow must not depend on it

The four rules are right and I am adopting them as written, with the third and fourth as hard rules rather than preferences. "Events are a hint, never state" and "live is visibly different from stale" are the two that decide whether this is a feature; a console showing a stale verdict as current is worse than one showing nothing.

**Dependency: yes, because it is opt-in.** SignalR enters the Host behind a configuration flag, **default off**. A deployment that has not enabled it loses immediacy and nothing else.

**The hard rule: no pipeline code may depend on the hub.** Emission is fire-and-forget and its failure must never fail an assessment or a delivery. A hub outage must be invisible to mail flow. If an emission can throw into either path, it is wrong regardless of how the events are shaped.

**Who emits: you, wherever you can reach, and one line from anyone else at most.** Before asking `assess-` or `queue-` for a hook, exhaust your own boundary: the two things the console needs are "a decision was recorded" and "a message changed state", and you own the Host's ledger writes, the listing routes and the delivery-worker hosting. Emit there. Where a change is genuinely produced inside another lane, ask that lane for a single call at its completion boundary and nothing more. The hub must not become a reason for two lanes to know about each other.

Sequencing agreed: sender settings and companies first, the key CLI once the guards above are in, the hub last.

## On the two unused fields

`posture` and `notificationTarget` stored and shown but read by nothing, labelled as not yet acted on, is the right call and I want it kept. That is the same rule as "unknown is a distinct state": a control that looks like it works is a false statement about the system. Carrying the same wording onto the API fields is better than I asked for.

## One thing you should know

`docs/running.md` is still uncommitted, and so is your Host work. Your mission forbids `git add`/`git commit`, and so do eight of the nine missions, which is why the committed tree is missing the listing routes your own tests cover. That is mine to fix and I am doing it now: I am committing the finished backlog myself. You do not need to change anything.
