using System.Collections.Concurrent;
using Weasel.Core.Sequences;
using Weasel.Sqlite;

namespace Fisher.Storage.Sequences;

/// <summary>
///     The store's <see cref="ISequenceSource" />: one cached <see cref="HiloSequence" /> per logical
///     sequence name, which is what the shared <c>HiloIntIdentification</c> /
///     <c>HiloLongIdentification</c> strategies resolve through when assigning a numeric id.
/// </summary>
/// <remarks>
///     Keyed by sequence <em>name</em> rather than by document type, so two document types configured
///     onto the same <c>SequenceName</c> genuinely share one allocation instead of each holding its
///     own client-side lo range over the same row — which would hand out duplicate ids.
/// </remarks>
internal sealed class SequenceFactory : ISequenceSource
{
    private readonly SqliteDataSource _dataSource;
    private readonly StoreOptions _options;
    private readonly ConcurrentDictionary<string, ISequence> _sequences = new();

    public SequenceFactory(StoreOptions options, SqliteDataSource dataSource)
    {
        _options = options;
        _dataSource = dataSource;
    }

    public ISequence SequenceFor(Type documentType)
    {
        var settings = _options.Schema.MappingFor(documentType).HiloSettings ?? _options.HiloSequenceDefaults;

        return Hilo(documentType, settings);
    }

    public ISequence Hilo(Type documentType, IReadOnlyHiloSettings settings)
    {
        var name = string.IsNullOrEmpty(settings.SequenceName) ? documentType.Name : settings.SequenceName!;

        return _sequences.GetOrAdd(name, sequenceName => new HiloSequence(
            _dataSource,
            _options.DatabaseSchemaName,
            sequenceName,
            settings,
            _options.ResiliencePipeline,
            _options.AutoCreateSchemaObjects));
    }
}
