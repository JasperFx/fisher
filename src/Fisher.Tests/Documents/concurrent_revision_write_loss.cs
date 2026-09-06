using JasperFx;
using Microsoft.Data.Sqlite;
using Weasel.Core;
using Weasel.Storage;

namespace Fisher.Tests.Documents;

/// <summary>
///     Silent write-loss on a revisioned upsert — Marten <c>c09eed24c</c> + <c>c8e851722</c>, checked
///     against genuinely concurrent writers rather than against the sequential guard
///     <c>numeric_revisions</c> already pins.
/// </summary>
/// <remarks>
///     <para>
///         <b>The Marten bug.</b> <c>mt_upsert_&lt;doc&gt;</c> ran a conditional
///         <c>ON CONFLICT … DO UPDATE … WHERE revision &gt; mt_version</c> and then unconditionally
///         re-<c>SELECT</c>ed <c>mt_version</c> as its return value. When a concurrent transaction had
///         already moved the version past the caller's revision the UPDATE matched no row, but the
///         function still returned a non-zero version — which Marten reads as success. The write was
///         dropped and reported as landed. The fix made the returned value come from the UPDATE
///         itself, via <c>RETURNING</c>.
///     </para>
///     <para>
///         <b>Why Fisher's shape is the fixed one already.</b> The upsert is
///         <c>insert … on conflict … do update set … where (? = 0 or t.revision &lt; ?) returning revision</c>
///         — one statement, and <c>RETURNING</c> in SQLite emits a row only for a row the statement
///         actually wrote. A guard that matches nothing yields no row, which is exactly what the
///         shared numeric operations' postprocessing reads as a <see cref="ConcurrencyException" />.
///         There is no second read to disagree with the first, because there is no second read.
///     </para>
///     <para>
///         <b>SQLite's one-writer-per-file does not by itself make this safe, which is why these are
///         behavioural tests and not a comment.</b> Serialising the writers removes the interleaving,
///         not the staleness: two sessions can both read revision 1, and the second's commit then runs
///         against a row already at 2. That is the whole of the bug — a guard evaluated against a row
///         somebody else moved — and it survives serialisation intact. What one writer per file
///         guarantees is only that the two do not overlap <em>inside</em> the statement.
///     </para>
///     <para>
///         <b>Every assertion pairs a reported outcome with the stored row</b>, because a silent loss
///         is precisely the case where the two disagree. Asserting only that one writer threw would
///         pass against a store that dropped the winner's write as well.
///     </para>
/// </remarks>
public class concurrent_revision_write_loss : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("revision-races");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<Permit> StorePermitAsync(string description)
    {
        var permit = new Permit { Id = Guid.NewGuid(), Description = description };

        await using var session = _store.LightweightSession();
        session.Store(permit);
        await session.SaveChangesAsync(Token);

        return permit;
    }

    private async Task<(long Revision, string Description)> StoredAsync(Guid id)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select revision, json_extract(data, '$.description') from fi_doc_permit where id = @id";
        command.Parameters.AddWithValue("@id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).ShouldBeTrue();

        return (reader.GetInt64(0), reader.GetString(1));
    }

    /// <remarks>
    ///     Two writers, both holding revision 1, both trying to move it to 2. Exactly one may win, and
    ///     — the half that catches the Marten shape — <b>the row must hold what the winner wrote</b>.
    ///     A silent loss shows as both reporting success with only one description stored.
    /// </remarks>
    [Fact]
    public async Task two_writers_at_the_same_revision_do_not_both_report_success()
    {
        var permit = await StorePermitAsync("Trout, one rod");

        await using var first = _store.LightweightSession();
        await using var second = _store.LightweightSession();

        var mine = await first.LoadAsync<Permit>(permit.Id, Token);
        var theirs = await second.LoadAsync<Permit>(permit.Id, Token);

        mine!.Version.ShouldBe(1);
        theirs!.Version.ShouldBe(1);

        mine.Description = "Pike";
        theirs.Description = "Perch";

        first.UpdateRevision(mine, 2);
        second.UpdateRevision(theirs, 2);

        var winners = new List<string>();

        foreach (var (session, document) in new[] { (first, mine), (second, theirs) })
        {
            try
            {
                await session.SaveChangesAsync(Token);
                winners.Add(document.Description);
            }
            catch (ConcurrencyException)
            {
            }
        }

        winners.Count.ShouldBe(1);

        var stored = await StoredAsync(permit.Id);
        stored.Revision.ShouldBe(2);
        stored.Description.ShouldBe(winners.Single());
    }

    /// <remarks>
    ///     <para>
    ///         The same fact scaled: eight writers all holding revision 1 and all claiming revision 2.
    ///         Exactly one may report success, and the row must agree with whoever did.
    ///     </para>
    ///     <para>
    ///         <b>The eight reads overlap and the eight commits are deliberately serialised</b>, and
    ///         that is the honest shape rather than a weakening. What this bug class is about is a
    ///         guard evaluated against a row somebody else moved — which is entirely a property of
    ///         the eight <em>reads</em> having happened before any commit. Releasing eight commits at
    ///         once on one SQLite file adds no staleness whatever: the file takes one writer at a
    ///         time regardless, so the seven losers would simply queue at the write lock, each waiting
    ///         out the connection's 30-second busy timeout. That turns a deterministic assertion into
    ///         a minutes-long test whose failures are timeouts.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task racing_writers_never_report_success_for_a_write_that_did_not_land()
    {
        const int writers = 8;

        var permit = await StorePermitAsync("Trout, one rod");

        var sessions = new List<IDocumentSession>();
        var documents = new List<Permit>();

        // Every writer reads before any of them writes — this is where the staleness comes from.
        await Task.WhenAll(Enumerable.Range(0, writers).Select(async i =>
        {
            var session = _store.LightweightSession();

            var loaded = await session.LoadAsync<Permit>(permit.Id, Token);
            loaded!.Version.ShouldBe(1);
            loaded.Description = $"Writer {i}";

            lock (sessions)
            {
                sessions.Add(session);
                documents.Add(loaded);
            }
        }));

        var reported = new List<string>();

        for (var i = 0; i < writers; i++)
        {
            sessions[i].UpdateRevision(documents[i], 2);

            try
            {
                await sessions[i].SaveChangesAsync(Token);
                reported.Add(documents[i].Description);
            }
            catch (ConcurrencyException)
            {
            }
        }

        foreach (var session in sessions)
        {
            await session.DisposeAsync();
        }

        reported.Count.ShouldBe(1);

        var stored = await StoredAsync(permit.Id);
        stored.Revision.ShouldBe(2);
        stored.Description.ShouldBe(reported.Single());
    }

    /// <remarks>
    ///     The auto-increment path, which has no guard to fail and therefore a different way to lose a
    ///     write: the final revision must account for every success. Six racing writers that all
    ///     succeed must leave the row at 1 + 6, so a dropped update shows as a revision lower than the
    ///     number of writes claimed to have landed.
    /// </remarks>
    [Fact]
    public async Task auto_increment_accounts_for_every_reported_success()
    {
        const int writers = 6;

        var permit = await StorePermitAsync("Trout, one rod");

        var successes = 0;

        for (var i = 0; i < writers; i++)
        {
            await using var session = _store.LightweightSession();

            // Revision 0 is the auto sentinel: increment whatever is stored, guard always passes.
            session.Store(new Permit { Id = permit.Id, Description = $"Writer {i}", Version = 0 });

            try
            {
                await session.SaveChangesAsync(Token);
                successes++;
            }
            catch (ConcurrencyException)
            {
            }
        }

        successes.ShouldBe(writers);
        (await StoredAsync(permit.Id)).Revision.ShouldBe(1 + writers);
    }

    /// <remarks>
    ///     <c>TryUpdateRevision</c> is the "last writer loses quietly" variant, so it reports nothing
    ///     — which makes it the one shape where a silent loss is <em>correct</em>. What must still hold
    ///     is that the row keeps the newer write rather than the dropped one, and that everything else
    ///     in the unit of work commits.
    /// </remarks>
    [Fact]
    public async Task a_dropped_try_update_leaves_the_newer_write_in_place()
    {
        var permit = await StorePermitAsync("Trout, one rod");

        await using (var ahead = _store.LightweightSession())
        {
            var theirs = await ahead.LoadAsync<Permit>(permit.Id, Token);
            theirs!.Description = "Pike";
            ahead.UpdateRevision(theirs, 5);
            await ahead.SaveChangesAsync(Token);
        }

        var other = new Permit { Id = Guid.NewGuid(), Description = "Unrelated" };

        await using (var stale = _store.LightweightSession())
        {
            stale.TryUpdateRevision(new Permit { Id = permit.Id, Description = "Perch" }, 2);
            stale.Store(other);

            await stale.SaveChangesAsync(Token);
        }

        var stored = await StoredAsync(permit.Id);
        stored.Revision.ShouldBe(5);
        stored.Description.ShouldBe("Pike");

        (await StoredAsync(other.Id)).Description.ShouldBe("Unrelated");
    }
}
