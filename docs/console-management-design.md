# Operator console: the management surface

Design for the sixth area of `src/StyloMail.Desktop`. Agreed with the operator 2026-09-22.

Status: **agreed, not built.** The console currently covers the five areas in spec §10.2 (senders,
messages, decisions, quarantine, feedback) and nothing else.

## Why this exists

Spec §2 names **Operator / tenant admin** as a user who "configures routes, traffic classes, quotas,
retention, and the cloud-content decision", but §10.2's five areas are all review work and none of
them is administration. So the console has no way to do the job its second user exists to do, and no
way to complete a first run at all: the API key seam is built and tested, and there is no screen that
puts a key into it.

## The three decisions this rests on

1. **A company is operator-side, for now.** It organises senders in the console and is stored by the
   Host, but nothing in the assessment pipeline reads it. Designed so it can be promoted to a domain
   concept later without a redesign, which is why membership is a field on the sender rather than a
   list owned by the company.
2. **Rate limits come after this.** They are the one thing here that cannot be operator-side:
   `SendingQuotaLedger` and `OutboundQuotaExhausted` are read by the pipeline, and group-wide limits
   need a group the pipeline can see. Both wait for the promotion in (1).
3. **Principals live in a Host store, with environment configuration still honoured.** Minted
   principals and their key digests go in a store; principals configured by environment keep working,
   so existing deployments and the console's own harness scripts do not break. Resolution checks the
   store first.

## Nouns

**Principal**: a sender identity and the digest of its key. Today the key is
`HostPrincipalOptions.Key`: plaintext in configuration, which is the thing every other secret in this
project is carefully kept out of. Minting moves it to a store, holds only a digest, and shows the
value exactly once.

**SenderProfile**: per principal, everything an operator records:

| Field | Why |
| --- | --- |
| `label` | A human name for a principal. "Acme outbound" is navigable; an address is not. |
| `companyId` | The group it belongs to. |
| `notes` | Free text an operator curates. |
| `externalRef` | The operator's own id for this sender, so the console can be joined to their systems. |
| `notificationTarget` | Where to tell someone. **Stored only: see the honesty note below.** |
| `posture` | A visible stance: trusted, normal, watch. **Shown and stored only, for now.** |

**Company**: an operator-side group: id, name, notes.

## Routes needed

`ingress-` owns these. The console cannot do any of this without them, which is the point of the rule
rather than an obstacle: a headless deployment needs the same routes.

| Route | Privilege | Purpose |
| --- | --- | --- |
| `GET /v1/senders/{id}/settings` | Review | Read one sender's profile |
| `PUT /v1/senders/{id}/settings` | Administer | Write it |
| `GET /v1/companies` | Review | List groups |
| `POST /v1/companies`, `PUT /v1/companies/{id}` | Administer | Create and edit |
| `GET /v1/senders` (extend) | Review | Carry `companyId` and `label` on each row, so the sidebar groups without a second call per sender |
| `GET /v1/senders` (extend) | Review | Carry `source` on each row: `store` or `environment`, so the console can show whether a sender was minted or configured. Owned by `keys-`; the console mirrors it. |

**On `source`, and the bug that prompted it.** `keys-` found that a name which is both configured and
minted currently **disappears from the listing entirely**: wholesale precedence means the configured
entry can no longer authenticate, and the listing already excludes principals that cannot. So the
console would show a sender simply gone, with nothing anywhere saying a conflict existed. That is the
same shape as the `.gitignore` finding of the same afternoon, a thing that looks healthy because
something is quietly absent, and it is why `source` is worth carrying rather than dropping: a sender
whose row says where it came from is one an operator can reason about when the two disagree.

## The key CLI

Minting is a bootstrap action rather than a screen, so it belongs to the executable, not the console.
That also keeps the console's rule intact: the CLI is the other way to do it.

```
stylomail key create --principal ops@acme --tenant acme --privileges Review,Administer --by ops@acme
stylomail key list
stylomail key revoke --principal ops@acme --by ops@acme
```

