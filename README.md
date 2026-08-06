# fisher

SQLite Backed Document and Event Store within the Critter Stack

> **Status: early development, but no longer a skeleton.** The event store, document storage over all
> four identity types plus strong-typed wrappers, LINQ, DCB tags, soft delete, duplicated fields,
> document metadata mapping, all four projection shapes across all three lifecycles, the async
> projection daemon, and event rewriting — including data masking and stream compacting — all work
> and are tested. Fisher passes **all 24 suites and 199 tests** of
> `JasperFx.Events.ComplianceTests`, the shared cross-store suite Marten and Polecat also enroll in.
>
> What is *not* there is still real — no message bus (deliberately: the side-effect seam is there and
> delivery is a bus integration's job, as it is on both siblings), no document hierarchies or numeric
> revisions, no user-declared indexes over unduplicated members, no bulk insert, no DI registration,
> and no multi-tenancy beyond a tenant id column. See [CLAUDE.md](CLAUDE.md) for the current state
> and the SQLite-specific decisions, [ROADMAP.md](ROADMAP.md) for what comes next, and
> [HANDOFF.md](HANDOFF.md) for the compliance scoreboard and the deliberate gaps.

## Working with the code

Requires the .NET 10 SDK. No database server is needed — the tests create throwaway SQLite files.

```bash
dotnet build fisher.slnx
dotnet test fisher.slnx
```

## License

Copyright © Jeremy D. Miller and contributors.

Fisher is provided as-is under the MIT license. See [LICENSE](LICENSE).
