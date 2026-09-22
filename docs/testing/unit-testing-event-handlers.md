# Unit Testing Event Handlers

A command handler that takes an `IEventStream<T>` — Wolverine's `[WriteAggregate]`, or the body you
pass to `WriteToAggregate` — is a **decision**: aggregate state in, events out. `StubEventStream<T>`
stands in for the stream so you can test that decision with no store at all.

```cs
var stream = new StubEventStream<Account>(new Account { Balance = 100m });

WithdrawHandler.Handle(new Withdraw(stream.Id, 30m), stream);

stream.EventsAppended.ShouldBe([new AccountDebited(30m)]);
```

::: tip
`StubEventStream<T>` is `JasperFx.Events.StubEventStream<T>` — not a Fisher type. Fisher's
`IEventStream<T>` *is* the JasperFx interface, so the stub satisfies it as-is, and a handler's
decision tests are **the same code on Marten, Polecat and Fisher**. That is worth more than it
sounds if you are prototyping on Fisher and deploying on one of the others.
:::

## The honest case for it on Fisher

The usual argument for a stub is "avoid paying for a database in a unit test", and on Fisher that
cost is a throwaway file — [integration testing](/testing/integration) opens by calling that one of
Fisher's strongest cases, and it is. So the argument here has to be a different one.

**It tests a decision, not a store.** No file, no schema creation, no WAL, no serialization. The
shape it pays off on is a table-driven test over a dozen aggregate states, which would otherwise be a
dozen file-backed stores.

**It is the alternative to mocking `IEventStream<T>`,** which is what you reach for otherwise:

```cs
// Don't
var stream = Substitute.For<IEventStream<Account>>();
stream.Aggregate.Returns(new Account { Balance = 100m });

WithdrawHandler.Handle(new Withdraw(Guid.NewGuid(), 30m), stream);

stream.Received(1).AppendOne(Arg.Any<AccountDebited>());
```

That last line proves a method was called. It says nothing about *which* event or *what it carried* —
which is the entire content of the handler's decision — and it breaks on a refactor that appends the
same events through `AppendMany`.

::: warning Where the stub stops
It records. It does not persist, project, validate, or raise a concurrency exception, and
`TryFastForwardVersion()` does nothing.

On Fisher the integration test is cheap, so **anything touching persistence belongs in one**:
optimistic concurrency, inline projections, the one-writer-per-file contention story, and the
Solo-only [async daemon](/events/projections/async-daemon). Use the stub for branch coverage over a
decision, and [integration tests](/testing/integration) for everything that is about the store.
:::

## The aggregate and the handler

<!-- snippet: sample_unit_testing_account -->
<a id='snippet-sample_unit_testing_account'></a>
```cs
public record AccountDebited(decimal Amount);

public record AccountOverdrawn(decimal Balance);

public class Account
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }
    public bool IsFrozen { get; set; }

    public void Apply(AccountDebited e) => Balance -= e.Amount;
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/unit_testing_samples.cs#L17-L30' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_unit_testing_account' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: sample_unit_testing_handler -->
<a id='snippet-sample_unit_testing_handler'></a>
```cs
public static class WithdrawHandler
{
    public static void Handle(Withdraw command, IEventStream<Account> stream)
    {
        var account = stream.Aggregate
                      ?? throw new InvalidOperationException("No such account.");

        if (account.IsFrozen)
        {
            throw new InvalidOperationException("The account is frozen.");
        }

        stream.AppendOne(new AccountDebited(command.Amount));

        if (account.Balance - command.Amount < 0)
        {
            stream.AppendOne(new AccountOverdrawn(account.Balance - command.Amount));
        }
    }
}

public record Withdraw(Guid AccountId, decimal Amount);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/unit_testing_samples.cs#L32-L55' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_unit_testing_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Asserting on the events

