# Fisher

SQLite-backed Event Store and Document Database inside the Critter Stack.

[![Discord](https://img.shields.io/discord/1074998995086225460?color=blue&label=Chat%20on%20Discord)](https://discord.gg/WMxrvegf8H)
[![Nuget Package](https://badgen.net/nuget/v/fisher)](https://www.nuget.org/packages/Fisher/)
[![Nuget](https://img.shields.io/nuget/dt/fisher)](https://www.nuget.org/packages/Fisher/)

<div align="center">
    <img src="./logo.png" alt="Fisher logo" width="40%">
</div>

**Documentation: [fisher.jasperfx.net](https://fisher.jasperfx.net/)**

Fisher is [Marten](https://martendb.io) and [Polecat](https://polecat.jasperfx.net) for SQLite —
event sourcing and document storage with the same API, in a database that is a **file inside your own
process**. There is no server to install, nothing to provision, and nothing to keep running. Backup is
`cp`. Tests need no fixture.

> **Status: 1.0.** The event store, document storage over all four
> identity types plus strong-typed wrappers, hierarchies, numeric revisions, soft delete, duplicated
> fields, user-declared indexes, document metadata mapping, LINQ — including joins, grouping and both
> paging styles — patching, bulk insert, DCB tags, natural keys, all five projection shapes across all
> three lifecycles (composite projections included), the async projection daemon, subscriptions, event
> rewriting — including data masking and stream compacting — both tenancy styles (conjoined and
> database-per-tenant, with tenants that appear at runtime), `AddFisher(...)` DI registration, and the
> `Fisher.AspNetCore` and `Fisher.EntityFrameworkCore` companion packages all work and are tested.
>
> Fisher passes **all 40 suites and 391 tests** it enrolls from `JasperFx.Events.ComplianceTests`,
> the shared cross-store suite Marten and Polecat also enroll in, alongside its own 1,554.
>
> That suite pins **API portability, not behavioural equivalence** — code written against one store
> compiles and runs against another. It does not pin that the three behave identically, and they do
> not: concurrency under contention, string collation, timestamp precision and staleness semantics all
> differ. The [migration guide](https://fisher.jasperfx.net/migration-guide#behaviour-that-differs)
> lists them, which is also why Fisher is not a drop-in test double for a Marten or Polecat
> application.
>
> 1.0 means the API is stable and the semantics above are settled — not that Fisher is
> feature-complete against Marten. The gaps that remain are **decisions**, documented in
> [HANDOFF.md](HANDOFF.md) rather than filed as issues.
>
> The one thing that is *not* there is deliberate and permanent: **no message bus.** The projection
> side-effect seam exists and the default outbox drops every message, because delivery is a bus
> integration's job here as it is on both siblings.

## Getting started

```shell
dotnet add package Fisher
```

```cs
builder.Services.AddFisher(opts =>
{
    opts.Connection("Data Source=app.db");
})
.ApplyAllDatabaseChangesOnStartup();
```

```cs
// Documents
session.Store(new User { FirstName = "Jane", LastName = "Doe" });
await session.SaveChangesAsync();

var users = await session.Query<User>().Where(x => x.LastName == "Doe").ToListAsync();

// Events
var stream = session.Events.StartStream<Order>(new OrderPlaced("Acme", 199.95m));
await session.SaveChangesAsync();

var order = await session.Events.AggregateStreamAsync<Order>(stream.Id);
```

See [Getting Started](https://fisher.jasperfx.net/getting-started) for the full walkthrough, and
[Why Fisher?](https://fisher.jasperfx.net/whitepaper) for where it fits and where it does not.

## What SQLite changes

The API is Marten's. The storage decisions are not, and the honest summary is that SQLite makes some
things cheaper and one thing harder:

**Cheaper.** Duplicated fields are `VIRTUAL` generated columns — no backfill, no drift, and a patch
has nothing to refresh. Declared indexes are expression indexes and add no column at all. Patching
needs no server-side function installed. JSON-returning reads are byte-exact and skip the *whole*
round trip rather than a fraction of it, because the database is your process. Joins are plain SQL.
Database-per-tenant is a file per tenant, so it buys concurrency as well as isolation.

**Harder.** One writer per database file. That is why the unit of work is sequential, why a busy retry
is a real policy, why `ITransactionParticipant` and `QueueSqlCommand` matter more here than on either
sibling, and why the exclusive append methods *fail* where Marten's and Polecat's *wait*.

All of it is documented rather than left to be discovered — see the
[migration guide](https://fisher.jasperfx.net/migration-guide) for the behaviours that differ.

## Companion packages

```shell
dotnet add package Fisher.AspNetCore          # streaming IResult types, ETags, a daemon health check
dotnet add package Fisher.EntityFrameworkCore # a DbContext inside Fisher's transaction
```

## Support Plans

<div align="center">
    <img src="https://www.jasperfx.net/logo.png" alt="JasperFx logo" width="70%">
</div>

While Fisher is open source, [JasperFx Software offers paid support and consulting
contracts](https://jasperfx.net/support-plans/) for the Critter Stack.

## Help us keep working on this project 💚

[Become a Sponsor on GitHub](https://github.com/sponsors/JasperFX) by sponsoring monthly or one time.

## Working with the Code

You need the **.NET 10 SDK** ([available here](https://dotnet.microsoft.com/download)) and the .NET 9
runtime to run that target framework's suites.

**There is no database server to run.** No `docker-compose up`, no connection string to configure, no
container to wait for — the tests create throwaway SQLite files under the temp directory. This is the
whole point of the library, and it applies to its own build as much as to yours.

```bash
dotnet build fisher.slnx
dotnet test fisher.slnx                    # both TFMs; they run serially on purpose
dotnet test fisher.slnx -f net10.0         # one TFM

# One test class or method (Microsoft Testing Platform — note the bare `--`)
dotnet test src/Fisher.Tests/Fisher.Tests.csproj -f net10.0 -- --filter-method "*appending_events*"
```

## Documentation

The docs are written in Markdown under `docs/` and published as a static
[VitePress](https://vitepress.dev) site, as the other JasperFx projects are.

```bash
npm install
npm run docs          # dev server at http://localhost:5173
npm run docs-build    # a dead internal link fails the build
```

## Contributing

Fisher is developed in the open. Read [CLAUDE.md](CLAUDE.md) first — it is the architectural account
of the library, including every SQLite divergence and the reason behind it, and it will save you
rediscovering a trap that has already bitten. [ROADMAP.md](ROADMAP.md) is what comes next and in what
order; [HANDOFF.md](HANDOFF.md) is the compliance scoreboard and the deliberate gaps.

## License

Copyright © Jeremy D. Miller and contributors.

Fisher is provided as-is under the MIT license. For more information see [LICENSE](LICENSE).

## Code of Conduct

This project has adopted the code of conduct defined by the
[Contributor Covenant](http://contributor-covenant.org/) to clarify expected behavior in our
community.
