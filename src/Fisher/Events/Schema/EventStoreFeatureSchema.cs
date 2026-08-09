using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Sqlite;

namespace Fisher.Events.Schema;

/// <summary>
///     Groups the event store tables (<c>fi_streams</c>, <c>fi_events</c>,
///     <c>fi_event_progression</c>) into a single Weasel feature schema so they migrate together.
/// </summary>
internal class EventStoreFeatureSchema : FeatureSchemaBase
{
    private readonly EventGraph _events;
    private readonly IReadOnlyList<JasperFx.Events.NaturalKeyDefinition> _naturalKeys;

    public EventStoreFeatureSchema(EventGraph events,
        IReadOnlyList<JasperFx.Events.NaturalKeyDefinition> naturalKeys)
        : base("EventStore", new SqliteMigrator())
    {
        _events = events;
        _naturalKeys = naturalKeys;
    }

    public override Type StorageType => typeof(EventStoreFeatureSchema);

    protected override IEnumerable<ISchemaObject> schemaObjects()
    {
        // Streams before events: Fisher declares no foreign key between them (see EventsTable), but
        // creation order still matters for anything that reads the schema back in dependency order.
        yield return _events.BuildStreamsTable();
        yield return _events.BuildEventsTable();
        yield return _events.BuildEventProgressionTable();

        // Independent of the others — it deliberately carries no foreign key to fi_events, so a dead
        // letter outlives the event it describes. See DeadLetterTable.
        yield return _events.BuildDeadLetterTable();

        // Tag tables last: each carries a real foreign key to fi_events(seq_id), so the referenced
        // table has to exist first.
        foreach (var tagTable in _events.BuildTagTables())
        {
            yield return tagTable;
        }

        // The natural key lookups (fisher#40). No foreign key to fi_streams, so their position here is
        // presentational rather than an ordering constraint — see NaturalKeyTable for why not.
        foreach (var naturalKeyTable in _events.BuildNaturalKeyTables(_naturalKeys))
        {
            yield return naturalKeyTable;
        }
    }
}
