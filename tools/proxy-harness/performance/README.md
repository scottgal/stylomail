# SMTP optional-analysis component measurements

Run `python3 tools/proxy-harness/performance/run.py /absolute/new/artifact-directory`
only in an overview-brokered build/measurement window. This tool builds its own executable,
then measures local components without starting a listener or calling a provider or database.
It records production/instrument source hashes, HEAD, SDK and inherited build policy before
and after, process censuses and host load/swap observations. A failed take is retained and
is never automatically retried. Do not edit pinned sources while it runs.

Each synthetic plain, HTML and quoted-history MIME message is exactly 1 MiB; hashes and
the parser's new-body coverage are recorded outside the timed arms. These are SMTP
full-message inputs. The IMAP optional worker's 256 KiB literal cap excludes this size.

The arms run separately, with ten warmups followed by 100 samples each:

- A stages into the actual fixed 25 MiB backing, writes in 16 KiB fragments, rewinds,
  reads into a preallocated 1 MiB destination and disposes (including backing-array clearing).
  Exact byte equality and released ownership are checked outside measurement.
- B calls the real Host warning analyzer, bounded MIME parser and real HMAC thread-token
  deriver. The synthetic ephemeral HMAC key exists only in memory and is cleared on disposal.
- C calls the real element orchestrator, including its separate MIME parse, new-body
  segmentation and point construction. Classifier results/tasks for every segment count
  and all declared scorers/elements are prebuilt. The sink records scalar counts and the
  last content-free outcome; it retains no text or points.
- B-disabled calls the Host analyzer with MIME disabled and checks the named
  `NotProduced/NoAnalyserConfigured` result. C-disabled uses the real orchestrator with
  a prebuilt classifier-unavailable result and checks named absence and zero points.
  These price disabled analysis seams, not queue scheduling or a 1 MiB IMAP worker path.
- A fixture callback control prices preallocated classifier-task selection and scalar
  sink bookkeeping; a no-op control prices the measurement wrapper. Neither is subtracted.

Median and p95 use the nearest-rank definition. Every sample's elapsed time and allocated
bytes are retained. Allocation uses `GC.GetAllocatedBytesForCurrentThread` only after
checking synchronous completion of every product ValueTask and caller/classifier/sink
thread identity. The driver contains no await or task scheduling. Asynchronous completion
or changed thread fails closed without publishing a report; that case requires an isolated
process paired-total-allocation instrument, rather than interpreting these thread figures.
Scalar fixture callback checks remain inside C and are disclosed by the separate control.
Setup input generation/hash/control-parse costs are recorded separately; global component
construction and prebuilt fixture responses are outside all sample boundaries.

Positive controls check the exact staged bytes, available warning analysis, full scorer/element
point counts, 32-segment truncation for long plain/HTML bodies and one untruncated current-body
segment for quoted history. Disabled arms check actual named reasons and zero points.

There is no numerical pass budget. These measurements inform whether duplicate MIME
parsing/copying merits repair and which target to propose. They establish no provider,
SQLite, network, queue, throughput or end-to-end performance claim.
