using Fisher.Storage;
using JasperFx;
using Microsoft.Data.Sqlite;
using Weasel.Core;

namespace Fisher.Tests.Documents;

/// <summary>
///     Server-written timestamps really are UTC — marten#5136's shape, checked against SQLite's
///     idioms rather than PostgreSQL's.
/// </summary>
/// <remarks>
///     <para>
///         <b>The Marten bug.</b> <c>now() at time zone 'utc'</c> written into a <c>timestamptz</c>
///         column yields a naive timestamp that PostgreSQL then re-interprets in the server's own
///         zone, so <c>mt_last_modified</c> and <c>mt_events.timestamp</c> came out skewed by the
///         server's UTC offset. Invisible on a UTC server, which is where it was tested.
///     </para>
///     <para>
///         <b>SQLite's analogues, and why Fisher's spelling is the correct one.</b> There is no
///         date/time type at all, so the entire question is what text goes into a TEXT column. SQLite's
///         <c>'now'</c> modifier is UTC by construction; <c>datetime('now','localtime')</c> is the way
///         to get the machine's wall clock, and <c>CURRENT_TIMESTAMP</c> is UTC but renders
///         <c>'YYYY-MM-DD HH:MM:SS'</c> with no sub-second part and no zone marker.
///         <see cref="SqliteTimestamp.NowExpression" /> is <c>strftime('%Y-%m-%dT%H:%M:%fZ','now')</c>
///         — UTC, milliseconds, and a literal <c>Z</c> that is honest rather than decorative.
///     </para>
///     <para>
///         <b>Two halves, because neither alone is enough.</b> The behavioural tests compare a stored
///         instant against a client-side UTC window, which catches a local-time expression outright —
///         but only on a client that is not itself on UTC, so they <b>skip rather than pass</b> when
///         the host is at offset zero. A UTC CI runner therefore cannot green them by accident. The
///         schema tests are timezone-independent and always run: they are what would catch a
///         <c>'localtime'</c> modifier or a stray <c>CURRENT_TIMESTAMP</c> introduced later, on any
///         host.
///     </para>
///     <para>
///         Fisher's client side is clean too and stays that way by the same means:
///         <see cref="SqliteTimestamp.ToDatabaseValue" /> calls <c>ToUniversalTime()</c>, and
///         <see cref="SqliteTimestamp.FromDatabaseValue" /> parses with <c>AssumeUniversal</c> — so a
///         value that lost its <c>Z</c> is still read as UTC rather than as local, which is the read
///         half of the same mistake.
///     </para>
/// </remarks>
public class server_timestamp_utc : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("utc-timestamps");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Boat>().SoftDeleted();
            options.Schema.For<Boat>().Metadata(m => m.CreatedAt.Enabled = true);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    ///     The client's current offset from UTC. Zero means this run cannot tell a UTC expression from
    ///     a local-time one, whatever the store does.
    /// </summary>
    private static TimeSpan LocalOffset => TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow);

    /// <summary>
    ///     Skip rather than pass when the host is on UTC, so this file cannot go green by accident on
    ///     a UTC CI runner.
    /// </summary>
    private static void RequireANonUtcHost()
    {
        if (LocalOffset == TimeSpan.Zero)
        {
            Assert.Skip(
                "This host is on UTC, so a local-time expression would be indistinguishable from a "
                + "UTC one. The schema tests in this class still discriminate and still ran.");
        }
    }

    private async Task<string?> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var value = await command.ExecuteScalarAsync(Token);
        return value as string;
    }

    private async Task<IReadOnlyList<(string Table, string Sql)>> SchemaAsync()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select name, sql from sqlite_master where sql is not null";

        var rows = new List<(string, string)>();

        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    // ---- behavioural: only meaningful off UTC ----

    [Fact]
    public async Task a_documents_last_modified_is_the_real_utc_instant()
    {
        RequireANonUtcHost();

        var id = Guid.NewGuid();

        var before = DateTimeOffset.UtcNow;

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Boat { Id = id, Name = "Northern Star" });
            await session.SaveChangesAsync(Token);
        }

        var after = DateTimeOffset.UtcNow;

        await using var query = _store.QuerySession();
        var metadata = await query.MetadataForAsync<Boat>(id, Token);

        metadata.ShouldNotBeNull();

        // A dropped or inverted offset lands a whole timezone away — hours, not the milliseconds of
        // slack these bounds allow.
        metadata.LastModified.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-1));
        metadata.LastModified.ShouldBeLessThanOrEqualTo(after.AddSeconds(1));
    }

    [Fact]
    public async Task a_documents_created_at_is_the_real_utc_instant()
    {
        RequireANonUtcHost();

        var id = Guid.NewGuid();

        var before = DateTimeOffset.UtcNow;

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Boat { Id = id, Name = "Northern Star" });
            await session.SaveChangesAsync(Token);
        }

        var after = DateTimeOffset.UtcNow;

        // created_at is filled by the column DEFAULT rather than by a write binder, so it is the one
        // that exercises NowDefaultExpression rather than NowExpression.
        await using var query = _store.QuerySession();
        var metadata = await query.MetadataForAsync<Boat>(id, Token);

        metadata!.CreatedAt.ShouldNotBeNull();
        metadata.CreatedAt.Value.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-1));
        metadata.CreatedAt.Value.ShouldBeLessThanOrEqualTo(after.AddSeconds(1));
    }

    [Fact]
    public async Task a_soft_deletions_deleted_at_is_the_real_utc_instant()
    {
        RequireANonUtcHost();

        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Boat { Id = id, Name = "Northern Star" });
            await session.SaveChangesAsync(Token);
        }

        var before = DateTimeOffset.UtcNow;

        await using (var session = _store.LightweightSession())
        {
            session.Delete<Boat>(id);
            await session.SaveChangesAsync(Token);
        }

        var after = DateTimeOffset.UtcNow;

        await using var query = _store.QuerySession();
        var metadata = await query.MetadataForAsync<Boat>(id, Token);

        metadata!.DeletedAt.ShouldNotBeNull();
        metadata.DeletedAt.Value.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-1));
        metadata.DeletedAt.Value.ShouldBeLessThanOrEqualTo(after.AddSeconds(1));
    }

    /// <remarks>
    ///     This one is checking the <em>client</em> half rather than the column default: since
    ///     fisher#119 the append stamps <c>IEvent.Timestamp</c> from <c>DateTimeOffset.UtcNow</c>
    ///     through <see cref="SqliteTimestamp.ToDatabaseValue" /> and persists that value. So a
    ///     sabotaged <see cref="SqliteTimestamp.NowExpression" /> leaves this test green — which is
    ///     the point of it: the two paths have to agree, and this is the one that pins the path the
    ///     schema tests below cannot see.
    /// </remarks>
    [Fact]
    public async Task an_events_timestamp_is_the_real_utc_instant()
    {
        RequireANonUtcHost();

        var before = DateTimeOffset.UtcNow;

        Guid streamId;

        await using (var session = _store.LightweightSession())
        {
            streamId = session.Events.StartStream<Boat>(new BoatLaunched("Northern Star")).Id;
            await session.SaveChangesAsync(Token);
        }

        var after = DateTimeOffset.UtcNow;

        await using var query = _store.QuerySession();
        var events = await query.Events.FetchStreamAsync(streamId, token: Token);

        var timestamp = events.Single().Timestamp;
        timestamp.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-1));
        timestamp.ShouldBeLessThanOrEqualTo(after.AddSeconds(1));
    }

    /// <remarks>
    ///     The expression on its own, against the client's own UTC clock — the narrowest statement of
    ///     the property, with no Fisher column in the way. If this fails, everything above is failing
    ///     for one reason.
    /// </remarks>
    [Fact]
    public async Task the_now_expression_agrees_with_the_clients_utc_clock()
    {
        RequireANonUtcHost();

        var before = DateTimeOffset.UtcNow;
        var rendered = await ScalarAsync($"select {SqliteTimestamp.NowExpression}");
        var after = DateTimeOffset.UtcNow;

        rendered.ShouldNotBeNull();

        var parsed = SqliteTimestamp.FromDatabaseValue(rendered);
        parsed.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-1));
        parsed.ShouldBeLessThanOrEqualTo(after.AddSeconds(1));
    }

    // ---- schema: timezone-independent, always run ----

    /// <remarks>
    ///     The property a UTC deployment cannot check behaviourally and still has to hold. Asserted
    ///     over the whole schema rather than over a list of columns, because the risk is a
    ///     <em>new</em> column spelling its own default.
    /// </remarks>
    [Fact]
    public async Task no_schema_object_asks_sqlite_for_local_time()
    {
        foreach (var (name, sql) in await SchemaAsync())
        {
            sql.ShouldNotContain("localtime", Case.Insensitive,
                $"'{name}' asks SQLite for the machine's wall clock, which is not UTC.");

            // CURRENT_TIMESTAMP is UTC but has no sub-second part and no zone marker, so it neither
            // round-trips through SqliteTimestamp nor orders events appended in the same second.
            sql.ShouldNotContain("CURRENT_TIMESTAMP", Case.Insensitive,
                $"'{name}' should use SqliteTimestamp.NowDefaultExpression instead.");
        }
    }

    /// <remarks>
    ///     Every server-written timestamp default is the one shared expression, so there is exactly one
    ///     place the idiom can be got wrong. Discovered from the schema rather than listed, or a table
    ///     added later would simply not be checked.
    /// </remarks>
    [Fact]
    public async Task every_timestamp_default_in_the_schema_is_the_shared_utc_expression()
    {
        var defaults = 0;

        foreach (var (name, sql) in await SchemaAsync())
        {
            var index = 0;

            while ((index = sql.IndexOf("DEFAULT (", index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var open = index + "DEFAULT ".Length;
                var close = sql.IndexOf(')', open);

                // strftime(...) itself carries a nested paren, so take the matching one.
                var depth = 0;
                for (var i = open; i < sql.Length; i++)
                {
                    if (sql[i] == '(')
                    {
                        depth++;
                    }
                    else if (sql[i] == ')' && --depth == 0)
                    {
                        close = i;
                        break;
                    }
                }

                var expression = sql[open..(close + 1)];

                expression.ShouldBe(SqliteTimestamp.NowDefaultExpression,
                    $"'{name}' declares an expression default that is not the shared UTC one.");

                defaults++;
                index = close;
            }
        }

        // The event store alone contributes four (fi_events.timestamp, fi_streams.timestamp and
        // .created, fi_event_progression.last_updated), so a zero here means the walk found nothing
        // and asserted nothing.
        defaults.ShouldBeGreaterThanOrEqualTo(4);
    }

    /// <remarks>
    ///     The read half of the same mistake. A value that reached the column without its <c>Z</c> —
    ///     from a hand-written statement, or an older store — must still be read as UTC rather than
    ///     silently reinterpreted in the machine's zone.
    /// </remarks>
    [Fact]
    public void a_stored_timestamp_without_a_zone_marker_is_still_read_as_utc()
    {
        var withZ = SqliteTimestamp.FromDatabaseValue("2026-09-06T14:23:05.123Z");
        var withoutZ = SqliteTimestamp.FromDatabaseValue("2026-09-06T14:23:05.123");

        withoutZ.ShouldBe(withZ);
        withoutZ.Offset.ShouldBe(TimeSpan.Zero);
    }

    /// <remarks>
    ///     And the write half: a client-supplied value is normalised to UTC rather than written with
    ///     whatever offset it happened to carry, so the column stays comparable as text.
    /// </remarks>
    [Fact]
    public void a_client_timestamp_is_written_as_utc_whatever_offset_it_carried()
    {
        var instant = new DateTimeOffset(2026, 9, 6, 9, 23, 5, 123, TimeSpan.FromHours(-5));

        SqliteTimestamp.ToDatabaseValue(instant).ShouldBe("2026-09-06T14:23:05.123Z");
        SqliteTimestamp.ToDatabaseValue(instant.ToUniversalTime())
            .ShouldBe(SqliteTimestamp.ToDatabaseValue(instant));
    }
}

public record BoatLaunched(string Name);