<!-- snippet: sample_unit_testing_the_happy_path -->
<a id='snippet-sample_unit_testing_the_happy_path'></a>
```cs
[Fact]
public void a_withdrawal_within_the_balance_debits_and_nothing_else()
{
    var stream = new StubEventStream<Account>(new Account { Balance = 100m });

    WithdrawHandler.Handle(new Withdraw(stream.Id, 30m), stream);

    // The decision, stated as the events it produced — not as "AppendOne was called".
    stream.EventsAppended.ShouldBe([new AccountDebited(30m)]);
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/unit_testing_samples.cs#L59-L70' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_unit_testing_the_happy_path' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A branch that appends more than one event reads the same way:

<!-- snippet: sample_unit_testing_the_branch -->
<a id='snippet-sample_unit_testing_the_branch'></a>
```cs
[Fact]
public void overdrawing_also_raises_the_overdraft_event()
{
    var stream = new StubEventStream<Account>(new Account { Balance = 20m });

    WithdrawHandler.Handle(new Withdraw(stream.Id, 50m), stream);

    stream.EventsAppended.ShouldBe([
        new AccountDebited(50m),
        new AccountOverdrawn(-30m)
    ]);
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/unit_testing_samples.cs#L72-L85' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_unit_testing_the_branch' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

**Assert the empty case too.** A handler that decides to do nothing is a decision like any other, and
it is the one a mock-based test most often lets through:

<!-- snippet: sample_unit_testing_nothing_appended -->
<a id='snippet-sample_unit_testing_nothing_appended'></a>
```cs
[Fact]
public void a_frozen_account_is_refused_and_appends_nothing()
{
    var stream = new StubEventStream<Account>(new Account { Balance = 100m, IsFrozen = true });

    Should.Throw<InvalidOperationException>(
        () => WithdrawHandler.Handle(new Withdraw(stream.Id, 30m), stream));

    // The half that is easy to leave out, and the one a mock will not give you for free.
    stream.EventsAppended.ShouldBeEmpty();
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/unit_testing_samples.cs#L87-L99' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_unit_testing_nothing_appended' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## A stream that does not exist yet

`Aggregate` is null for a stream that has never been written — which is what a handler that *starts*
a stream should see, and what a handler that requires an existing aggregate has to refuse:

<!-- snippet: sample_unit_testing_missing_stream -->
<a id='snippet-sample_unit_testing_missing_stream'></a>
```cs
[Fact]
public void a_stream_that_does_not_exist_yet_has_a_null_aggregate()
{
    var stream = new StubEventStream<Account>(null);

    Should.Throw<InvalidOperationException>(
        () => WithdrawHandler.Handle(new Withdraw(stream.Id, 30m), stream));
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/unit_testing_samples.cs#L101-L110' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_unit_testing_missing_stream' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Identity and versions

`Id` and `Key` are both populated by default, because the stub does not know which identity style the
handler reads. Set whichever it does — and set `StartingVersion` / `CurrentVersion` for a
version-sensitive handler:

<!-- snippet: sample_unit_testing_identity_and_version -->
<a id='snippet-sample_unit_testing_identity_and_version'></a>
```cs
[Fact]
public void the_identity_and_the_versions_are_settable()
{
    var accountId = Guid.NewGuid();

    var stream = new StubEventStream<Account>(new Account { Balance = 100m })
    {
        Id = accountId,
        Key = accountId.ToString(),
        StartingVersion = 7,
        CurrentVersion = 7
    };

    WithdrawHandler.Handle(new Withdraw(accountId, 10m), stream);

    stream.EventsAppended.ShouldHaveSingleItem();
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/unit_testing_samples.cs#L112-L130' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_unit_testing_identity_and_version' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## `EventsAppended` vs `Events`

`EventsAppended` is the raw event bodies, in order, and is what nearly every assertion wants. `Events`
wraps them in `IEvent` envelopes for a handler or an assertion that reads the interface's own member:

<!-- snippet: sample_unit_testing_envelopes -->
<a id='snippet-sample_unit_testing_envelopes'></a>
```cs
[Fact]
public void the_envelopes_carry_the_event_type_naming()
{
    var stream = new StubEventStream<Account>(new Account { Balance = 100m });

    WithdrawHandler.Handle(new Withdraw(stream.Id, 30m), stream);

    // Events wraps EventsAppended in IEvent envelopes, for a handler or an assertion that reads
    // the interface's own member. Assert on EventsAppended unless you need the envelope.
    stream.Events.Single().Data.ShouldBe(new AccountDebited(30m));
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/unit_testing_samples.cs#L132-L144' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_unit_testing_envelopes' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
The envelopes carry the event type naming and **nothing a real store would only know at save time** —
no sequence, no version, no timestamp beyond the default. Assert on `EventsAppended` unless you
specifically need the envelope.
:::

## Multi-stream handlers

A handler taking two streams gets two stubs. Set each one's identity so the command's ids line up:

<!-- snippet: sample_unit_testing_multi_stream_handler -->
<a id='snippet-sample_unit_testing_multi_stream_handler'></a>
```cs
public record Transfer(Guid FromId, Guid ToId, decimal Amount);

public static class TransferHandler
{
    public static void Handle(Transfer command, IEventStream<Account> from, IEventStream<Account> to)
    {
        from.AppendOne(new AccountDebited(command.Amount));
        to.AppendOne(new AccountDebited(-command.Amount));
    }
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/unit_testing_samples.cs#L161-L172' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_unit_testing_multi_stream_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: sample_unit_testing_multi_stream -->
<a id='snippet-sample_unit_testing_multi_stream'></a>
```cs
[Fact]
public void two_streams_are_two_stubs()
{
    var from = new StubEventStream<Account>(new Account { Balance = 100m }) { Id = Guid.NewGuid() };
    var to = new StubEventStream<Account>(new Account { Balance = 0m }) { Id = Guid.NewGuid() };

    TransferHandler.Handle(new Transfer(from.Id, to.Id, 25m), from, to);

    from.EventsAppended.ShouldBe([new AccountDebited(25m)]);
    to.EventsAppended.ShouldBe([new AccountDebited(-25m)]);
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/unit_testing_samples.cs#L146-L158' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_unit_testing_multi_stream' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## See also

- [Integration Testing](/testing/integration) — a throwaway store per test, which is where anything
  about persistence belongs.
- [Event Sourcing Quickstart](/events/quickstart#_5-read-it-back) — `FetchForWriting`, which is how a
  handler gets an `IEventStream<T>` in production.
