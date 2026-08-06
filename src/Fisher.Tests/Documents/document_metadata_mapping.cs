using Fisher.Attributes;
using Fisher.Linq;
using Fisher.Linq.SoftDeletes;
using JasperFx;
using JasperFx.Metadata;

namespace Fisher.Tests.Documents;

/// <summary>
///     fisher#11 — the metadata columns Fisher writes, projected back onto document members.
/// </summary>
public class document_metadata_mapping : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("metadata_mapping");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.Schema.For<Rod>();
            options.Schema.For<Reel>();
            options.Schema.For<Creel>();
            options.Schema.For<Waders>().SoftDeleted()
                .Metadata(m =>
                {
                    m.IsSoftDeleted.MapTo(x => x.Retired);
                    m.DeletedAt.MapTo(x => x.RetiredAt);
                });
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    // ---- ISoftDeleted, the interface the issue was filed about ----

    [Fact]
    public async Task a_live_document_reads_back_undeleted()
    {
        var id = await StoreAsync(new Rod { Name = "Sage" });

        await using var session = _store.LightweightSession();
        var loaded = await session.LoadAsync<Rod>(id, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.Deleted.ShouldBeFalse();
        loaded.DeletedAt.ShouldBeNull();
    }

    /// <summary>
    ///     A soft-deleted document is filtered out of every ordinary read — see the load-SQL note in
    ///     CLAUDE.md — so a query carrying a soft-delete operator is the only way to observe the
    ///     populated members at all.
    /// </summary>
    [Fact]
    public async Task a_deleted_document_reads_back_with_both_members_populated()
    {
        var id = await StoreAsync(new Rod { Name = "Orvis" });
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        await DeleteAsync<Rod>(id);

        await using var session = _store.LightweightSession();
        var deleted = await session.Query<Rod>().IsDeleted()
            .ToListAsync(TestContext.Current.CancellationToken);

        var rod = deleted.ShouldHaveSingleItem();
        rod.Deleted.ShouldBeTrue();
        rod.DeletedAt.ShouldNotBeNull();
        rod.DeletedAt!.Value.ShouldBeGreaterThan(before);
        rod.DeletedAt!.Value.Offset.ShouldBe(TimeSpan.Zero);
    }

    /// <summary>
    ///     The query-only selector reads <c>data</c> at ordinal 0 and metadata from 1, where the
    ///     writeable flavors read <c>id</c> first. Both have to land on the same members, so this is the
    ///     ordinal contract being pinned rather than the feature being retested.
    /// </summary>
    [Fact]
    public async Task both_read_paths_populate_the_same_members()
    {
        var id = await StoreAsync(new Rod { Name = "Winston" });

        await using var session = _store.LightweightSession();

        var loaded = await session.LoadAsync<Rod>(id, TestContext.Current.CancellationToken);
        var queried = (await session.Query<Rod>().Where(x => x.Name == "Winston")
            .ToListAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem();

        loaded.ShouldNotBeNull();
        queried.Name.ShouldBe(loaded.Name);
        queried.Deleted.ShouldBe(loaded.Deleted);
        queried.DeletedAt.ShouldBe(loaded.DeletedAt);
    }

    /// <summary>
    ///     Storing a soft-deleted document undeletes it, so the members have to come back to their live
    ///     values rather than keeping what the deletion wrote.
    /// </summary>
    [Fact]
    public async Task undeleting_by_storing_again_clears_both_members()
    {
        var id = await StoreAsync(new Rod { Name = "Loomis" });
        await DeleteAsync<Rod>(id);

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Rod { Id = id, Name = "Loomis" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var loaded = await query.LoadAsync<Rod>(id, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.Deleted.ShouldBeFalse();
        loaded.DeletedAt.ShouldBeNull();
    }

    // ---- IVersioned ----

    [Fact]
    public void implementing_iversioned_turns_optimistic_concurrency_on()
        => _store.Options.Schema.MappingFor(typeof(Reel)).UseOptimisticConcurrency.ShouldBeTrue();

    [Fact]
    public async Task a_version_is_populated_on_read()
    {
        var id = await StoreAsync(new Reel { Name = "Hardy" });

        await using var session = _store.LightweightSession();
        var loaded = await session.LoadAsync<Reel>(id, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.Version.ShouldNotBe(Guid.Empty);
    }

    /// <summary>
    ///     The version column is dropped from the query-only projection when nothing reads it, and kept
    ///     when a member does — otherwise a query-only load would disagree with a lightweight one about
    ///     what the document holds.
    /// </summary>
    [Fact]
    public async Task a_mapped_version_survives_the_query_only_projection()
    {
        var id = await StoreAsync(new Reel { Name = "Abel" });

        await using var session = _store.LightweightSession();

        var loaded = await session.LoadAsync<Reel>(id, TestContext.Current.CancellationToken);
        var queried = (await session.Query<Reel>().Where(x => x.Name == "Abel")
            .ToListAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem();

        loaded.ShouldNotBeNull();
        queried.Version.ShouldBe(loaded.Version);
        queried.Version.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task a_version_changes_when_the_document_is_written_again()
    {
        var id = await StoreAsync(new Reel { Name = "Ross" });

        Guid first;
        await using (var session = _store.LightweightSession())
        {
            first = (await session.LoadAsync<Reel>(id, TestContext.Current.CancellationToken))!.Version;
        }

        await using (var session = _store.LightweightSession())
        {
            var reel = await session.LoadAsync<Reel>(id, TestContext.Current.CancellationToken);
            reel!.Name = "Ross Evolution";
            session.Store(reel);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var reloaded = await query.LoadAsync<Reel>(id, TestContext.Current.CancellationToken);

        reloaded!.Version.ShouldNotBe(first);
    }

    // ---- attributes ----

    [Fact]
    public async Task an_attribute_maps_last_modified()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        var id = await StoreAsync(new Creel { Name = "Wicker" });

        await using var session = _store.LightweightSession();
        var loaded = await session.LoadAsync<Creel>(id, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.UpdatedAt.ShouldBeGreaterThan(before);
        loaded.UpdatedAt.Offset.ShouldBe(TimeSpan.Zero);
    }

    // ---- the fluent DSL ----

    /// <summary>
    ///     A type with no metadata interface at all, mapped entirely through
    ///     <c>Schema.For&lt;T&gt;().Metadata(...)</c>.
    /// </summary>
    [Fact]
    public async Task the_fluent_dsl_maps_the_soft_delete_columns_onto_members_of_its_choosing()
    {
        var id = await StoreAsync(new Waders { Brand = "Simms" });
        await DeleteAsync<Waders>(id);

        await using var session = _store.LightweightSession();
        var deleted = (await session.Query<Waders>().IsDeleted()
            .ToListAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem();

        deleted.Retired.ShouldBeTrue();
        deleted.RetiredAt.ShouldNotBeNull();
    }

    // ---- configuration-time refusals ----

    [Fact]
    public void mapping_a_column_onto_a_member_of_the_wrong_type_is_refused()
    {
        var ex = Should.Throw<ArgumentException>(() => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.Schema.For<Rod>().Metadata(m => m.LastModified.MapTo(x => x.WhenCaught));
        }));

        ex.Message.ShouldContain("last_modified");
        ex.Message.ShouldContain("DateTime");
    }

    [Fact]
    public void mapping_a_column_onto_a_getter_only_member_is_refused()
    {
        var ex = Should.Throw<ArgumentException>(() => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.Schema.For<Rod>().Metadata(m => m.LastModified.MapTo(x => x.Registered));
        }));

        ex.Message.ShouldContain("setter");
    }

    [Fact]
    public void mapping_a_column_onto_a_nested_member_is_refused()
    {
        var ex = Should.Throw<ArgumentException>(() => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.Schema.For<Rod>().Metadata(m => m.LastModified.MapTo(x => x.Maker.Since));
        }));

        ex.Message.ShouldContain("of the document itself");
    }

    // ---- helpers ----

    private async Task<Guid> StoreAsync<T>(T document) where T : notnull
    {
        await using var session = _store.LightweightSession();
        session.Store(document);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return document switch
        {
            Rod rod => rod.Id,
            Reel reel => reel.Id,
            Creel creel => creel.Id,
            Waders waders => waders.Id,
            _ => throw new NotSupportedException()
        };
    }

    private async Task DeleteAsync<T>(Guid id) where T : notnull
    {
        await using var session = _store.LightweightSession();
        session.Delete<T>(id);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}

/// <summary>Soft deletion declared by the interface, so both members are mapped by convention.</summary>
public class Rod : ISoftDeleted
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime WhenCaught { get; set; }
    public DateTimeOffset Registered { get; } = DateTimeOffset.UtcNow;
    public Maker Maker { get; set; } = new();

    public bool Deleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public class Maker
{
    public DateTimeOffset Since { get; set; }
}

/// <summary>Optimistic concurrency asked for by the interface rather than by configuration.</summary>
public class Reel : IVersioned
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid Version { get; set; }
}

/// <summary>The one column no interface declares, reached by attribute.</summary>
public class Creel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    [LastModifiedMetadata] public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>No metadata interface at all — mapped entirely through the fluent DSL.</summary>
public class Waders
{
    public Guid Id { get; set; }
    public string Brand { get; set; } = string.Empty;
    public bool Retired { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
}
