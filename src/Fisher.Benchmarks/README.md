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
dotnet run -c Release --project src/Fisher.Benchmarks -- writer-queue       --writers 8 --total-commits 400 --docs-per-commit 5 --rounds 7
dotnet run -c Release --project src/Fisher.Benchmarks -- writer-queue       --writers 8,16 --modes race,queue,bh-mimic,bh-sleep1ms,group --workload events-hot --rounds 11
dotnet run -c Release --project src/Fisher.Benchmarks -- writer-queue       --writers 8 --total-commits 4000 --docs-per-commit 1 --workload events-many --daemon 1 --modes race,queue,bh-sleep1ms

# The BenchmarkDotNet micro-benchmarks (MemoryDiagnoser)
dotnet run -c Release --project src/Fisher.Benchmarks -- bdn
dotnet run -c Release --project src/Fisher.Benchmarks -- bdn --filter '*QueryBenchmarks*'
dotnet run -c Release --project src/Fisher.Benchmarks -- bdn --filter '*Binary*'
dotnet run -c Release --project src/Fisher.Benchmarks -- binary-bind-probe
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
5. **`writer-queue`** (fisher#547) — the `concurrent-writers` shape run three ways with the same total
   work: racing for `BEGIN IMMEDIATE` as today, through a benchmark-only `Channel<T>` single-writer
   queue (`SingleWriterQueue`), and through a `SemaphoreSlim(1)` control. Reports wall clock, commit
   latency percentiles, allocations per commit, the `fisher.write_lock.wait` histogram and both retry
   signals. `--writers N` (or a list, `--writers 1,8,16`) runs those counts; without it, it sweeps
   1/2/4/8/16. The queue is a measurement aid only; Fisher's write path does not use it.
   Round 2 (fisher#547) adds, all benchmark-only:
   - `--modes` picks from `race`, `queue`, `gate`, `bh-mimic`, `bh-yield`, `bh-spin50us`, `bh-sleep1ms`,
     `bh-none`, `bounded1`, `bounded4` and `group` (default `race,queue,gate`). The `bh-*` modes race as
     today but replace SQLite's busy handler on every connection the store opens (`BusyHandlers`, a
     native `sqlite3_busy_handler` installed through `StoreOptions.Functions`); `bh-mimic` is a counting
     copy of SQLite's default handler. `bounded*` are the queue on a bounded channel with
     `FullMode.Wait`. `group` is a group-commit prototype (`GroupCommitQueue`): queued units of work
     replayed as enlisted sessions, one `SAVEPOINT` each, one `BEGIN IMMEDIATE` and one `COMMIT` per
     group, capped by `--group-size` (default 64).
   - `--workload docs` (default), `events-many` (`--docs-per-commit` new streams per commit, one event
     each) or `events-hot` (one stream; each writer reads its version and appends expecting it, and a
     lost race is counted as a version conflict).
   - `--daemon 1` runs an async `Snapshot<BenchTally>` under the daemon during the writes and reports
     the daemon's catch-up time and its own `fisher.write_lock.wait` (the `daemon` holder tag).
   - Process CPU time per round, and the busy handler's invocation count and time inside it.
6. **`cold-start`** — a fresh database file, base schema applied, then the first unit of work
   touching T document types, which pays the first-use table-ensure migration once per type (the
   O(types × objects) migration finding). The second, identical commit is the warm contrast, so
   the report can isolate "table-ensure overhead" and "overhead per type".
7. **`QueryConstructionBenchmarks`** (BDN only) — the same query shapes split into **construct**
   (`session.ToSql(...)`: walk the expression tree, resolve members, build the `Statement`, render the
   SQL) and **full** (construct, execute, materialize), on one warm session so the pair is comparable.
   This is the harness for the compiled-query question: a compiled query removes the construct half
   and nothing else, so the construct/full ratio *is* the answer. `ByIdConstruct`/`ByIdFull` is the
   ceiling — an index seek returning one row, the cheapest execution there is, so the largest share
   construction can hold. See `Results.md` for the numbers and fisher#195 for what they decided.
8. **`QueryBenchmarks`** (BDN only) — one LINQ query end to end over a deliberately tiny table, so
   parsing the chain and rendering the SQL rather than materializing rows is what the number is
   about. The harness for the query-construction work: allocations are the signal, since the SQLite
   round trip is in-process and the same before and after. The `FilteredCount` and `FirstByMember`
   shapes read no documents (or one), which makes them the closest thing here to a per-query
   overhead reading.
9. **`BinaryEncodeBenchmarks`, `BinaryColumnReadBenchmarks`, `BinaryEventStoreBenchmarks`** (BDN) and
   **`binary-bind-probe`** (fisher#570) — binary event bodies through `IEventBinarySerializer`, with two
   MemoryPack serializers: one implementing only the `byte[]` pair, and one overriding JasperFx 3.3.0's
   `IBufferWriter` / `ReadOnlySpan` members. The serializer step alone, the BLOB column read three ways,
   and end to end through a store (ten-event append + commit, ten-event `FetchStreamAsync`), at 200-byte
   and 100 KB bodies. `binary-bind-probe` is not a timing: it reports which BLOB parameter shapes
   Microsoft.Data.Sqlite will bind and what binding a truncated array costs. Rerun it after a provider
   bump; it is the first thing that would make the span members worth revisiting.

## Results

`Results.md` holds the baseline numbers recorded on current `main` (machine noted there) and is
where before/after tables for the gated perf fixes should be appended, in the same format Marten's
`EventAppenderPerfTester/Results.md` uses: one fenced block per configuration, newest at the
bottom, so the history of what each change bought stays readable.
