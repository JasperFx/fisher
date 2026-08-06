# fisher

SQLite Backed Document and Event Store within the Critter Stack

> **Status: early development, but no longer a skeleton.** The event store, document storage over all
> four identity types, LINQ, DCB tags, all four projection shapes across all three lifecycles, and the
> async projection daemon all work and are tested. Fisher passes **all 21 suites and 167 tests** of
> `JasperFx.Events.ComplianceTests`, the shared cross-store suite Marten and Polecat also enroll in.
>
> What is *not* there is still substantial — no message bus, no soft delete, no duplicated fields or
> indexes, no DI registration, no multi-tenancy beyond a tenant id column, no event rewriting. See
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
