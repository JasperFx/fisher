using Microsoft.Data.Sqlite;

namespace Fisher.TestUtils;

/// <summary>
///     A throwaway SQLite database file, deleted on dispose.
/// </summary>
/// <remarks>
///     <para>
///         Fisher's tests isolate by file rather than by schema, because SQLite has no schemas —
///         the analogue of Polecat's per-test <c>DatabaseSchemaName</c> isolation is simply a
///         different database file.
///     </para>
///     <para>
///         A file, not <c>:memory:</c>. An in-memory database is scoped to a single connection unless
///         it is opened shared-cache, and Fisher opens several — sessions, the schema migrator, and
///         eventually the async daemon. Shared-cache in-memory databases also serialize differently
///         under WAL than a real file does, which would make the tests a poor proxy for how Fisher
///         actually behaves.
///     </para>
/// </remarks>
public sealed class TemporaryDatabase : IAsyncDisposable, IDisposable
{
    private TemporaryDatabase(string path)
    {
        Path = path;
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }

    /// <summary>The full path of the backing database file.</summary>
    public string Path { get; }

    /// <summary>A connection string pointing at <see cref="Path" />.</summary>
    public string ConnectionString { get; }

    /// <summary>
    ///     Create a uniquely named database file in the system temp directory.
    /// </summary>
    /// <param name="name">
    ///     Optional prefix to make the file recognizable when a test leaves one behind.
    /// </param>
    public static TemporaryDatabase Create(string? name = null)
    {
        var fileName = $"fisher-{name ?? "test"}-{Guid.NewGuid():n}.db";
        return new TemporaryDatabase(System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName));
    }

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools connections by connection string, and a pooled connection keeps
        // the file handle (and the -wal / -shm sidecars) alive. Without clearing the pool first the
        // delete below silently fails on Windows and leaves WAL sidecars behind everywhere else.
        SqliteConnection.ClearAllPools();

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = Path + suffix;

            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // A leaked connection elsewhere in the run holds the file. Leaving a stray temp file
                // behind is not worth failing an otherwise passing test over.
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}
