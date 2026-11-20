using Fisher.Projections;
using JasperFx.Events;

namespace Fisher.Tests.Documentation;

/*
 * The compiled source behind docs/events/projections/backfills.md.
 *
 * See "Documentation samples" in CLAUDE.md. The behaviour is pinned by BackfillCompliance
 * (fisher_event_store_compliance.cs) and Projections/projection_backfills.cs.
 */

public record WalletOpened;

public record WalletDeposited(decimal Amount);

public class Wallet
{
    public Guid Id { get; set; }
    public int Deposits { get; set; }

    // The new member
    public decimal Balance { get; set; }
}

#region sample_declare_a_backfill
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
#endregion

public static class backfill_samples
{
    public static void register_a_backfill(StoreOptions options)
    {
        #region sample_register_a_backfill
        options.Projections.Add(new WalletProjection(), JasperFx.Events.Projections.ProjectionLifecycle.Async);

        // Wolverine's integration sets this. Without it the store refuses the Backfill(...) declaration.
        options.EventGraph.HasJobHost = true;
        #endregion
    }

    public static async Task run_a_backfill(IEventStore store, CancellationToken token)
    {
        #region sample_run_a_backfill
        var shard = store.RegisteredShardNames().Single(x => x.Name == "Wallet");

        foreach (var database in await store.AllDatabases())
        {
            // The first request stores the ceiling, unless the live projection's start already did.
            // A later one resumes after the stored cursor.
            var run = await BackfillRun.FromMarksAsync(database, shard, "balance", null, token);
            var totals = await store.RunBackfillAsync(run, token);
        }
        #endregion
    }
}