**`--by` is required on create and revoke, and this document was wrong to omit it.** `keys-`
implemented it that way and `overview-` ruled the document correct rather than the code. The rule is
the one this CLI already established with `quarantine release --by`: **a mutating command with no
identity to sign with must not invent one.** Minting is how access is granted and revoking is how it
is withdrawn, so an entry that records neither is an audit trail with a hole exactly where the
interesting events are.

`key list` takes no `--by` because it mutates nothing.

Constraints: the value is printed once and never recoverable; only a digest is stored; `key list`
shows the principal, tenant and privileges and never the key or its digest.

## Live traffic over SignalR

A hub the console subscribes to, so the UI reflects traffic as it happens rather than when someone
refreshes. Four rules, and the first two are the ones that decide whether this is a feature or a
source of wrong answers.

1. **Events are a hint, not state.** An event says "this changed"; the console re-reads the affected
   row over HTTP. A pushed payload rendered directly would make a dropped, duplicated or reordered
   event a permanently wrong screen, and a console showing a stale verdict as current is worse than
   one showing nothing.
2. **The console must visibly distinguish live from stale.** A feed that silently freezes looks
   exactly like a quiet system. Connection state is shown alongside the traffic, and when the hub is
   down the console falls back to its existing behaviour and says so.
3. **The key never goes in a query string.** SignalR's access-token pattern puts it in the URL, where
   it lands in access logs, proxies and crash reports. The .NET client can send a header on both the
   negotiate request and the WebSocket handshake, and that is what the console uses.
4. **`wss://` for anything that is not loopback**, on the same rule as the REST surface.

The hub does not replace polling: the console works with it absent, and a deployment that has not
enabled it loses nothing but immediacy.

## Transport security

The console refuses a non-loopback `http` Host. Loopback is the only case where plain http is
defensible, because every request carries the operator's API key in a header.

**The default address stays at the Host's documented port.** An earlier draft of this section said it
should move because `127.0.0.1:5000` is macOS AirPlay Receiver on this machine. That was the wrong
call: `docs/running.md` tells an operator to run the Host there, so a console defaulting somewhere
else would contradict the documentation and send someone to the wrong place twice. The collision is
a diagnosis problem rather than a default problem, and it is handled as one: a refusal that carries
neither the Host's error code nor its sentence now reports that the address is probably not a
StyloMail Host and says to check the port. See `HostStatus.FromFailure`.

## The console surface

A **Management** sidebar section:

- **Connection**: enter, replace or clear the API key; shows which Host and whether it is live.
- **Companies**: list, create, rename.
- **Senders**: grouped by company, each opening a profile form, keeping the existing pause/resume.

**Posture and notification target are shown as stored, and labelled as not yet acted on.** A console
whose stated job is explaining why something was held must not show a control that looks like it
works when nothing reads it. When the pipeline honours posture, or the Host sends to a notification
target, the label comes off.

## Testing

The UI harness (`ux-scripts/`) drives it, which is what makes this loopable rather than
hope-driven. A management script creates a company, files a sender into it, sets a label, and asserts
the sidebar regroups. Key entry is an in-app window rather than a native picker, so the harness can
drive it; a native OS picker could not be driven, which is the wall mylo documents.

Unit tests cover the profile model and the live-channel state machine, including the case that
matters most: the hub disconnecting must leave the console usable and visibly not-live.

## Deferred, with reasons

- **Rate limits**, until a company is something the pipeline reads.
- **Enforcing posture**, same reason.
- **Delivering notifications**, which is a new outbound capability from the Host and not a field.
- **Tenant administration.** Every noun here is scoped to the authenticated principal's tenant, and
  there is no route that crosses tenants. Managing multiple tenants from one console is a different
  design and is not implied by this one.

## Open risk

The largest one is that "company" is the wrong noun. If marketing senders need a hierarchy (a brand
under a group under an agency), a flat `companyId` will need reworking, and senders filed under it
make that a migration rather than a rename. The mitigation is that membership is a single field on
the sender, so a hierarchy can be added as a parent link on the company without moving senders.
