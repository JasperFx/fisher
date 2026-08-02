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

    public EventStoreFeatureSchema(EventGraph events)
        : base("EventStore", new SqliteMigrator())
    {
        _events = events;
    }

    public override Type StorageType => typeof(EventStoreFeatureSchema);

    protected override IEnumerable<ISchemaObject> schemaObjects()
    {
        // Streams before events: Fisher declares no foreign key between them (see EventsTable), but
        // creation order still matters for anything that reads the schema back in dependency order.
        yield return _events.BuildStreamsTable();
        yield return _events.BuildEventsTable();
        yield return _events.BuildEventProgressionTable();
    }
}
