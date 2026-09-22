using JasperFx.Events;
using Shouldly;

namespace Fisher.Tests.Documentation;

/*
 * The compiled source behind docs/testing/unit-testing-event-handlers.md.
 *
 * See "Documentation samples" in CLAUDE.md: every sample a reader would copy lives in a
 * #region here and is pulled into the markdown by mdsnippets, so a sample that stops compiling
 * fails the build rather than going stale in a page nobody rebuilds.
 *
 * These are the only samples in the repository that need no store at all — which is the page's
 * whole subject.
 */

#region sample_unit_testing_account
public record AccountDebited(decimal Amount);

public record AccountOverdrawn(decimal Balance);

public class Account
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }
    public bool IsFrozen { get; set; }

    public void Apply(AccountDebited e) => Balance -= e.Amount;
}
#endregion

#region sample_unit_testing_handler
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
#endregion

public class unit_testing_samples
{
    #region sample_unit_testing_the_happy_path
    [Fact]
    public void a_withdrawal_within_the_balance_debits_and_nothing_else()
    {
        var stream = new StubEventStream<Account>(new Account { Balance = 100m });

        WithdrawHandler.Handle(new Withdraw(stream.Id, 30m), stream);

        // The decision, stated as the events it produced — not as "AppendOne was called".
        stream.EventsAppended.ShouldBe([new AccountDebited(30m)]);
    }
    #endregion

    #region sample_unit_testing_the_branch
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
    #endregion

    #region sample_unit_testing_nothing_appended
    [Fact]
    public void a_frozen_account_is_refused_and_appends_nothing()
    {
        var stream = new StubEventStream<Account>(new Account { Balance = 100m, IsFrozen = true });

        Should.Throw<InvalidOperationException>(
            () => WithdrawHandler.Handle(new Withdraw(stream.Id, 30m), stream));

        // The half that is easy to leave out, and the one a mock will not give you for free.
        stream.EventsAppended.ShouldBeEmpty();
    }
    #endregion

    #region sample_unit_testing_missing_stream
    [Fact]
    public void a_stream_that_does_not_exist_yet_has_a_null_aggregate()
    {
        var stream = new StubEventStream<Account>(null);

        Should.Throw<InvalidOperationException>(
            () => WithdrawHandler.Handle(new Withdraw(stream.Id, 30m), stream));
    }
    #endregion

    #region sample_unit_testing_identity_and_version
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
    #endregion

    #region sample_unit_testing_envelopes
    [Fact]
    public void the_envelopes_carry_the_event_type_naming()
    {
        var stream = new StubEventStream<Account>(new Account { Balance = 100m });

        WithdrawHandler.Handle(new Withdraw(stream.Id, 30m), stream);

        // Events wraps EventsAppended in IEvent envelopes, for a handler or an assertion that reads
        // the interface's own member. Assert on EventsAppended unless you need the envelope.
        stream.Events.Single().Data.ShouldBe(new AccountDebited(30m));
    }
    #endregion

    #region sample_unit_testing_multi_stream
    [Fact]
    public void two_streams_are_two_stubs()
    {
        var from = new StubEventStream<Account>(new Account { Balance = 100m }) { Id = Guid.NewGuid() };
        var to = new StubEventStream<Account>(new Account { Balance = 0m }) { Id = Guid.NewGuid() };

        TransferHandler.Handle(new Transfer(from.Id, to.Id, 25m), from, to);

        from.EventsAppended.ShouldBe([new AccountDebited(25m)]);
        to.EventsAppended.ShouldBe([new AccountDebited(-25m)]);
    }
    #endregion
}

#region sample_unit_testing_multi_stream_handler
public record Transfer(Guid FromId, Guid ToId, decimal Amount);

public static class TransferHandler
{
    public static void Handle(Transfer command, IEventStream<Account> from, IEventStream<Account> to)
    {
        from.AppendOne(new AccountDebited(command.Amount));
        to.AppendOne(new AccountDebited(-command.Amount));
    }
}
#endregion
