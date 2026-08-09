using System.Data.Common;
using System.Runtime.CompilerServices;
using Fisher.Storage;
using Microsoft.Data.Sqlite;
using Weasel.Storage;

namespace Fisher.Internal;

/// <summary>
///     <see cref="IAdvancedSql" /> — raw SQL in, typed results out (fisher#34).
/// </summary>
/// <remarks>
///     The read counterpart to <c>QueueSqlCommand</c>, and it shares that method's whole rationale:
///     an application using Fisher keeps its own tables in the same file, so being able to read across
///     both in one statement — on the session's connection, inside its transaction — is what makes the
///     single-file arrangement usable rather than merely possible.
/// </remarks>
internal partial class FisherSession : IAdvancedSql
{
    private const char DefaultPlaceholder = '?';

    public IAdvancedSql AdvancedSql => this;

    string[] IAdvancedSql.SelectFieldsFor<T>()
        => FisherDatabase.Providers.StorageFor<T>().QueryOnly is ISelectClause clause
            ? clause.SelectFields()
            : throw new InvalidOperationException(
                $"The storage for '{typeof(T).Name}' cannot produce a select clause.");

    // ---- single result type ----

    Task<IReadOnlyList<T>> IAdvancedSql.QueryAsync<T>(string sql, CancellationToken token,
        params object?[] parameters)
        => ((IAdvancedSql)this).QueryAsync<T>(DefaultPlaceholder, sql, token, parameters);

    Task<IReadOnlyList<T>> IAdvancedSql.QueryAsync<T>(char placeholder, string sql, CancellationToken token,
        params object?[] parameters)
        => ReadAllAsync(placeholder, sql, parameters, [typeof(T)],
            values => (T)values[0]!, token);

    // ---- two ----

    Task<IReadOnlyList<(T1, T2)>> IAdvancedSql.QueryAsync<T1, T2>(string sql, CancellationToken token,
        params object?[] parameters)
        => ((IAdvancedSql)this).QueryAsync<T1, T2>(DefaultPlaceholder, sql, token, parameters);

    Task<IReadOnlyList<(T1, T2)>> IAdvancedSql.QueryAsync<T1, T2>(char placeholder, string sql,
        CancellationToken token, params object?[] parameters)
        => ReadAllAsync(placeholder, sql, parameters, [typeof(T1), typeof(T2)],
            values => ((T1)values[0]!, (T2)values[1]!), token);

    // ---- three ----

    Task<IReadOnlyList<(T1, T2, T3)>> IAdvancedSql.QueryAsync<T1, T2, T3>(string sql, CancellationToken token,
        params object?[] parameters)
        => ((IAdvancedSql)this).QueryAsync<T1, T2, T3>(DefaultPlaceholder, sql, token, parameters);

    Task<IReadOnlyList<(T1, T2, T3)>> IAdvancedSql.QueryAsync<T1, T2, T3>(char placeholder, string sql,
        CancellationToken token, params object?[] parameters)
        => ReadAllAsync(placeholder, sql, parameters, [typeof(T1), typeof(T2), typeof(T3)],
            values => ((T1)values[0]!, (T2)values[1]!, (T3)values[2]!), token);

    // ---- streaming ----

    IAsyncEnumerable<T> IAdvancedSql.StreamAsync<T>(string sql, CancellationToken token,
        params object?[] parameters)
        => ((IAdvancedSql)this).StreamAsync<T>(DefaultPlaceholder, sql, token, parameters);

    IAsyncEnumerable<T> IAdvancedSql.StreamAsync<T>(char placeholder, string sql, CancellationToken token,
        params object?[] parameters)
        => StreamRowsAsync(placeholder, sql, parameters, [typeof(T)],
            values => (T)values[0]!, token);

    IAsyncEnumerable<(T1, T2)> IAdvancedSql.StreamAsync<T1, T2>(string sql, CancellationToken token,
        params object?[] parameters)
        => ((IAdvancedSql)this).StreamAsync<T1, T2>(DefaultPlaceholder, sql, token, parameters);

    IAsyncEnumerable<(T1, T2)> IAdvancedSql.StreamAsync<T1, T2>(char placeholder, string sql,
        CancellationToken token, params object?[] parameters)
        => StreamRowsAsync(placeholder, sql, parameters, [typeof(T1), typeof(T2)],
            values => ((T1)values[0]!, (T2)values[1]!), token);

