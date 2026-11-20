# Projection Backfills

You added a member to a single-stream projection's document. New events fill it, but the documents the
projection already wrote don't have it, and a rebuild replays every event to fix that. A backfill fills the new
member on the existing documents from only the events that produce it, while the projection keeps running.

A backfill runs as a Wolverine long-running job, one per database. Fisher implements the store half,
`IEventStore.RunBackfillAsync`, as Marten and Polecat will (jasperfx#1222).

## Declaring a backfill

Declare it on the projection, and start the live projection from the same mark:

<!-- snippet: sample_declare_a_backfill -->
<a id='snippet-sample_declare_a_backfill'></a>
```cs
public class WalletProjection : SingleStreamProjection<Wallet, Guid>
{
    public WalletProjection()
    {
        // Leave Version alone: a new version gets a new table, which a backfill cannot reach

        // The live projection starts where it left off, at the mark the backfill stops at
        Options.SubscribeFromPriorVersionMark("balance");

        // Fill Balance on the documents already written, from the deposits up to that mark
        Backfill("balance").On<WalletDeposited>((wallet, e) => wallet.Balance += e.Amount);
    }

    public void Apply(WalletOpened _, Wallet wallet)
    {
    }

    public void Apply(WalletDeposited e, Wallet wallet)
    {
        wallet.Deposits++;
        wallet.Balance += e.Amount;
    }
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/backfill_samples.cs#L26-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_declare_a_backfill' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`SubscribeFromPriorVersionMark` and the backfill split the event history at one stored sequence, the ceiling. The
live projection handles every event after it with the new `Apply` methods, and the backfill handles the events up to
and including it with its own handlers. Whichever of the two gets there first stores the ceiling, and the other uses
that one, so no event is handled by both or by neither.

The store refuses the declaration at startup unless a job host manages it, which Wolverine's integration sets:

<!-- snippet: sample_register_a_backfill -->
<a id='snippet-sample_register_a_backfill'></a>
```cs
options.Projections.Add(new WalletProjection(), JasperFx.Events.Projections.ProjectionLifecycle.Async);

// Wolverine's integration sets this. Without it the store refuses the Backfill(...) declaration.
options.EventGraph.HasJobHost = true;
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/backfill_samples.cs#L56-L61' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_register_a_backfill' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

It also refuses a backfill on a projection that isn't asynchronous, and one declared on a member of a composite
projection.

## Running a backfill

Wolverine drives this for you. The seam underneath it is one call per database:

<!-- snippet: sample_run_a_backfill -->
<a id='snippet-sample_run_a_backfill'></a>
```cs
var shard = store.RegisteredShardNames().Single(x => x.Name == "Wallet");

foreach (var database in await store.AllDatabases())
{
    // The first request stores the ceiling, unless the live projection's start already did.
    // A later one resumes after the stored cursor.
    var run = await BackfillRun.FromMarksAsync(database, shard, "balance", null, token);
    var totals = await store.RunBackfillAsync(run, token);
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/backfill_samples.cs#L66-L76' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_run_a_backfill' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

What it does:

- It reads only the event types the backfill handles, from the stored cursor up to the ceiling, outside the async
  daemon. Archived events are left out, as the daemon leaves them out.
- A handler is applied to a document that exists. A document that is missing or deleted is skipped.
- Each page, the documents it changes and its cursor are read and written in one `BEGIN IMMEDIATE` transaction,
  kept in `fi_event_range_marks`. A run that stops resumes after its cursor, so no event is applied twice.
- A page starts from the stored cursor when that is ahead of the run's own, so a second run of the same backfill
  does nothing twice.
- A backfill that has finished does nothing when it runs again.

## Don't bump the version

Fisher gives version 2 and above of an aggregation projection its own table: `fi_doc_wallet_2` for version 2. A
version that starts from version 1's mark would then build its documents from the events after the mark alone, and
the backfill would find nothing to patch, because version 1's documents are in `fi_doc_wallet`. Fisher doesn't copy
documents between versions' tables.

So declare the backfill on the version you have. With no prior version on record, the mark is the projection's own
progress when the new code first starts. Fisher runs one daemon per database, so that is exact: stop the old process
before you start the new one.

When the ceiling is captured, Fisher refuses with an `InvalidOperationException` if the prior version's documents are
in another table, or if this version has already run without the mark. The message names both tables. If the prior
version is retired, delete its progression rows and the mark falls back to this version's own progress.

## Rebuilding while a backfill runs

A rebuild applies every event with the new `Apply` methods, which already fill the member. If the backfill then
applied its remaining pages, those events would count twice. So Fisher refuses to rebuild the projection, or to
rebuild one of its streams with `RebuildSingleStreamAsync`, while a backfill it declares hasn't finished in that
database. A backfill that hasn't started counts as not finished.

Let the backfill finish first. If you want the rebuild to fill the member instead, remove the `Backfill(...)`
declaration, or mark the backfill done by setting its cursor mark (`backfill:{projection}:{name}:cursor`) to its
ceiling mark (`backfill:{projection}:{name}`) with `IEventDatabase.SetEventRangeMarkAsync`.

::: warning
**A backfill page and the live projection can both write the same document, and Fisher doesn't guard that yet.**
A page loads and writes its documents inside its own transaction, so it never overwrites what the live projection
wrote. The live projection loads a document before its transaction, though, so a page that commits in between is
overwritten by the live write and that document misses the backfilled value. Only a stream that gets new events
while the backfill runs is exposed. jasperfx#1222 left this race to each store's design.
:::
