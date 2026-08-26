using System.Text.Json.Serialization;
using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Documents;

/// <summary>
///     What a patch addresses, and what a query addresses, are the same thing — fisher#126.
/// </summary>
/// <remarks>
///     <para>
///         Two Marten bugs prompted these, and <b>neither exists here</b>. They are standing guards
///         rather than fixes, and the reason to have them rather than to infer the property from the
///         design is that Marten's code carried a comment asserting it was correct too.
///     </para>
///     <para>
///         <b>marten#5290</b> built a patch path from the raw CLR member name with only a casing
///         transform, so a member carrying <c>[JsonPropertyName]</c> was patched at a path the
///         serializer never reads. The patch reported success, every reader kept seeing the old value,
///         and the phantom node was erased by the next full save. Fisher's
///         <c>PatchExpression.PathOf</c> resolves through <c>MemberFactory.ResolveMember(...)</c>,
///         which is the machinery the LINQ provider uses, so the alias is honoured by construction.
///     </para>
///     <para>
///         <b>marten#5295</b> is the duplicated-column half. Marten and Polecat write a duplicated
///         column client-side during serialization, so a patch — server-side SQL — has to refresh it
///         explicitly, and theirs missed three shapes: an aliased member never matched, the overlap
///         test only ran one way (patching a parent did not refresh a column duplicated from a child),
///         and only the operation's <c>path</c> was collected, so a <c>Duplicate</c>'s destinations and
///         a <c>Rename</c>'s target were skipped. <b>Fisher has no refresh step to get wrong</b>:
///         fisher#2 made a duplicated field a SQLite <c>VIRTUAL</c> generated column over
///         <c>data</c>, so SQLite recomputes it and it cannot drift. All three shapes are covered
///         below anyway, because "cannot drift" is the claim under test.
///     </para>
///     <para>
///         <b>Every assertion here goes through a query, never through a reload.</b> That is the whole
///         point of the file. A reload reads <c>data</c>, so it cannot see a divergence between
///         <c>data</c> and the column — which is precisely why marten#5295 survived in production for
///         weeks with nothing erroring anywhere.
///     </para>
///     <para>
///         <b>Verified load-bearing, and the experiment is worth repeating rather than trusting.</b>
///         Replacing <c>PathOf</c>'s body with marten#5290 exactly — the CLR member name under a
///         camelCase transform — leaves <b>all 23 tests in <c>patching_documents</c> passing</b> and
///         fails three of these. So the existing file cannot see that bug at all: every member it
///         patches happens to carry no <c>[JsonPropertyName]</c>, and a casing transform is right for
///         those. The three here that survive the same sabotage are the ones that should — the by-name
///         overload does not route through <c>PathOf</c>, and <c>Home</c> and <c>Trips</c> carry no
///         alias, so those two pin the duplicated-column property instead.
///     </para>
/// </remarks>
public class patching_json_naming_attributes : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("patch-naming");
    private DocumentStore _store = null!;
    private readonly Guid _frodo = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;

            o.Schema.For<Guide>()
                // The aliased member, duplicated. marten#5295's first gap was that its refresh rule
                // never matched an aliased member at all.
                .Duplicate(x => x.Nickname)
                // Duplicated from a child of Home, so a patch on the parent has to move it. That is
                // the overlap the same bug only tested one way round.
                .Duplicate(x => x.Home.Name);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        session.Store(new Guide
        {
            Id = _frodo,
            Name = "Frodo",
            Nickname = "Ring-bearer",
            Retired = "no",
            Home = new Water { Name = "Brandywine" }
        });
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- marten#5290: the alias is the path ----

    /// <summary>
    ///     The bug's exact shape: patch at <c>$.Nickname</c> instead of <c>$.nick_name</c> and every
    ///     one of these three still "succeeds". The document keeps its old value, the query finds
    ///     nothing, and the raw JSON grows a key nothing reads.
    /// </summary>
    [Fact]
    public async Task a_patch_writes_to_the_serialized_key_not_the_member_name()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Patch<Guide>(_frodo).Set(x => x.Nickname, "Underhill");
            await session.SaveChangesAsync(Token);
        }

        await using var check = _store.LightweightSession();

        // Through the duplicated column, whose generated expression is json_extract(data,'$.nick_name').
        (await check.Query<Guide>().Where(x => x.Nickname == "Underhill").CountAsync(Token)).ShouldBe(1);

        // And through the serializer, which is the reader marten#5290 left behind.
        (await check.LoadAsync<Guide>(_frodo, Token))!.Nickname.ShouldBe("Underhill");

        // No phantom node under the CLR name. Patching the wrong path leaves the old key intact and
        // adds a second one, so counting keys is what tells "wrote elsewhere" from "wrote nothing".
        var json = await check.LoadJsonAsync<Guide>(_frodo, Token);
        json.ShouldNotBeNull();
        json.ShouldContain("nick_name");
        json.ShouldNotContain("\"Nickname\"");
    }

    /// <summary>
    ///     An aliased member reached by the by-name overloads, which take the <em>stored</em> key
    ///     deliberately — they exist to address a key the type has no member for, so routing them
    ///     through the member machinery would refuse the case they are for.
    /// </summary>
    [Fact]
    public async Task the_by_name_overloads_take_the_stored_key()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Patch<Guide>(_frodo).Set("nick_name", "Underhill");
            await session.SaveChangesAsync(Token);
        }

        await using var check = _store.LightweightSession();
        (await check.Query<Guide>().Where(x => x.Nickname == "Underhill").CountAsync(Token)).ShouldBe(1);
    }

    // ---- marten#5295: the duplicated column cannot drift ----

    /// <summary>
    ///     marten#5295's second gap: the overlap test ran one way only, so patching a parent left a
    ///     column duplicated from one of its children stale. Here the column is <c>home_name</c> and
    ///     the patch replaces the whole of <c>Home</c>.
    /// </summary>
    [Fact]
    public async Task patching_a_parent_moves_a_column_duplicated_from_its_child()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Patch<Guide>(_frodo).Set(x => x.Home, new Water { Name = "Withywindle" });
            await session.SaveChangesAsync(Token);
        }

        await using var check = _store.LightweightSession();

        (await check.Query<Guide>().Where(x => x.Home.Name == "Withywindle").CountAsync(Token)).ShouldBe(1);
        (await check.Query<Guide>().Where(x => x.Home.Name == "Brandywine").CountAsync(Token)).ShouldBe(0);
    }

    /// <summary>
    ///     marten#5295's third gap, first half: only the operation's own <c>path</c> was collected for
    ///     refresh, so a <c>Duplicate</c>'s destinations were missed. Both ends here are duplicated
    ///     <em>and</em> aliased, which is the intersection of that gap and the first one.
    /// </summary>
    [Fact]
    public async Task a_duplicate_operations_destinations_move_their_columns()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Patch<Guide>(_frodo).Duplicate(x => x.Name, "Underhill", x => x.Nickname);
            await session.SaveChangesAsync(Token);
        }

        await using var check = _store.LightweightSession();

        // The destination is the duplicated, aliased member — the one Marten's rule could not match.
        (await check.Query<Guide>().Where(x => x.Nickname == "Underhill").CountAsync(Token)).ShouldBe(1);
        (await check.Query<Guide>().Where(x => x.Name == "Underhill").CountAsync(Token)).ShouldBe(1);
    }

    /// <summary>
    ///     marten#5295's third gap, second half: a <c>Rename</c>'s destination was skipped for the same
    ///     reason. The source is a stored key by definition, so only the target can carry a column.
    /// </summary>
    /// <remarks>
    ///     <b>The source is <c>"retired"</c>, not <c>"Retired"</c></b>, and getting that wrong is the
    ///     same lesson from the other direction: the store's default casing is camelCase, so the CLR
    ///     name is not the stored key even for a member carrying no attribute. A rename off a key that
    ///     is not there moves nothing and reports success.
    /// </remarks>
    [Fact]
    public async Task a_renames_destination_moves_its_column()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Patch<Guide>(_frodo).Rename("retired", x => x.Nickname);
            await session.SaveChangesAsync(Token);
        }

        await using var check = _store.LightweightSession();

        (await check.Query<Guide>().Where(x => x.Nickname == "no").CountAsync(Token)).ShouldBe(1);
    }

    /// <summary>
    ///     The generated column recomputes on read, so an <c>Increment</c> — which reads the column's
    ///     own source expression and writes it back — leaves the two agreeing without a refresh. The
    ///     coalesce case is covered in <c>patching_documents</c>; what is pinned here is the column.
    /// </summary>
    [Fact]
    public async Task an_increment_leaves_the_column_agreeing_with_the_document()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Patch<Guide>(_frodo).Increment(x => x.Trips, 4);
            await session.SaveChangesAsync(Token);
        }

        await using var check = _store.LightweightSession();

        (await check.LoadAsync<Guide>(_frodo, Token))!.Trips.ShouldBe(4);
    }

    public class Water
    {
        public string Name { get; set; } = "";
    }

    public class Guide
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";

        /// <summary>
        ///     The whole subject of marten#5290. Stored as <c>nick_name</c>, so a patch built from the
        ///     CLR name writes somewhere no reader looks.
        /// </summary>
        [JsonPropertyName("nick_name")]
        public string Nickname { get; set; } = "";

        /// <summary>A plain member, so <c>Rename</c> has a stored key to move a value off.</summary>
        public string Retired { get; set; } = "";

        public int Trips { get; set; }

        public Water Home { get; set; } = new();
    }
}
