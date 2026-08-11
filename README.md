# fisher

SQLite Backed Document and Event Store within the Critter Stack

> **Status: early development, but no longer a skeleton.** The event store, document storage over all
> four identity types plus strong-typed wrappers, hierarchies, numeric revisions, soft delete,
> duplicated fields, user-declared indexes, document metadata mapping, LINQ — including joins,
> grouping and both paging styles — patching, bulk insert, DCB tags, natural keys, all five projection
> shapes across all three lifecycles (composite projections included), the async projection daemon,
> subscriptions, event rewriting — including data masking and stream compacting — both tenancy styles
> (conjoined and database-per-tenant, with tenants that appear at runtime), `AddFisher(...)` DI
> registration, and the `Fisher.AspNetCore` and `Fisher.EntityFrameworkCore` companion packages all
> work and are tested. Fisher passes **all 28 suites and 230 tests** of
> `JasperFx.Events.ComplianceTests`, the shared cross-store suite Marten and Polecat also enroll in —
> which as of 2.45.0 is the whole of that library's event sourcing backlog.
>
> The one thing that is *not* there is deliberate and permanent: **no message bus.** The projection
> side-effect seam exists and the default outbox drops every message, because delivery is a bus
> integration's job here as it is on both siblings. See [CLAUDE.md](CLAUDE.md) for the current state
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
