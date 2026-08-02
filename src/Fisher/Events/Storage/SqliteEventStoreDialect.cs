using JasperFx.Events;
using Weasel.Storage;

namespace Fisher.Events.Storage;

// TODO(task 3): real implementation.
internal sealed class SqliteEventStoreDialect : IEventStoreSqlDialect
{
    public RichEventStorageDescriptor BuildRichDescriptor(EventRegistry graph, IStorageSerializer serializer)
        => throw new NotSupportedException();

    public QuickEventStorageDescriptor BuildQuickDescriptor(EventRegistry graph, IStorageSerializer serializer)
        => throw new NotImplementedException();

    public QuickWithServerTimestampsEventStorageDescriptor BuildQuickWithServerTimestampsDescriptor(
        EventRegistry graph, IStorageSerializer serializer)
        => throw new NotSupportedException();
}
