using System.Data.Common;
using JasperFx.Events.Tags;
using Weasel.Core;
using Weasel.Core.SqlGeneration;
using Weasel.Storage;

namespace Fisher.Events.Storage;

/// <summary>
///     Retroactively tags every already-persisted event matching a predicate.
/// </summary>
/// <remarks>
///     <para>
///         One <c>insert … select</c> rather than a read followed by a loop of inserts: the events to
///         tag are identified by a predicate the database can evaluate, so shipping the rows to the
///         client only to send their ids straight back would be pure round trips.
///     </para>
///     <para>
///         <c>on conflict do nothing</c> is what makes the operation idempotent, leaning on the tag
///         table's composite primary key. Running the same <c>AssignTagWhere</c> twice is a no-op the
///         second time, which <c>assign_tag_where_is_idempotent</c> pins — and it costs nothing on the
///         first run, unlike a guarding <c>not exists</c> subquery.
///     </para>
/// </remarks>
internal sealed class AssignTagWhereOperation : Weasel.Storage.IStorageOperation
{
    private readonly EventGraph _events;
    private readonly ITagTypeRegistration _registration;
    private readonly object _value;
    private readonly ISqlFragment _predicate;

    internal AssignTagWhereOperation(EventGraph events, ITagTypeRegistration registration, object value,
        ISqlFragment predicate)
    {
        _events = events;
        _registration = registration;
        _value = value;
        _predicate = predicate;
    }

    public Type DocumentType => typeof(object);

    public OperationRole Role() => OperationRole.Other;

    public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
    {
        builder.Append("insert into ");
        builder.Append(_events.TagTableName(_registration));
        builder.Append(" (value, seq_id) select ");
        builder.AppendParameter(_value);
        builder.Append(", seq_id from ");
        builder.Append(_events.EventsTableName);
        builder.Append(" where ");

        _predicate.Apply(builder);

        builder.Append(" on conflict do nothing;");
    }

    public Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
        => Task.CompletedTask;
}
