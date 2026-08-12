# Transaction Participants

`ITransactionParticipant` lets something else write **on Fisher's connection, inside Fisher's
transaction, committed with it**.

```cs
public interface ITransactionParticipant
{
    Task BeforeCommitAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token);

    Task AfterCommitAsync(CancellationToken token) => Task.CompletedTask;
}
```

```cs
session.AddTransactionParticipant(new MyParticipant());
session.Store(order);
await session.SaveChangesAsync();      // your writes and Fisher's, or neither
```

## Why this matters more here

**One writer per database file.** An application using Fisher for its events and something else — EF
Core, Dapper, hand-written ADO.NET — for its relational tables, in the same file, which is the natural
thing to do with an embedded database, cannot write both atomically without this.

Worse, it cannot write both *at all* without contending against itself: the two transactions are two
writers on one file, and one waits or fails with `SQLITE_BUSY`. On PostgreSQL the equivalent feature
is a nicety.

## Write on the connection you are handed

::: danger
**A participant must write on the connection it is given, not merely to the same file.** Two
connections to one file are two writers, and the second blocks on the first *from inside the first's
transaction* — a genuine self-deadlock that presents as a **hang** rather than an error, with nothing
anywhere to report it.

That is why the connection is a parameter rather than something the participant is expected to find.
:::

## BeforeCommitAsync can be called more than once

::: warning
A retried `SQLITE_BUSY` re-executes the whole write delegate, so **assume your participant's
`BeforeCommitAsync` may run twice for one unit of work**. Everything it consumes has to survive being
read twice, and anything it considers "already done" has to actually be done.

This is not theoretical. It was a real, silent bug in the EF Core participant — see below.
:::

`AfterCommitAsync` is the other half of that, and it is **not** a general post-commit side-effect
hook — [`IDocumentSessionListener`](/documents/listeners) is still the seam for those. This one exists
for the narrower job the retry rule creates: a participant holding its writes replayable across
attempts needs one place to stop, and only Fisher knows when the commit happened.

It runs outside the resilience pipeline in both commit paths, and **not at all for an
[enlisted session](/documents/sessions#enlisting-in-your-own-connection-or-transaction)** — where the
commit is the caller's and there is no retry either, so there is nothing to reconcile until they
commit.

## Where the hook sits

`BeforeCommitAsync` is the **last thing inside the transaction**, the position the outbox's before
hook already occupies. So a participant's write is invisible to another connection until the commit,
and visible immediately after.

**Both commit paths invoke participants** — a session's `SaveChangesAsync` and the async daemon's
projection batch alike — so a projection or subscription that enlists one does not have to know which
it is running under.

::: tip
A participant added through a [tenant scope](/documents/multi-tenancy#writing-across-tenants) lands on
the parent, for the same reason its boundaries and metadata do: there is one transaction.
:::

## The inverse: enlisting Fisher in your transaction

`SessionOptions.ForTransaction(tx)` lets you hand Fisher a transaction *you* own. Which fits depends
on the participant: a component whose "save" is a method call rather than a connection to borrow —
`DbContext.SaveChangesAsync()` — is far easier this way round. See
[Opening Sessions](/documents/sessions#enlisting-in-your-own-connection-or-transaction).

## Entity Framework Core

`Fisher.EntityFrameworkCore` ships a participant:

```cs
dotnet add package Fisher.EntityFrameworkCore
```

```cs
// The safe form: a factory over the connection Fisher supplies.
session.AddTransactionParticipant(
    new DbContextTransactionParticipant<AppDbContext>(
        connection => new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options)));

// Or an already-built context, which is checked
session.AddTransactionParticipant(new DbContextTransactionParticipant<AppDbContext>(context));
```

::: tip
**The safe constructor takes a factory, so the trap is not expressible.** The one taking a built
context checks `Database.GetDbConnection()` **by reference** — two connections to one file have the
same connection string and are still two writers, so comparing strings would pass the exact case the
check exists to catch.
:::

::: warning
**EF Core's `SaveChangesAsync` accepts its changes when its own command succeeds, not when Fisher
commits.** Probed directly: an entity goes `Added` → `Unchanged` at the save and stays `Unchanged`
through a rollback of the enclosing transaction.

So a retried attempt found a `DbContext` that believed it had already saved, wrote nothing, and let
Fisher commit without EF's rows — invisibly, because Fisher's own work committed either way. The
participant now saves with `acceptAllChangesOnSuccess: false`. The factory form was safe by
construction, since a retry runs the factory again.
:::

Verified against EF Core 9.0.14 and Microsoft.Data.Sqlite 10.0.9 before anything was built on it:
`Database.UseTransaction` enlists, `SaveChangesAsync` writes inside the transaction, another
connection sees nothing until the commit, and a rollback takes EF's write with it. All four are what
the seam needs, and none was safe to assume.

Two packaging notes:

- **`Microsoft.EntityFrameworkCore.Relational` only.** Which EF provider your `DbContext` uses is your
  decision; referencing the SQLite provider would be Fisher making it for you, even though on SQLite
  it is nearly always the right one.
- **Pinned to EF Core 9.x, not 10.x**, because the package multi-targets net9.0 and net10.0 and EF
  Core 10 is net10-only. 9.x targets net8.0, so it loads on both.

See also [EF Core Projections](/events/projections/efcore).
