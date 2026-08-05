using JasperFx.Events;

namespace Fisher.Tests.Events;

/// <summary>
///     Fisher's default answer for the rebuild concurrency cap.
/// </summary>
/// <remarks>
///     <c>RebuildConcurrencyCapCompliance</c> covers the shared contract — explicit wins, non-positive
///     disables, otherwise <c>max(1, poolSize / 8)</c> — but only ever with an explicitly set pool
///     ceiling. What it never exercises is the <em>default</em>, and Fisher's default is a deliberate
///     SQLite decision rather than an inherited one: writers serialize at the file level, so more than
///     one concurrent rebuild cell contends for the same write lock instead of parallelising.
/// </remarks>
public class rebuild_concurrency_cap
{
    private static IEventStore StoreWith(Action<StoreOptions> configure)
    {
        var options = new StoreOptions { ConnectionString = "Data Source=:memory:" };
        configure(options);
        return new DocumentStore(options);
    }

    /// <summary>
    ///     The point of <see cref="StoreOptions.MaxPoolSize" />'s default of 8: it derives to one.
    /// </summary>
    [Fact]
    public void the_default_cap_is_one_writer()
    {
        StoreWith(_ => { }).MaxConcurrentRebuildsPerDatabase.ShouldBe(1);
    }

    [Fact]
    public void an_explicit_setting_wins_over_the_derived_default()
    {
        StoreWith(options => options.DaemonSettings.MaxConcurrentRebuildsPerDatabase = 4)
            .MaxConcurrentRebuildsPerDatabase.ShouldBe(4);
    }

    /// <summary>
    ///     Null means "unbounded" to JasperFx, so a non-positive setting removes the cap rather than
    ///     pinning it at zero — which would stall every rebuild.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void a_non_positive_setting_disables_the_cap(int configured)
    {
        StoreWith(options => options.DaemonSettings.MaxConcurrentRebuildsPerDatabase = configured)
            .MaxConcurrentRebuildsPerDatabase.ShouldBeNull();
    }

    [Fact]
    public void a_raised_ceiling_derives_an_eighth_of_itself()
    {
        StoreWith(options => options.MaxPoolSize = 64).MaxConcurrentRebuildsPerDatabase.ShouldBe(8);
    }

    /// <summary>
    ///     The floor matters: integer division of a small ceiling reaches zero, and a cap of zero would
    ///     stall a rebuild rather than run it slowly.
    /// </summary>
    [Fact]
    public void a_tiny_ceiling_still_floors_at_one()
    {
        StoreWith(options => options.MaxPoolSize = 1).MaxConcurrentRebuildsPerDatabase.ShouldBe(1);
    }

    [Fact]
    public async Task the_usage_descriptor_carries_the_effective_cap()
    {
        var store = StoreWith(options => options.DaemonSettings.MaxConcurrentRebuildsPerDatabase = 6);

        var usage = await store.TryCreateUsage(TestContext.Current.CancellationToken);

        usage.ShouldNotBeNull();
        usage.MaxConcurrentRebuildsPerDatabase.ShouldBe(6);
    }
}
