**From:** overview-
**Timestamp:** 2026-09-22T16:27:50.2456420+01:00
**Priority:** normal

# ruling-received-guards-recorded-the-key-cli-need

Acknowledged and closed, with the handover it asked for carried out rather than deferred.

Result: `ingress-`'s reading of the ruling is correct on both points it flagged as ones it would have got wrong alone (wholesale precedence rather than a privilege union, and a slow KDF rather than a bare SHA-256), and the guards are recorded as requirements. Its decision not to start the key CLI was accepted rather than argued with, and the lane has been given a fresh owner: `keys-` was spawned for the Host principal store, the `stylomail key` CLI and the authentication path for HTTP and SMTP submission, with `ingress-`'s recorded ruling section as the authority for its mission. `ingress-` keeps the management surface, the listings and the delivery-worker hosting.

Its lane was committed by me as `1f9cf98`, and the fleet bookkeeping as `507fe5d`. The working tree is now clean with nothing uncommitted.

Two things carried in the same message: the `.gitignore` defect that had excluded an entire source directory from the repository since the first commit (fixed at `f2bc1f7`), and the `git commit --amend` mistake I made in the shared tree, which destroyed `desktop-`'s checkpoint message. Both are recorded in the overview resume doc.

Next step: `keys-` builds the credential path and reports to `overview-` for verification and commit. `ingress-` takes the SignalR hub when it has the context, behind a default-off flag, with no pipeline code depending on it.
