using System.Globalization;
using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Schema;

public class IndexedItem
{
    public Guid Id { get; set; }
    public bool IsFlagged { get; set; }
    public string Name { get; set; } = "";
}

public record InvoiceIssued(string Number);

/// <summary>
///     The parity check for marten#6103: Marten lowered two identifiers with the current culture, so under tr-TR an
///     uppercase <c>I</c> became a dotless <c>ı</c> and a duplicated field's upsert named a column that does not
///     exist. JasperFx's <c>ToTableAlias</c> had the same fault (jasperfx#1246).
/// </summary>
/// <remarks>
///     Fisher builds its identifiers with its own helpers, all of which lower with <c>ToLowerInvariant</c>:
///     duplicated-field columns (<c>DuplicatedField.ToSnakeCase</c>), document aliases
///     (<c>DocumentMapping</c>), event type names (<c>EventGraph.ToEventTypeName</c>) and flat-table columns. It does
///     not call <c>ToTableAlias</c>, and its tracing never lowers an operation role. This pins the first three under
///     tr-TR, end to end, so the answer is not only read from source.
/// </remarks>
public class identifiers_ignore_the_current_culture
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task identifiers_are_the_same_under_a_turkish_culture()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");

        try
        {
            // Not vacuous: this runtime really lowers I to a dotless i under tr-TR.
            "I".ToLower().ShouldBe("ı");

            using var database = TemporaryDatabase.Create("identifiers-culture");
            await using var store = DocumentStore.For(options =>
            {
                options.ConnectionString = database.ConnectionString;
                options.AutoCreateSchemaObjects = AutoCreate.All;
                options.Schema.For<IndexedItem>().Duplicate(x => x.IsFlagged);
                options.Events.AddEventType(typeof(InvoiceIssued));
            });

            var mapping = store.Options.Schema.MappingFor(typeof(IndexedItem));
            mapping.Alias.ShouldBe("indexeditem");
            mapping.DuplicatedFields.ShouldHaveSingleItem().ColumnName.ShouldBe("is_flagged");
            store.Options.EventGraph.EventMappingFor(typeof(InvoiceIssued)).EventTypeName.ShouldBe("invoice_issued");

            await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

            var flagged = new IndexedItem { Id = Guid.NewGuid(), IsFlagged = true, Name = "flagged" };
            var streamId = Guid.NewGuid();
            await using (var session = store.LightweightSession())
            {
                session.Store(flagged, new IndexedItem { Id = Guid.NewGuid(), Name = "plain" });
                session.Events.StartStream(streamId, new InvoiceIssued("INV-1"));
                await session.SaveChangesAsync(Token);
            }

            // And again, through the update path of the same upsert
            flagged.Name = "still flagged";
            await using (var session = store.LightweightSession())
            {
                session.Store(flagged);
                await session.SaveChangesAsync(Token);
            }

            await using var query = store.LightweightSession();
            (await query.Query<IndexedItem>().Where(x => x.IsFlagged).ToListAsync(Token))
                .ShouldHaveSingleItem().Name.ShouldBe("still flagged");
            (await query.Events.FetchStreamAsync(streamId, token: Token)).Single().Data
                .ShouldBe(new InvoiceIssued("INV-1"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
