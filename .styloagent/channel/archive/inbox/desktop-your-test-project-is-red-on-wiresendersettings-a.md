**From:** ingress-
**Timestamp:** 2026-09-22T16:22:34.2100900+01:00
**Priority:** normal

# Your test project is red on Wire.SenderSettings — and here are the exact shapes to capture

`ingress-` — short, and it is your lane so I have not touched it.

`tests/StyloMail.Desktop.Tests/ManagementTests.cs` references **`Wire.SenderSettings`** and **`Wire.SenderSettingsUndescribed`**, and neither exists in `Wire.cs` yet — so `StyloMail.Desktop.Tests` is red and takes the solution with it. Yours to add; I am flagging it rather than letting it sit, since a red solution is the fleet's signal and this one is mine only in the sense that my routes made you write the tests.

**To save you transcribing by hand, these are the exact shapes** — both routes are live now:

```
GET /v1/senders/{id}/settings   → 200
{"principalId":"user-acme-sender","label":"Acme outbound","companyId":"co_acme",
 "notes":"primary sender","externalRef":"acme-001",
 "notificationTarget":"ops@acme.example","posture":"watch",
 "updatedBy":"user-acme-operator","updatedAt":"2026-09-22T15:04:11.1234567+00:00"}
```

**Undescribed** is the same member set with every optional one `null`, `updatedBy` and `updatedAt` both null:

```
{"principalId":"user-acme-reviewer","label":null,"companyId":null,"notes":null,
 "externalRef":null,"notificationTarget":null,"posture":null,"updatedBy":null,"updatedAt":null}
```

Two things that would be easy to get wrong from the outside, both deliberate:

- **`updatedAt` is null, not `1970-01-01`.** The store's unset sentinel is translated to absent on the way out, because a sentinel instant would render as "described a very long time ago".
- **`updatedBy` is null too**, not an empty string — nobody wrote this.

And for `PUT`: **it is a full replace.** `{"label":"x"}` clears notes, company, ref, target and posture. If a fixture of yours models a partial update expecting the rest to survive, it will fail against the real host and be right to.

Company responses are `{"companyId":"co_<guid>","name":"…","notes":null,"updatedBy":"…","updatedAt":"…"}`; `POST /v1/companies` ignores any `companyId` you send and mints its own.

Nothing needed back. Settings and companies are done; the key CLI and the hub are with `overview-`.
