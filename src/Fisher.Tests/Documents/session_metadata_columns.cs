using Fisher.Attributes;
using Fisher.Linq;
using Fisher.Linq.SoftDeletes;
using JasperFx;

namespace Fisher.Tests.Documents;

/// <summary>
///     The five opt-in metadata columns and <c>MetadataForAsync</c> — fisher#29.
/// </summary>
/// <remarks>
///     <para>
///         Two properties carry the weight. <c>created_at</c> must survive an update, which is the one
///         thing the write path's "assign every column from <c>excluded.*</c>" rule would break — and
///         it survives by not being in the write path at all. And a document and an event written in
///         one unit of work must carry the same correlation id, which is the whole point of the
///         feature rather than a nicety: an application that can answer "which request wrote this
///         event" could not answer it about the document written beside it.
///     </para>
///     <para>
///         Everything here is opt-in, so a type that asks for nothing gets exactly the table it had
///         before this existed. <c>an_unconfigured_type_gains_no_columns</c> is what says so.
///     </para>
/// </remarks>
public class session_metadata_columns : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("docmeta");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
            o.Events.EnableCorrelationId = true;
            o.Events.EnableCausationId = true;

            o.Schema.For<Plain>();
            o.Schema.For<Tracked>().Metadata(m =>
            {
                m.CreatedAt.Enabled = true;
                m.CorrelationId.Enabled = true;
                m.CausationId.Enabled = true;
                m.LastModifiedBy.Enabled = true;
                m.Headers.Enabled = true;
            });
            o.Schema.For<Annotated>();
            o.Schema.For<Perishable>().SoftDeleted();
            o.Schema.For<Scoped>().MultiTenanted().Metadata(m => m.TenantId.MapTo(x => x.Owner));
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<IReadOnlyList<string>> ColumnsOf(string table)
    {
        await using var session = _store.LightweightSession();

        return await session.AdvancedSql.QueryAsync<string>(
            $"select name from pragma_table_info('{table}')", Token);
    }

    // ---- the columns, and their absence ----

    /// <summary>
    ///     Opt-in means opt-in: a type configured before fisher#29 keeps the table it had.
    /// </summary>
    [Fact]
    public async Task an_unconfigured_type_gains_no_columns()
    {
        var columns = await ColumnsOf("fi_doc_plain");

        columns.ShouldBe(["id", "data", "dotnet_type", "last_modified"], ignoreOrder: true);
    }

    [Fact]
    public async Task an_enabled_type_gains_exactly_the_five()
    {
        var columns = await ColumnsOf("fi_doc_tracked");

        columns.ShouldBe([
            "id", "data", "dotnet_type", "last_modified",
            "created_at", "correlation_id", "causation_id", "last_modified_by", "headers"
        ], ignoreOrder: true);
    }

    /// <summary>
    ///     Marking a member both maps the column and creates it — a mapping onto a column that would
    ///     not exist is configuration that silently does nothing.
    /// </summary>
    [Fact]
    public async Task an_attribute_enables_the_column_it_maps()
    {
        var columns = await ColumnsOf("fi_doc_annotated");

        columns.ShouldContain("created_at");
        columns.ShouldContain("correlation_id");
        columns.ShouldContain("headers");
    }

    // ---- what the columns hold ----

    /// <summary>
    ///     <b>The property this feature exists for.</b> The values come off the session, which is the
    ///     same place <c>AppendPlanner.ApplySessionMetadata</c> reads them for events — so the document
    ///     and the event written beside it carry identical values, with no second source to drift.
    /// </summary>
    [Fact]
    public async Task a_document_and_an_event_in_one_unit_of_work_agree()
    {
        var streamId = Guid.NewGuid();
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.CorrelationId = "corr-1";
            session.CausationId = "cause-1";
            session.CurrentUserName = "frodo";

            session.Store(new Tracked { Id = id, Label = "one" });
            session.Events.StartStream(streamId, new Landed("Trout"));
            await session.SaveChangesAsync(Token);
        }

        await using var check = _store.LightweightSession();

        var metadata = await check.MetadataForAsync<Tracked>(id, Token);
        var events = await check.Events.FetchStreamAsync(streamId, token: Token);

        metadata!.CorrelationId.ShouldBe("corr-1");
        metadata.CausationId.ShouldBe("cause-1");
        metadata.LastModifiedBy.ShouldBe("frodo");

        events[0].CorrelationId.ShouldBe(metadata.CorrelationId);
        events[0].CausationId.ShouldBe(metadata.CausationId);
    }

    [Fact]
    public async Task headers_round_trip_as_json()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.SetHeader("origin", "shire");
            session.Store(new Tracked { Id = id, Label = "one" });
            await session.SaveChangesAsync(Token);
        }

        await using var check = _store.LightweightSession();
        var metadata = await check.MetadataForAsync<Tracked>(id, Token);

        metadata!.Headers!["origin"].ToString().ShouldBe("shire");
    }

    /// <summary>
    ///     <b>The one an update would break.</b> Fisher's upsert assigns every column in its write list
    ///     from <c>excluded.*</c>, so a <c>created_at</c> written client-side would move forward on
    ///     every save. It survives by being filled from the column's DEFAULT and never entering the
    ///     write list — the rule needs no exception.
    /// </summary>
    [Fact]
    public async Task created_at_survives_an_update_and_last_modified_does_not()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Tracked { Id = id, Label = "one" });
            await session.SaveChangesAsync(Token);
        }

        await using var first = _store.LightweightSession();
        var before = await first.MetadataForAsync<Tracked>(id, Token);

        // The stored timestamps are millisecond-precision, so two writes in the same millisecond would
        // agree with or without the fix. Planted rather than waited on.
        first.QueueSqlCommand(
            "update fi_doc_tracked set created_at = '2020-01-01T00:00:00.000Z', "
            + "last_modified = '2020-01-01T00:00:00.000Z'");
        await first.SaveChangesAsync(Token);

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Tracked { Id = id, Label = "two" });
            await session.SaveChangesAsync(Token);
        }

        await using var check = _store.LightweightSession();
        var after = await check.MetadataForAsync<Tracked>(id, Token);

        after!.CreatedAt!.Value.Year.ShouldBe(2020);
        after.LastModified.Year.ShouldBe(before!.LastModified.Year);
        after.LastModified.ShouldBeGreaterThan(after.CreatedAt!.Value);
    }

    [Fact]
    public async Task the_columns_are_projected_onto_mapped_members()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.CorrelationId = "corr-2";
            session.SetHeader("origin", "bree");
            session.Store(new Annotated { Id = id, Label = "one" });
            await session.SaveChangesAsync(Token);
        }

        await using var check = _store.LightweightSession();
        var loaded = (await check.LoadAsync<Annotated>(id, Token))!;

        loaded.Correlation.ShouldBe("corr-2");
        loaded.Created.ShouldBeGreaterThan(DateTimeOffset.UnixEpoch);
        loaded.Bag!["origin"].ToString().ShouldBe("bree");
    }

    [Fact]
    public async Task a_tenant_id_is_projected_onto_a_member()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession("shire"))
        {
            session.Store(new Scoped { Id = id, Label = "one" });
            await session.SaveChangesAsync(Token);
        }

        await using var check = _store.LightweightSession("shire");
        (await check.LoadAsync<Scoped>(id, Token))!.Owner.ShouldBe("shire");
    }

    // ---- MetadataForAsync ----

    [Fact]
    public async Task metadata_for_a_present_and_an_absent_document()
    {
        var id = Guid.NewGuid();
        var document = new Plain { Id = id, Label = "one" };

        await using (var session = _store.LightweightSession())
        {
            session.Store(document);
            await session.SaveChangesAsync(Token);
        }

        await using var check = _store.LightweightSession();

        var metadata = await check.MetadataForAsync<Plain>(id, Token);

        metadata.ShouldNotBeNull();
        metadata.Id.ShouldBe(id);
        metadata.TenantId.ShouldBe(StorageConstants.DefaultTenantId);
        metadata.DotNetType.ShouldNotBeNull();
        metadata.LastModified.ShouldBeGreaterThan(DateTimeOffset.UnixEpoch);

        // Nothing this type did not ask for.
        metadata.CreatedAt.ShouldBeNull();
        metadata.CorrelationId.ShouldBeNull();
        metadata.Version.ShouldBeNull();
        metadata.Deleted.ShouldBeFalse();

        // The by-document overload finds the same row, and an unknown id is null rather than an error.
        (await check.MetadataForAsync(document, Token))!.Id.ShouldBe(id);
        (await check.MetadataForAsync<Plain>(Guid.NewGuid(), Token)).ShouldBeNull();
    }

    /// <summary>
    ///     A soft-deleted document has metadata, and this is the only way to reach it — every ordinary
    ///     load filters the row out, which is exactly why the read does not carry that filter.
    /// </summary>
    [Fact]
    public async Task metadata_for_a_soft_deleted_document()
    {
        var id = Guid.NewGuid();
        var document = new Perishable { Id = id, Label = "milk" };

        await using (var session = _store.LightweightSession())
        {
            session.Store(document);
            await session.SaveChangesAsync(Token);

            session.Delete(document);
            await session.SaveChangesAsync(Token);
        }

        await using var check = _store.LightweightSession();

        (await check.LoadAsync<Perishable>(id, Token)).ShouldBeNull();

        var metadata = await check.MetadataForAsync<Perishable>(id, Token);

        metadata!.Deleted.ShouldBeTrue();
        metadata.DeletedAt.ShouldNotBeNull();
    }

    /// <summary>
    ///     A conjoined table keys on <c>(tenant_id, id)</c>, so the same id under another tenant is a
    ///     different document — and metadata is scoped the way every other read is.
    /// </summary>
    [Fact]
    public async Task metadata_is_scoped_to_the_tenant()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession("shire"))
        {
            session.Store(new Scoped { Id = id, Label = "one" });
            await session.SaveChangesAsync(Token);
        }

        await using var shire = _store.LightweightSession("shire");
        await using var bree = _store.LightweightSession("bree");

        (await shire.MetadataForAsync<Scoped>(id, Token))!.TenantId.ShouldBe("shire");
        (await bree.MetadataForAsync<Scoped>(id, Token)).ShouldBeNull();
    }

    // ---- what is refused ----

    [Fact]
    public void enabling_a_column_whose_existence_is_decided_elsewhere_is_refused()
    {
        var exception = Should.Throw<InvalidOperationException>(() => DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.Schema.For<Plain>().Metadata(m => m.LastModified.GetType());
            o.Schema.For<Plain>().Mapping.Metadata.LastModified.Enable();
        }));

        exception.Message.ShouldContain("not optional");
    }

    [Fact]
    public void turning_an_enabled_column_back_off_is_refused()
    {
        var exception = Should.Throw<InvalidOperationException>(() => DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.Schema.For<Plain>().Metadata(m =>
            {
                m.CreatedAt.Enabled = true;
                m.CreatedAt.Enabled = false;
            });
        }));

        exception.Message.ShouldContain("migration");
    }

    public record Landed(string Species);

    public class Plain
    {
        public Guid Id { get; set; }
        public string Label { get; set; } = "";
    }

    public class Tracked
    {
        public Guid Id { get; set; }
        public string Label { get; set; } = "";
    }

    public class Annotated
    {
        public Guid Id { get; set; }
        public string Label { get; set; } = "";

        [CreatedAtMetadata] public DateTimeOffset Created { get; set; }
        [CorrelationIdMetadata] public string? Correlation { get; set; }
        [HeadersMetadata] public Dictionary<string, object>? Bag { get; set; }
    }

    public class Perishable
    {
        public Guid Id { get; set; }
        public string Label { get; set; } = "";
    }

    public class Scoped
    {
        public Guid Id { get; set; }
        public string Label { get; set; } = "";
        public string? Owner { get; set; }
    }
}
