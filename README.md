# fisher

SQLite Backed Document and Event Store within the Critter Stack

> **Status: early development, but no longer a skeleton.** The event store, document storage over all
> four identity types plus strong-typed wrappers, hierarchies, numeric revisions, soft delete,
> duplicated fields, user-declared indexes, document metadata mapping, LINQ, DCB tags, all four
> projection shapes across all three lifecycles, the async projection daemon, subscriptions, event
> rewriting — including data masking and stream compacting — and `AddFisher(...)` DI registration all
> work and are tested. Fisher passes **all 26 suites and 216 tests** of
> `JasperFx.Events.ComplianceTests`, the shared cross-store suite Marten and Polecat also enroll in.
>
> What is *not* there is still real — no message bus (deliberately: the side-effect seam is there and
> delivery is a bus integration's job, as it is on both siblings), no composite projections, no bulk
> insert, no natural keys, and no multi-tenancy beyond a tenant id column. See
> [CLAUDE.md](CLAUDE.md) for the current state and the SQLite-specific decisions,
> [ROADMAP.md](ROADMAP.md) for what comes next, and [HANDOFF.md](HANDOFF.md) for the compliance
> scoreboard and the deliberate gaps.

## Working with the code

Requires the .NET 10 SDK. No database server is needed — the tests create throwaway SQLite files.

```bash
dotnet build fisher.slnx
dotnet test fisher.slnx
```

## License

Copyright © Jeremy D. Miller and contributors.

Fisher is provided as-is under the MIT license. See [LICENSE](LICENSE).