    // ---- the two engines ----

    /// <remarks>
    ///     Materialized <em>inside</em> the resilience pipeline. A retried <c>SQLITE_BUSY</c>
    ///     re-executes the whole delegate, so yielding a live reader out of it would let the retry
    ///     resume against a connection the previous attempt had already disposed — the property
    ///     <c>GetRecentStreamsAsync</c> documents and <c>StreamRowsAsync</c> below deliberately
    ///     forgoes.
    /// </remarks>
    private async Task<IReadOnlyList<TResult>> ReadAllAsync<TResult>(char placeholder, string sql,
        object?[] parameters, Type[] resultTypes, Func<object?[], TResult> project, CancellationToken token)
    {
        var readers = ReadersFor(resultTypes);
        var connection = await ConnectionAsync(token).ConfigureAwait(false);

        return await Options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var command = BuildCommand(placeholder, sql, parameters, connection);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            var results = new List<TResult>();

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(project(ReadRow(reader, readers)));
            }

            return (IReadOnlyList<TResult>)results;
        }, token).ConfigureAwait(false);
    }

    /// <remarks>
    ///     Outside the resilience pipeline, deliberately — see <see cref="IAdvancedSql.StreamAsync{T}" />
    ///     for the trade. A <c>SQLITE_BUSY</c> here surfaces to the caller.
    /// </remarks>
    private async IAsyncEnumerable<TResult> StreamRowsAsync<TResult>(char placeholder, string sql,
        object?[] parameters, Type[] resultTypes, Func<object?[], TResult> project,
        [EnumeratorCancellation] CancellationToken token)
    {
        var readers = ReadersFor(resultTypes);
        var connection = await ConnectionAsync(token).ConfigureAwait(false);

        await using var command = BuildCommand(placeholder, sql, parameters, connection);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            yield return project(ReadRow(reader, readers));
        }
    }

    private AdvancedSqlResultReader[] ReadersFor(Type[] resultTypes)
    {
        var readers = new AdvancedSqlResultReader[resultTypes.Length];

        for (var i = 0; i < resultTypes.Length; i++)
        {
            readers[i] = AdvancedSqlResultReader.ForType(resultTypes[i], this);

            // A document is materialized by its storage's own selector, which resolves from fixed
            // positions starting at column 0 — see AdvancedSqlResultReader for why going through the
            // selector rather than deserializing `data` by hand is the right trade. Saying so beats
            // the InvalidCastException a silently misaligned read would produce.
            if (i > 0 && readers[i].MustLeadTheRow)
            {
                throw new InvalidOperationException(
                    $"'{resultTypes[i].Name}' is a Fisher document type, and a document can only be the "
                    + $"first result type of a raw SQL query — it was asked for at position {i + 1}. "
                    + "Reorder the query so its columns come first, or select the members you want as "
                    + "scalars instead.");
            }
        }

        return readers;
    }

    private static object?[] ReadRow(DbDataReader reader, AdvancedSqlResultReader[] readers)
    {
        var values = new object?[readers.Length];
        var column = 0;

        for (var i = 0; i < readers.Length; i++)
        {
            values[i] = readers[i].ReadValue(reader, column);
            column += readers[i].ColumnCount;
        }

        return values;
    }

    /// <remarks>
    ///     Parameter values go through <see cref="SqliteParameterValue" /> for the same reason
    ///     <c>QueueSqlCommand</c>'s do: a Guid, a timestamp or a decimal bound raw matches nothing
    ///     Fisher wrote, silently.
    /// </remarks>
    private SqliteCommand BuildCommand(char placeholder, string sql, object?[] parameters,
        SqliteConnection connection)
    {
        var builder = new Weasel.Sqlite.CommandBuilder();
        var slots = builder.AppendWithDbParameters(sql, placeholder);

        if (slots.Length != parameters.Length)
        {
            throw new InvalidOperationException(
                $"Wrong number of parameter values for SQL '{sql}': the statement has {slots.Length} "
                + $"'{placeholder}' placeholders and {parameters.Length} values were supplied.");
        }

        for (var i = 0; i < slots.Length; i++)
        {
            slots[i].Value = SqliteParameterValue.ToDatabaseValue(parameters[i]);
        }

        var command = builder.Compile();
        command.Connection = connection;
        command.Transaction = EnlistedTransaction;
        command.CommandTimeout = CommandTimeout;

        return command;
    }
}
