using System.Buffers;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Weasel.Core;
using Weasel.Core.SqlGeneration;
using Weasel.Sqlite;
using Weasel.Storage;

namespace Fisher.Storage;

/// <summary>
///     SQLite implementation of the shared closed-shape storage runtime's <see cref="IStorageDialect" />
///     strategy — the per-engine half of document load and parameter binding. The counterpart of
///     Marten's <c>PostgresStorageDialect&lt;TId&gt;</c> and Polecat's <c>SqlServerStorageDialect&lt;TId&gt;</c>.
/// </summary>
/// <remarks>
///     <para>
///         SQLite's type system is affinity-based, so parameter typing carries far less weight here
///         than it does in the SQL Server dialect: there is no <c>varchar</c> versus <c>nvarchar</c>
///         index-seek trap to avoid, because there is only TEXT. What the mapping below must get
///         right is the .NET-side conversion — binding a <see cref="Guid" /> as
///         <see cref="SqliteType.Text" /> so it round-trips as the same string the schema stores.
///     </para>
/// </remarks>
internal sealed class SqliteStorageDialect<TId> : IStorageDialect
{
    public static readonly IStorageDialect Instance = new SqliteStorageDialect<TId>();

    private SqliteStorageDialect()
    {
    }

    public DbCommand BuildLoadCommand(string loaderSql, object rawId, string? tenant)
    {
        var command = new SqliteCommand(loaderSql);
        command.Parameters.Add(CreateParameter("id", ToDatabaseValue(rawId), TypeForRawId(rawId)));

        if (tenant is not null)
        {
            command.Parameters.Add(CreateParameter("tenant_id", tenant, SqliteType.Text));
        }

        return command;
    }

    /// <summary>
    ///     SQLite has no array parameters. Ids travel as a JSON array string that the load-many SQL
    ///     unpacks with <c>json_each(@ids)</c> — the json1 analogue of SQL Server's <c>OPENJSON</c>
    ///     and Postgres's <c>= ANY($1)</c>.
    /// </summary>
    /// <remarks>
    ///     The JSON is written by hand rather than through the serializer: the values are always
    ///     scalars, and doing it manually keeps the dialect free of reflection-based serialization
    ///     and therefore trim/AOT-clean.
    /// </remarks>
    public DbParameter CreateIdArrayParameter(Array rawIds, Type rawSqlType)
        => CreateParameter("ids", WriteIdsAsJsonArray(rawIds), SqliteType.Text);

    public DbCommand BuildLoadManyCommand(string loadArraySql, DbParameter idArrayParameter, string? tenant)
    {
        var command = new SqliteCommand(loadArraySql);
        command.Parameters.Add((SqliteParameter)idArrayParameter);

        if (tenant is not null)
        {
            command.Parameters.Add(CreateParameter("tenant_id", tenant, SqliteType.Text));
        }

        return command;
    }

    public ISqlFragment ByIdFilter(object rawId) => new SqliteByIdFilter(ToDatabaseValue(rawId));

    /// <summary>
    ///     SQLite reports a missing table as the generic <c>SQLITE_ERROR</c> (1) with a "no such
    ///     table" message; there is no dedicated error code to match on the way SQL Server's 208 or
    ///     Postgres's 42P01 allow.
    /// </summary>
    public bool IsUndefinedTable(Exception exception)
        => exception is SqliteException { SqliteErrorCode: 1 } sqlite &&
           sqlite.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase);

    public void SetParameterType(DbParameter parameter, StorageColumnType type)
        => ((SqliteParameter)parameter).SqliteType = type switch
        {
            StorageColumnType.String => SqliteType.Text,
            // Stored as TEXT — see EventsTable. Microsoft.Data.Sqlite would otherwise bind a Guid as
            // a 16-byte BLOB, which would never match the TEXT the schema holds.
            StorageColumnType.Guid => SqliteType.Text,
            StorageColumnType.Long => SqliteType.Integer,
            StorageColumnType.Int => SqliteType.Integer,
            // No BOOLEAN type; 0/1 in an INTEGER column.
            StorageColumnType.Boolean => SqliteType.Integer,
            // No date/time type; ISO-8601 text — see SqliteTimestamp.
            StorageColumnType.Timestamp => SqliteType.Text,
            StorageColumnType.Json => SqliteType.Text,
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

    public void SetIdParameterType(DbParameter parameter, Type rawSqlType)
        => ((SqliteParameter)parameter).SqliteType = TypeForType(rawSqlType);

    /// <summary>
    ///     Convert a raw id to the CLR value SQLite should bind.
    /// </summary>
    /// <remarks>
    ///     Guids become their canonical string form so they match the TEXT columns the schema
    ///     declares. Everything else binds as-is.
    /// </remarks>
    internal static object ToDatabaseValue(object rawId)
        => rawId is Guid guid ? guid.ToString() : rawId;

    // Not derived from typeof(TId): for strongly-typed ids TId is the wrapper type while the raw SQL
    // value is the unwrapped inner (Guid/string/int/long), so type from the runtime value.
    private static SqliteType TypeForRawId(object rawId) => rawId switch
    {
        Guid => SqliteType.Text,
        string => SqliteType.Text,
        int or long or short or byte or bool => SqliteType.Integer,
        _ => TypeForType(rawId.GetType())
    };

    private static SqliteType TypeForType(Type type)
        => type == typeof(Guid) ? SqliteType.Text : SqliteProvider.Instance.ToParameterType(type);

    private static SqliteParameter CreateParameter(string name, object value, SqliteType type)
        => new(name, value) { SqliteType = type };

    private static string WriteIdsAsJsonArray(Array rawIds)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();

            foreach (var id in rawIds)
            {
                switch (id)
                {
                    case Guid guid:
                        writer.WriteStringValue(guid.ToString());
                        break;
                    case string text:
                        writer.WriteStringValue(text);
                        break;
                    case int number:
                        writer.WriteNumberValue(number);
                        break;
                    case long number:
                        writer.WriteNumberValue(number);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(rawIds),
                            $"Unsupported raw id type {id?.GetType().FullName ?? "null"}; expected Guid, string, int, or long.");
                }
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>
///     The <c>id = ?</c> filter, implementing the dialect-neutral
///     <see cref="Weasel.Core.SqlGeneration.ISqlFragment" /> directly.
/// </summary>
/// <remarks>
///     Unlike Marten and Polecat there is no Weasel.Sqlite <c>ISqlFragment</c> to derive from —
///     Weasel.Sqlite ships no SqlGeneration namespace — so this targets the shared
///     <see cref="ICommandBuilder" /> surface, which is all the fragment needs.
/// </remarks>
internal sealed class SqliteByIdFilter : ISqlFragment
{
    private readonly object _value;

    public SqliteByIdFilter(object value)
    {
        _value = value;
    }

    public void Apply(ICommandBuilder builder)
    {
        builder.Append("id = ");
        builder.AppendParameter(_value);
    }

    public bool Contains(string sqlText) => false;
}
