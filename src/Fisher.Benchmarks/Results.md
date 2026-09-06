# Fisher.Benchmarks results

Before/after log for the perf work, in the `EventAppenderPerfTester/Results.md` style: one fenced
block per configuration, newest at the bottom, so the history of what each change bought stays
readable. Append a new section per change; do not rewrite old ones.

All numbers are machine-dependent — record the machine header the harness prints with every run.

## Template for a new entry

```
## <change being measured> (<date>, <commit>)

<machine header from the harness>

<paste of `-- all` output>

<paste of the `-- bdn` summary table>

Notes: <what changed, what moved, anything surprising>
```

---

## Baseline — current `main` (2026-09-05, fa81b40)

Recorded before any of the gated perf fixes (write batching inside the exclusive lock, per-type
table-ensure migration work), as the reference those changes are measured against. Checked-in
short defaults; treat deltas, not absolutes, as the signal.

```
Fisher.Benchmarks — 2026-09-05 17:29 -05:00
  OS:        macOS 26.4.1 (Arm64)   (Apple Silicon, 18 logical CPUs)
  .NET:      .NET 10.0.1
  Config:    Release
```

Timed scenarios (`dotnet run -c Release --project src/Fisher.Benchmarks -- all`):

```
== doc-save (100 docs/commit, 5 rounds) ==
  median commit                                              3.3 ms
  fastest commit                                             1.7 ms
  slowest commit                                             4.4 ms
  docs/sec at median                                       30,344/s

== doc-save (1000 docs/commit, 5 rounds) ==
  median commit                                             23.4 ms
  fastest commit                                            20.8 ms
  slowest commit                                            26.6 ms
  docs/sec at median                                       42,757/s

== event-append many-streams (1000 streams x 3 events) ==
  total                                                    280.0 ms
  commits/sec                                               3,572/s
  events/sec                                               10,716/s

== event-append single-stream (1000 events, 10/commit) ==
  total                                                    104.1 ms
  commits/sec                                                 961/s
  events/sec                                                9,608/s

== daemon-rebuild (10000 events, 1000 streams, async Snapshot<BenchTally>) ==
  seed (not daemon time)                                   837.4 ms
  initial catch-up                                         186.5 ms
  catch-up events/sec                                      53,631/s
  rebuild                                                  109.9 ms
  rebuild events/sec                                       90,960/s

== concurrent-writers (8 writers x 50 commits x 5 docs) ==
  total                                                    134.8 ms
  commits/sec                                               2,968/s
  docs/sec                                                 14,842/s
  fisher.retry events                                             0
  commits that retried                                            0
  max retry attempt                                               0
  failed commits                                                  0

== concurrent-writers (1 writers x 400 commits x 5 docs) ==
  total                                                     74.8 ms
  commits/sec                                               5,350/s
  docs/sec                                                 26,750/s
  fisher.retry events                                             0
  commits that retried                                            0
  max retry attempt                                               0
  failed commits                                                  0

== cold-start (20 document types, 5 rounds) ==
  median first commit (table ensure)                        61.0 ms
  median second commit (warm)                                0.6 ms
  table-ensure overhead                                     60.3 ms
  overhead per type                                          3.0 ms
```

Micro-benchmarks (`dotnet run -c Release --project src/Fisher.Benchmarks -- bdn`, ShortRun,
in-process — see README for the publishable-run configuration):

```
| Method        | Documents | Mean      | Error     | StdDev    | Gen0     | Gen1     | Allocated  |
|-------------- |---------- |----------:|----------:|----------:|---------:|---------:|-----------:|
| SaveDocuments | 100       |  1.481 ms |  1.179 ms | 0.0646 ms |  50.7813 |  11.7188 |   439.1 KB |
| SaveDocuments | 1000      | 13.229 ms | 11.383 ms | 0.6239 ms | 515.6250 | 187.5000 | 4320.19 KB |

| Method                 | EventsPerCommit | Mean      | Error     | StdDev    | Gen0    | Gen1   | Allocated |
|----------------------- |---------------- |----------:|----------:|----------:|--------:|-------:|----------:|
| AppendToExistingStream | 1               |  74.15 us |  35.45 us |  1.943 us |  3.2959 | 0.1221 |  27.23 KB |
| StartNewStream         | 1               | 156.41 us | 209.58 us | 11.488 us |  3.4180 |      - |  28.13 KB |
| AppendToExistingStream | 10              | 434.66 us |  51.07 us |  2.799 us | 33.6914 | 2.4414 | 275.55 KB |
| StartNewStream         | 10              | 544.08 us | 561.55 us | 30.781 us | 34.1797 | 2.4414 | 279.27 KB |
```

Notes:

- **doc-save**: per-commit time scales roughly linearly with N (3.3 ms @ 100 → 23.4 ms @ 1000),
  which is the one-command-and-round-trip-per-operation shape the write-batching fix targets.
- **concurrent-writers**: 8 writers doing the same total work as 1 writer took ~1.8x the wall
  clock (134.8 ms vs 74.8 ms) with zero `fisher.retry` events — the contention cost shows up as
  waiting inside `BEGIN IMMEDIATE` under the connection's busy timeout, not as pipeline retries.
  Exactly the caveat in the scenario's remarks; both numbers matter for the write-lock work.
- **cold-start**: ~3 ms of first-use table-ensure per document type on this machine, ~60 ms for a
  20-type store — the per-type migration cost the table-ensure fix goes after; the warm commit of
  the identical shape is 0.6 ms.
- **micro-benchmarks**: allocations scale linearly with the operation count — ~4.3 KB per document
  saved and ~27 KB per single-event append commit — consistent with the per-operation
  command-building the batching work targets. ShortRun error bars are wide by design; use
  `-- bdn --job medium` before quoting a delta.
