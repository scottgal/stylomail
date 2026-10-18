# Bounded proxy reliability instrument

On macOS with .NET 10, from the repository root:

```sh
bash tools/proxy-harness/reliability/run.sh /private/tmp/proxy-reliability-run-1
```

Use a fresh artifact directory for each run. Coordinate an exclusive build/loopback window with the
fleet overview before running. This command builds the driver and uses only IPv4 loopback endpoints
assigned by the OS. Docker, provider accounts and a model service are unnecessary. Certificates and
credentials are generated per run; private keys and credentials remain in memory. The temporary
public trust-anchor certificate is removed at exit. Reports contain counts, timings and exception
type names; they contain no protocol transcript, credentials or message content.

The production listener, IMAP/SMTP sessions, limiter, Host backend transport, backend authenticators,
TLS channels and SMTP stager run in-process with local clients and synthetic TLS backend servers.
IMAP clients fetch a literal and verify its body; SMTP clients submit a message and wait for backend
acceptance. PBKDF2 uses 1,000 iterations for the disposable fixture, and submission assessment and
warning analysis are immediate stand-ins. This does not exercise MIME segmentation, real analysis,
durable telemetry, POP3, management enable/disable or external providers.

`thresholds.json` versions the workload and tolerances: 16 warm-up sessions, three 10-second traffic
rounds at concurrency eight (four IMAP/four SMTP workers), then one-second settle periods. Each round
waits for zero slots, tracked clients/tasks, backend sessions and staging handles. Managed retention,
resident memory, descriptor/socket deltas and successive-round trends have separate budgets. GC and
allocation figures are recorded; allocation counts include the clients/fixture and sampler. They are
not product-only performance budgets. Concurrency is sampled from actual session slots every 2 ms;
accepted-connection wrappers, including pre-TLS and teardown, are a separate number.

Darwin `proc_pidinfo(PROC_PIDLISTFDS)` counts this process's descriptors and sockets. A deliberately
held TCP pair must raise the socket count, so an unusable census cannot look like zero. Its pre-TLS
abort is recorded separately and excluded from healthy-session deltas. Process censuses request `ps`
`comm` without argument strings and compare executable basenames case-insensitively. The shell also
requires zero driver executables before and after the run and saves `netstat -an` snapshots. Empty,
denied or unparseable probes fail closed. Exclusive rebind of all fixture ports after teardown is a
separate availability observation; a failed bind is retained rather than called a process leak.

The first fault group covers refused backends (confirmed by a ConnectionRefused control), bound
non-listening endpoints that stall TCP connection establishment, TLS-connected backends withholding
their greeting, and client half-close in both protocols, plus SMTP reset during DATA. It requires one appropriate
named `SessionOutcome`, and records exceptions escaping the production session as **unnamed**.
Backend tests configure a 350 ms connect/authentication limit; the deadline guard permits an extra
250 ms for local client TLS, authentication and scheduler noise. The injected condition remains in
the report alongside the outcome. Every case also checks resource settlement.

Exit 0 means all implemented gates passed, 1 means measured product findings, and 2 means the
instrument could not finish. `report.json` separates `findings` from `instrumentErrors`; raw process
and socket censuses and the build log are adjacent. A named outcome is not inferred from an exception.
The initial bound-endpoint test was labelled too broadly as refusal; the final instrument separates
verified ConnectionRefused from connect stall. Six unnamed-outcome cases remain on the build with
the connect-deadline repair, while the IMAP connect stall now returns Timeout inside its budget.
A red run for those assertions is evidence of the defect, not an instrument error. This bounded instrument is
the first group of spec 9.11, and does not claim indefinite stability or completion of the remaining
fault matrix, bounds, backpressure or performance work.

`source-before.json` and `source-after.json` identify the build's HEAD and hash the instrument,
thresholds and relevant production source. Source movement invalidates a run; unrelated commits
may change HEAD while the watched source hashes stay stable. Keep earlier reports when a repair
lands so the before/after comparison retains its original meaning.
