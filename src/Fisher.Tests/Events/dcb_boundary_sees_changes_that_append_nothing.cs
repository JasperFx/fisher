using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Tags;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Events;

public record HarborTag(Guid Value);

public record PierTag(Guid Value);

public record VesselMoored(string Name);

public record VesselInspected(string Name);

[BoundaryAggregate]
public class HarborLog
{
    public List<string> Vessels { get; } = [];

    public void Apply(VesselMoored e) => Vessels.Add(e.Name);

    public void Apply(VesselInspected e) => Vessels.Add("inspected " + e.Name);
}

/// <summary>
///     fisher#635, the port of marten#6035 and marten#6078. A DCB boundary only asked whether a matching event had
///     been appended past the last sequence it read, so a tombstone, an archive, an un-archive or an
///     <c>AssignTagWhere</c> that changed what it read let its save through. Each of those now moves the version of
///     every tag on the events it touches (<c>fi_dcb_tag_version</c>), and the save compares the versions it
///     captured.
/// </summary>
public class dcb_boundary_sees_changes_that_append_nothing : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("dcb-tag-versions");
    private readonly HarborTag _harbor = new(Guid.NewGuid());
    private readonly HarborTag _otherHarbor = new(Guid.NewGuid());
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = BuildStore();
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private DocumentStore BuildStore(Action<StoreOptions>? configure = null) => DocumentStore.For(options =>
    {
        options.ConnectionString = _database.ConnectionString;
        options.AutoCreateSchemaObjects = AutoCreate.All;
        options.Events.AddEventType<VesselMoored>();
        options.Events.AddEventType<VesselInspected>();
        options.Events.RegisterTagType<HarborTag>("harbor");
        options.Events.RegisterTagType<PierTag>("pier");
        configure?.Invoke(options);
    });

    private async Task<Guid> MoorAsync(HarborTag? harbor, string name, string? tenantId = null, object? @event = null)
    {
        var streamId = Guid.NewGuid();

        await using var session = tenantId is null ? _store.LightweightSession() : _store.LightweightSession(tenantId);
        var built = session.Events.BuildEvent(@event ?? new VesselMoored(name));
        if (harbor is not null) built.WithTag(harbor);
        session.Events.StartStream(streamId, built);
        await session.SaveChangesAsync(Token);

        return streamId;
    }

    private async Task ChangeAsync(Action<IDocumentSession> change, string? tenantId = null)
    {
        await using var session = tenantId is null ? _store.LightweightSession() : _store.LightweightSession(tenantId);
        change(session);
        await session.SaveChangesAsync(Token);
    }

    /// <summary>Read the boundary, let <paramref name="meanwhile" /> commit, then append through the boundary and save.</summary>
    private async Task SaveThroughBoundaryAsync(EventTagQuery query, Func<Task> meanwhile, string? tenantId = null)
    {
        await using var session = tenantId is null ? _store.LightweightSession() : _store.LightweightSession(tenantId);
        var boundary = await session.Events.FetchForWritingByTags<HarborLog>(query, Token);

        await meanwhile();

        var @event = session.Events.BuildEvent(new VesselMoored("Mine"));
        @event.WithTag(_harbor);
        boundary.AppendOne(@event);

        await session.SaveChangesAsync(Token);
    }

    private EventTagQuery HarborQuery => new EventTagQuery().Or<HarborTag>(_harbor);

    [Fact]
    public async Task a_tombstone_of_a_stream_the_boundary_read_fails_its_save()
    {
        var streamId = await MoorAsync(_harbor, "Seen");

        await Should.ThrowAsync<DcbConcurrencyException>(() => SaveThroughBoundaryAsync(HarborQuery,
            () => ChangeAsync(s => s.Events.TombstoneStream(streamId))));
    }

    /// <summary>
    ///     The tag is still carried by another event after the tombstone, so its version row stays and has moved,
    ///     rather than being deleted.
    /// </summary>
    [Fact]
    public async Task a_tombstone_fails_the_save_while_other_events_still_carry_the_tag()
    {
        var streamId = await MoorAsync(_harbor, "Seen");
        await MoorAsync(_harbor, "Stays");

        await Should.ThrowAsync<DcbConcurrencyException>(() => SaveThroughBoundaryAsync(HarborQuery,
            () => ChangeAsync(s => s.Events.TombstoneStream(streamId))));
    }

    [Fact]
    public async Task an_archive_of_a_stream_the_boundary_read_fails_its_save()
    {
        var streamId = await MoorAsync(_harbor, "Seen");

        await Should.ThrowAsync<DcbConcurrencyException>(() => SaveThroughBoundaryAsync(HarborQuery,
            () => ChangeAsync(s => s.Events.ArchiveStream(streamId))));
    }

    [Fact]
    public async Task an_un_archive_that_brings_events_back_fails_the_save()
    {
        var streamId = await MoorAsync(_harbor, "Retired");
        await ChangeAsync(s => s.Events.ArchiveStream(streamId));

        await Should.ThrowAsync<DcbConcurrencyException>(() => SaveThroughBoundaryAsync(HarborQuery,
            () => ChangeAsync(s => s.Events.UnArchiveStream(streamId))));
    }

    [Fact]
    public async Task assign_tag_where_onto_an_older_event_fails_the_save()
    {
        await MoorAsync(_harbor, "Seen");
        await MoorAsync(null, "Untagged");

        await Should.ThrowAsync<DcbConcurrencyException>(() => SaveThroughBoundaryAsync(HarborQuery,
            () => ChangeAsync(s => s.Events.AssignTagWhere(
                x => x.EventTypeName == _store.Options.EventGraph.EventMappingFor(typeof(VesselMoored)).EventTypeName,
                _harbor))));
    }

    [Fact]
    public async Task a_boundary_over_unrelated_tags_still_saves()
    {
        await MoorAsync(_harbor, "Seen");
        var tombstoned = await MoorAsync(_otherHarbor, "Tombstoned");
        var archived = await MoorAsync(_otherHarbor, "Archived");
        var unarchived = await MoorAsync(_otherHarbor, "Unarchived");
        await ChangeAsync(s => s.Events.ArchiveStream(unarchived));
        await MoorAsync(null, "Untagged");

        await SaveThroughBoundaryAsync(HarborQuery, async () =>
        {
            await ChangeAsync(s => s.Events.TombstoneStream(tombstoned));
            await ChangeAsync(s => s.Events.ArchiveStream(archived));
            await ChangeAsync(s => s.Events.UnArchiveStream(unarchived));
            await ChangeAsync(s => s.Events.AssignTagWhere(x => x.Sequence > 0, new PierTag(Guid.NewGuid())));
        });
    }

    /// <summary>
    ///     A condition that names an event type is checked against that type's own version, as on Marten 10, so
    ///     removing events of another type that carry the tag is not a conflict.
    /// </summary>
    [Fact]
    public async Task a_condition_on_one_event_type_ignores_a_tombstone_of_another_type()
    {
        await MoorAsync(_harbor, "Seen");
        var inspection = await MoorAsync(_harbor, "Inspected", @event: new VesselInspected("Inspected"));

        var query = new EventTagQuery().Or<VesselMoored, HarborTag>(_harbor);

        await SaveThroughBoundaryAsync(query, () => ChangeAsync(s => s.Events.TombstoneStream(inspection)));
    }

    [Fact]
    public async Task a_condition_on_one_event_type_sees_a_tombstone_of_that_type()
    {
        var streamId = await MoorAsync(_harbor, "Seen");
        await MoorAsync(_harbor, "Inspected", @event: new VesselInspected("Inspected"));

        var query = new EventTagQuery().Or<VesselMoored, HarborTag>(_harbor);

        await Should.ThrowAsync<DcbConcurrencyException>(() => SaveThroughBoundaryAsync(query,
            () => ChangeAsync(s => s.Events.TombstoneStream(streamId))));
    }

    [Fact]
    public async Task a_change_made_before_the_boundary_read_is_no_conflict()
    {
        var tombstoned = await MoorAsync(_harbor, "Tombstoned");
        var archived = await MoorAsync(_harbor, "Archived");
        await MoorAsync(_harbor, "Seen");
        await ChangeAsync(s => s.Events.TombstoneStream(tombstoned));
        await ChangeAsync(s => s.Events.ArchiveStream(archived));

        await SaveThroughBoundaryAsync(HarborQuery, () => Task.CompletedTask);
    }

    /// <summary>A tag value can be personal data, so it does not outlive the last event carrying it (marten#6035).</summary>
    [Fact]
    public async Task a_tombstone_forgets_the_version_of_a_tag_no_event_carries_any_more()
    {
        var gone = await MoorAsync(_harbor, "Gone");
        await MoorAsync(_otherHarbor, "Stays");
        var alsoStays = await MoorAsync(_otherHarbor, "Also stays");

        // Something to move the other harbor's row, so it exists.
        await ChangeAsync(s => s.Events.ArchiveStream(alsoStays));
        await ChangeAsync(s => s.Events.TombstoneStream(gone));

        (await VersionRowsForAsync(_harbor.Value.ToString())).ShouldBe(0);
        (await VersionRowsForAsync(_otherHarbor.Value.ToString())).ShouldBeGreaterThan(0);
    }

    /// <summary>
    ///     A store that predates fisher#635 has tagged events and no version tables. The migration adds the tables,
    ///     no row is back-filled, and a boundary over the old events still sees a tombstone.
    /// </summary>
    [Fact]
    public async Task a_store_from_before_the_version_tables_gets_them_and_is_still_guarded()
    {
        var streamId = await MoorAsync(_harbor, "From before");

        await ExecuteAsync("drop table fi_dcb_tag_version; drop table fi_dcb_tag_version_sequence;");

        await _store.DisposeAsync();
        _store = BuildStore();
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        (await ExecuteScalarAsync("select count(*) from sqlite_master where name = 'fi_dcb_tag_version'")).ShouldBe(1);

        await Should.ThrowAsync<DcbConcurrencyException>(() => SaveThroughBoundaryAsync(HarborQuery,
            () => ChangeAsync(s => s.Events.TombstoneStream(streamId))));
    }

    [Fact]
    public async Task under_conjoined_tenancy_another_tenants_tombstone_is_no_conflict()
    {
        await _store.DisposeAsync();
        _store = BuildStore(o => o.Events.TenancyStyle = TenancyStyle.Conjoined);
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await MoorAsync(_harbor, "Red's", "red");
        var blues = await MoorAsync(_harbor, "Blue's", "blue");

        await SaveThroughBoundaryAsync(HarborQuery,
            () => ChangeAsync(s => s.Events.TombstoneStream(blues), "blue"), "red");

        var reds = await MoorAsync(_harbor, "Red's second", "red");

        await Should.ThrowAsync<DcbConcurrencyException>(() => SaveThroughBoundaryAsync(HarborQuery,
            () => ChangeAsync(s => s.Events.TombstoneStream(reds), "red"), "red"));
    }

    /// <summary>Erasing a tenant takes its tag values with it, versions included.</summary>
    [Fact]
    public async Task deleting_a_tenants_data_deletes_its_tag_versions_but_keeps_the_counter()
    {
        await _store.DisposeAsync();
        _store = BuildStore(o => o.Events.TenancyStyle = TenancyStyle.Conjoined);
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var reds = await MoorAsync(_harbor, "Red's", "red");
        var blues = await MoorAsync(_otherHarbor, "Blue's", "blue");
        await ChangeAsync(s => s.Events.ArchiveStream(reds), "red");
        await ChangeAsync(s => s.Events.ArchiveStream(blues), "blue");

        await _store.Advanced.DeleteAllTenantDataAsync("red", Token);

        (await VersionRowsForAsync(_harbor.Value.ToString())).ShouldBe(0);
        (await VersionRowsForAsync(_otherHarbor.Value.ToString())).ShouldBeGreaterThan(0);
        (await ExecuteScalarAsync("select value from fi_dcb_tag_version_sequence where id = 1")).ShouldBe(2);
    }

    private Task<long> VersionRowsForAsync(string value)
        => ExecuteScalarAsync($"select count(*) from fi_dcb_tag_version where tag_value = '{value}'");

    private async Task<long> ExecuteScalarAsync(string sql)
    {
        await using var connection = await _store.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(Token));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await _store.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }
}
