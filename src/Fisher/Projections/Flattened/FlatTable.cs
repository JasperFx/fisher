using Fisher.Storage;
using Weasel.Sqlite;
using Weasel.Sqlite.Tables;

namespace Fisher.Projections.Flattened;

/// <summary>
///     A flat-table projection's table definition, whose physical name is settled once the store's
///     logical schema is known.
/// </summary>
/// <remarks>
///     <para>
///         The subclass exists for exactly one reason: <c>SchemaObjectBase.Identifier</c> has a
///         <c>protected</c> setter, and Weasel's own <c>MoveToSchema</c> only changes the qualifier —
///         which is no help in SQLite, where the logical schema is folded into the <em>name</em>. So
///         the rename has to happen inside a derived type.
///     </para>
///     <para>
///         It is a rename rather than a constructor argument because a projection's constructor cannot
///         see the store it will be registered against, and defaulting to the unprefixed name would
///         quietly drop a flat table out of its logical store's isolation — two stores over one file
///         would share one table. See <see cref="FlatTableProjection.ResolveTableName" />.
///     </para>
/// </remarks>
internal sealed class FlatTable : Table
{
    public FlatTable(SqliteObjectName identifier) : base(identifier)
    {
    }

    public void RenameTo(string tableName)
        => Identifier = new SqliteObjectName(FisherTableNaming.DefaultSchemaName, tableName);
}
