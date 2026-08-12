# Resiliency Policies

Fisher executes database work through a [Polly](https://www.pollydocs.org) resilience pipeline on
`StoreOptions.ResiliencePipeline`. Unlike Marten and Polecat, where the equivalent is largely a
formality, **this one earns its place**: SQLite permits one writer per database file, so a second
writer gets `SQLITE_BUSY` or `SQLITE_LOCKED` rather than waiting in a queue somebody else manages.

## The default

The default pipeline retries the transient SQLite busy/locked errors with a backoff. You can extend
it or replace it:

```cs
// Keep Fisher's defaults and add to them
opts.ExtendPolly(builder =>
{
    builder.AddTimeout(TimeSpan.FromSeconds(10));
});

// Start from Fisher's defaults and reconfigure
opts.ConfigurePolly(builder =>
{
    builder.AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 5,
        Delay = TimeSpan.FromMilliseconds(50),
        BackoffType = DelayBackoffType.Exponential
    });
});
```

## Where the retry does *not* apply, and why

This is the part worth reading. A retried delegate runs **again from the top**, so anything the
delegate consumed has to survive being read twice, and anything it did after a successful commit must
not be repeated. Several paths therefore run deliberately outside the pipeline:

| Path | Why it is outside |
| :--- | :--- |
| `IMessageBatch.AfterCommitAsync` | A post-commit publish inside a retried delegate fires twice for a transaction that already committed. |
| `IDocumentSessionListener.AfterCommitAsync` | Same reason. |
| A subscription's post-commit listener | Same reason. |
| `ITransactionParticipant.AfterCommitAsync` | Same reason. |
| Dirty-tracking re-baselining | Re-baselining inside a retried delegate leaves the retry comparing against a snapshot it had already taken. |
| `IAdvancedSql.StreamAsync` | A live reader yielded to the caller would resume against a disposed connection. |
| An [enlisted](/documents/sessions#enlisting-in-your-own-connection-or-transaction) session's commit | The failed attempt's transaction is the *caller's* and is still open, so a retry would re-write everything the first attempt wrote. |

And one that runs inside but had to be made re-entrant: the async daemon's projection batch takes its
operations *before* the pipeline and executes from that snapshot inside it. Draining them inside left
a retry with nothing to write while the progression row still committed — advancing a projection past
events whose documents were never written, with no error anywhere.

::: warning
If you write an `ITransactionParticipant`, assume `BeforeCommitAsync` **can be called more than
once** for one unit of work. This is not theoretical: EF Core's `SaveChangesAsync` accepts its
changes when its own command succeeds, not when Fisher commits, so a naive participant's second
attempt finds a `DbContext` that believes it has already saved and writes nothing.
[The EF Core participant](/documents/transaction-participants) handles this by saving with
`acceptAllChangesOnSuccess: false`.
:::

## Seeing a retry happen

A retry is recorded as an event on the enclosing [trace span](/diagnostics#tracing), not as a span of
its own — a retry is the same operation happening again. That is often the only way to tell a request
that was slow from one that spent its time queued behind another writer.

## What a retry cannot fix

A retry helps when the lock is *transient*. It does not help when your application is holding the
write lock against itself:

- Two connections to one file are two writers. A component writing on its own connection from inside
  Fisher's transaction **deadlocks against itself** and presents as a hang, not an error. Use
  [transaction participants](/documents/transaction-participants) or
  [`QueueSqlCommand`](/documents/querying/raw-sql).
- A long transaction blocks every other writer for its whole duration. That is why
  [bulk insert](/documents/bulk-insert) batches.
- If you genuinely need concurrent writers, split across files:
  [database-per-tenant](/configuration/multitenancy#database-per-tenant) or
  [multiple stores](/configuration/multiple-stores).

## Timeouts are a different knob

Retries respond to a busy database. They do not bound how long a statement waits for the lock in the
first place, and they are not what interrupts a slow query. See
[Timeouts, and which knob does what](/configuration/sqlite#timeouts-and-which-knob-does-what).
