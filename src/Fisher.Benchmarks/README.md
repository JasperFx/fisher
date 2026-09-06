# Fisher.Benchmarks

A performance harness for Fisher, modeled on Marten's `EventAppenderPerfTester`. It exists to put
wall-clock-under-contention numbers — not just allocation counts — behind the perf work on the
write path (write batching inside the exclusive lock) and the first-use table-ensure migrations,
so a before/after for those changes is one command away.

Everything runs against throwaway SQLite files under the system temp directory
(`TemporaryDatabase`, the same isolation the tests use). No servers, no docker.

## Two kinds of measurement, on purpose

The harness is deliberately mixed, because the scenarios split into two shapes:

- **BenchmarkDotNet + `MemoryDiagnoser`** for the allocation-shaped work: one commit of N
  documents, one append of E events. These are micro-benchmarks where per-operation allocations
  and tight per-commit latency are the signal, and BDN's statistics are worth their setup cost.
- **A plain timed console harness** (the `EventAppenderPerfTester` shape) for the long-running and
  contended scenarios: daemon rebuilds, K parallel writers fighting over the file's one write
  lock, cold-start migrations. BDN is the wrong tool there — the interesting number is one long
  wall-clock stretch plus side telemetry (retry counts), not a distribution of nanosecond means.

## Running

Always `-c Release`; the harness prints a warning banner when built Debug.

```bash
# Every timed scenario at its checked-in (short) defaults, with a machine header
dotnet run -c Release --project src/Fisher.Benchmarks -- all

# Individual scenarios, with their knobs
dotnet run -c Release --project src/Fisher.Benchmarks -- doc-save           --docs 1000 --rounds 5
dotnet run -c Release --project src/Fisher.Benchmarks -- event-append       --streams 1000 --events-per-stream 3 --events 1000 --events-per-commit 10
dotnet run -c Release --project src/Fisher.Benchmarks -- daemon-rebuild     --events 10000
dotnet run -c Release --project src/Fisher.Benchmarks -- concurrent-writers --writers 8 --commits 50 --docs-per-commit 5
dotnet run -c Release --project src/Fisher.Benchmarks -- cold-start         --types 20 --rounds 5

# The BenchmarkDotNet micro-benchmarks (MemoryDiagnoser)
dotnet run -c Release --project src/Fisher.Benchmarks -- bdn
dotnet run -c Release --project src/Fisher.Benchmarks -- bdn --filter '*QueryBenchmarks*'
```

The checked-in defaults are sized to finish in a few minutes so the harness stays cheap to run on
every change. For a publishable number, scale up:

- Timed scenarios: raise the knobs (`--events 100000`, `--commits 500`, `--rounds 11`, …) —
  the defaults are floors, not recommendations.
- Micro-benchmarks: the default BDN job is `ShortRun` on an in-process toolchain. Everything after
  `bdn` is passed to BenchmarkDotNet unaltered, so `-- bdn --job medium` or `-- bdn --filter
  '*DocSave*'` work as usual; use `--job medium` (or longer) for numbers you intend to publish.

## The scenarios and what each one is for

1. **`doc-save`** — N documents (100/1000) queued into one `SaveChangesAsync`. This is the harness
   for the write-batching work: today every queued operation compiles and executes as its own
   command and round trip inside the one `BEGIN IMMEDIATE` transaction, so per-commit time should
   scale almost linearly with N until batching lands. The BDN `DocSaveBenchmarks` twin adds
   allocations per commit.
2. **`event-append`** — the two append shapes: many streams with a few events each (per-stream
   version bookkeeping dominates) and one stream with many events in batched commits (the version
   chain and trailing sequence read-back dominate). BDN twin: `EventAppendBenchmarks`.
3. **`daemon-rebuild`** — M events (default 10k) through an async `Snapshot<BenchTally>`; measures
   the initial catch-up from a standing start and a full `RebuildProjectionAsync` separately.
4. **`concurrent-writers`** — K parallel writers against one database file, the discriminating
   scenario for the write-lock and PRAGMA findings. Reports wall clock plus Fisher's own retry
   telemetry: the `fisher.retry` activity events the Polly pipeline records (captured with an
   `ActivityListener` on the `Fisher` source). It also runs the same total work single-writer, so
   the delta is what contention costs. Two caveats printed in the source: a contended
   `BEGIN IMMEDIATE` can wait inside the connection's 30s busy timeout and succeed with *no*
   retry event — high wall clock with zero retries is still contention — and the retry event only
   fires for a SQLITE_BUSY/SQLITE_LOCKED that reaches the resilience pipeline.
5. **`cold-start`** — a fresh database file, base schema applied, then the first unit of work
   touching T document types, which pays the first-use table-ensure migration once per type (the
   O(types × objects) migration finding). The second, identical commit is the warm contrast, so
   the report can isolate "table-ensure overhead" and "overhead per type".
6. **`QueryBenchmarks`** (BDN only) — one LINQ query end to end over a deliberately tiny table, so
   parsing the chain and rendering the SQL rather than materializing rows is what the number is
   about. The harness for the query-construction work: allocations are the signal, since the SQLite
   round trip is in-process and the same before and after. The `FilteredCount` and `FirstByMember`
   shapes read no documents (or one), which makes them the closest thing here to a per-query
   overhead reading.

## Results

`Results.md` holds the baseline numbers recorded on current `main` (machine noted there) and is
where before/after tables for the gated perf fixes should be appended, in the same format Marten's
`EventAppenderPerfTester/Results.md` uses: one fenced block per configuration, newest at the
bottom, so the history of what each change bought stays readable.
