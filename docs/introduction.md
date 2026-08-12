# Introduction

Welcome to the Fisher documentation!

## What is Fisher?

**Fisher is a .NET library for building applications using a
[document-oriented database approach](https://en.wikipedia.org/wiki/Document-oriented_database) and
[Event Sourcing](https://martinfowler.com/eaaDev/EventSourcing.html), backed by
[SQLite](https://sqlite.org).**

Fisher is part of the [Critter Stack](https://jasperfx.net) ecosystem and mirrors the API patterns of
[Marten](https://martendb.io) (PostgreSQL) and [Polecat](https://polecat.jasperfx.net) (SQL Server),
so a team already using either one will recognise nearly everything here.

::: tip
If you've used Marten or Polecat before, you'll feel at home. Same interface names, same session
patterns, same projection model. What differs is what SQLite makes cheap and what it makes
impossible — and this documentation says which is which rather than leaving you to find out.
:::

The thing that makes Fisher different from its siblings is not the SQL dialect. It is that **there is
no database server**. SQLite runs inside your process, a store is a file, and the "network round
trip" that shapes so much of a document database's design simply is not there.

Fisher is built on:

- **[JasperFx.Events](https://github.com/JasperFx/jasperfx)** — the shared event, projection and
  daemon abstractions the whole Critter Stack implements,
- **[Weasel.Sqlite](https://github.com/JasperFx/weasel)** — schema management, migrations and data
  access,
- **Weasel.Storage** — the dialect-neutral document and event storage runtime extracted from Marten.

Fisher supplies the SQLite dialects and the storage seams. Everything above them is shared with
Marten and Polecat, which is why a projection written for one store runs unaltered on another.

## Main Features

| Feature | Description |
| :---: | :---: |
| [Document Storage](/documents/) | Store entities as JSON documents with full LINQ querying support. |
| [Event Store](/events/) | Full event store with stream management, projections and subscriptions. |
| [Strong Consistency](/documents/sessions) | Documents and events commit in one SQLite transaction. |
| [LINQ Querying](/documents/querying/linq/) | Where, ordering, paging, projections, grouping, aggregates and joins. |
| [Event Projections](/events/projections/) | Inline, live and asynchronous read models. |
| [Automatic Schema Management](/schema/migrations) | Weasel.Sqlite creates and migrates tables. |
| [Optimistic Concurrency](/documents/concurrency) | Guid version *or* numeric revision. |
| [Multi-Tenancy](/configuration/multitenancy) | Conjoined tables, or a database file per tenant. |
| [Async Daemon](/events/projections/async-daemon) | Background projection processing. |
| [ASP.NET Core Integration](/documents/aspnetcore) | Streaming JSON results and ETag handling. |
| [EF Core Integration](/events/projections/efcore) | A `DbContext` writing inside Fisher's transaction. |

## Critter Stack Ecosystem

| Library | Purpose |
| :---: | :---: |
| [Marten](https://martendb.io) | PostgreSQL document database and event store |
| [Polecat](https://polecat.jasperfx.net) | SQL Server document database and event store |
| [Wolverine](https://wolverinefx.net) | Messaging and command processing |
| [JasperFx](https://jasperfx.net) | Core framework and event sourcing abstractions |
| [Weasel](https://github.com/JasperFx/weasel) | Database schema management |

## Fisher vs Marten and Polecat

The API is deliberately the same. The storage decisions are not, and these are the ones worth
knowing up front:

| Concern | Marten (PostgreSQL) | Polecat (SQL Server) | Fisher (SQLite) |
| :--- | :--- | :--- | :--- |
| Schemas | real schemas | real schemas | **none** — folded into the table prefix |
| JSON | `jsonb` | native `json` | TEXT + json1 functions |
| Timestamps | `timestamptz` | `datetimeoffset` | ISO-8601 TEXT, fixed width, UTC |
| Booleans | `boolean` | `bit` | INTEGER 0/1 |
| Guids | native `uuid` | `uniqueidentifier` | lowercase canonical TEXT |
| Event sequence | sequence | `IDENTITY` | `INTEGER PRIMARY KEY AUTOINCREMENT` |
| Document upsert | `INSERT … ON CONFLICT` | `MERGE` | `INSERT … ON CONFLICT … RETURNING` |
| Append concurrency | advisory lock | `UPDLOCK, HOLDLOCK` | `BEGIN IMMEDIATE` |
| Writers | many | many | **one per database file** |
| Serialization | STJ or Newtonsoft | System.Text.Json | System.Text.Json |

The single-writer row is the one that propagates. It is why Fisher's unit of work is strictly
sequential, why [transaction participants](/documents/transaction-participants) and
[raw SQL commands](/documents/querying/raw-sql) matter more here than on either sibling, why a
[busy retry](/configuration/retries) is a real policy rather than a formality, and why
[database-per-tenant](/configuration/multitenancy) is a *performance* feature and not only an
isolation one.

## What Fisher deliberately does not do

- **There is no message bus.** Projections can [publish side effects](/events/projections/side-effects),
  and the default outbox drops every message. Delivery is a bus integration's job here as it is on
  Marten and Polecat.
- **There is no table partitioning.** SQLite has no partition functions or schemes, and the nearest
  equivalent carries none of the operational properties that make the feature worth having.
- **There is no hot-cold daemon failover.** Leader election across nodes means several processes
  sharing one file, which SQLite does not make safe. The daemon runs `Solo`.
- **Fisher ships no binary event serializer.** The [seam exists](/events/storage#binary-event-bodies);
  choosing MessagePack or protobuf is your application's decision about how its data ages.

## History and Origins

The Critter Stack names its projects after animals, and the mustelid family has been good to it. A
[fisher](https://en.wikipedia.org/wiki/Fisher_\(animal\)) is a small, quick North American mustelid —
a cousin of the marten and the polecat, and the smallest of the three, which is about right for the
store that fits in a file.
