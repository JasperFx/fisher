using Fisher.TestUtils;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace Fisher.Tests.Schema;

/// <summary>
///     fisher#462 — the native SQLite build Fisher is tested against. Fisher stays on the
///     <c>SQLitePCLRaw.bundle_e_sqlite3</c> 3.0.x line (weasel#739): no SQLite3MC swap, no cap on
///     Microsoft.Data.Sqlite. What Fisher needs from the build is a floor, and this is the one place
///     that says so, so a pin that drifts to an older bundle fails here rather than as a json1 error.
/// </summary>
public class native_sqlite_bundle
{
    [Fact]
    public async Task the_bundled_sqlite_meets_the_floor_fisher_relies_on()
    {
        using var database = TemporaryDatabase.Create("bundle");
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "select sqlite_version()";
        var version = Version.Parse((string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);

        version.ShouldBeGreaterThanOrEqualTo(new Version(3, 44, 0));
    }
}
