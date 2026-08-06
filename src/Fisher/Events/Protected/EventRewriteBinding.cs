using Fisher.Storage;
using Weasel.Core;
using Weasel.Storage;

namespace Fisher.Events.Protected;

/// <summary>
///     Parameter binding shared by the three event-rewrite operations.
/// </summary>
/// <remarks>
///     The same two rules the append path follows, and for the same reasons: a Guid is converted to
///     its lowercase canonical text before binding, because Microsoft.Data.Sqlite would otherwise
///     write a 16-byte BLOB that never matches the TEXT <c>fi_events</c> holds; and the parameter's
///     SQLite type is set explicitly rather than inferred. <see cref="IStorageDialect.SetParameterType" />
///     does not depend on the dialect's identity type parameter, so one closed instance serves.
/// </remarks>
internal static class EventRewriteBinding
{
    private static readonly IStorageDialect Dialect = SqliteStorageDialect<Guid>.Instance;

    internal static void Bind(ICommandBuilder builder, object value, StorageColumnType type)
    {
        var parameter = builder.AppendParameter(SqliteStorageDialect<Guid>.ToDatabaseValue(value));
        Dialect.SetParameterType(parameter, type);
    }
}
