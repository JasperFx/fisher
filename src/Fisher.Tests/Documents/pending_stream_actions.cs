using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Documents;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Documents;

/// <summary>
///     fisher#96 / jasperfx#673 — <see cref="IDocumentSessionOperations.PendingStreams" />, the stream
///     actions a session has queued and not yet committed.
/// </summary>
/// <remarks>
///     <para>
///         <c>PendingStreamActionsCompliance</c> is the definition and covers the shape of the answer:
///         empty when nothing is enlisted, one action per stream, events in order, cleared by a commit,
///         and <c>Start</c> told apart from <c>Append</c>. What it cannot see is the two decisions
///         Fisher's forward makes, which is what this file is for — a suite written against three
///         stores has no vocabulary for either.
///     </para>
///     <para>
///         Both are about the difference between <em>this session's</em> events and <em>this unit of
///         work's</em>. Fisher answers the second question, because that is the one the accessor exists
///         to ask: a pre-commit hook told what is about to be written and then handed only part of it
///         is wrong about the commit it is bracketing.
///     </para>
/// </remarks>
public class pending_stream_actions : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("pending-streams");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    /// <remarks>
    ///     <para>
    ///         The scopes' streams commit in the parent's transaction — that is the whole of fisher#33 —
    ///         so a caller asking the parent what it is about to write has to be told about them.
    ///         <c>ChangeSet</c> already reports the two together for the same reason, and this is the
    ///         same question asked one interface over.
    ///     </para>
    ///     <para>
    ///         Deliberately the same stream id in both tenants, which under conjoined tenancy is two
    ///         streams: a forward that merged them, or that reported the parent's alone, is visibly
    ///         wrong here rather than plausibly right.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_tenant_scopes_streams_are_pending_on_the_session_that_will_commit_them()
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession("north");
        session.Events.StartStream(streamId, new QuestStarted("North"));
        session.ForTenant("south").Events.StartStream(streamId, new QuestStarted("South"));

        var pending = ((IDocumentSessionOperations)session).PendingStreams;

        pending.Count.ShouldBe(2);
        pending.Select(x => x.TenantId).OrderBy(x => x).ShouldBe(["north", "south"]);

        // Every one of them is written by this call, which is the claim the collection is making.
        await session.SaveChangesAsync(Token);

        ((IDocumentSessionOperations)session).PendingStreams.ShouldBeEmpty();
    }

    /// <remarks>
    ///     A scope holds no scopes of its own — they are flattened onto the session that commits — so
    ///     reading the collection from one reports that tenant alone. The pair with the fact above:
    ///     between them they say the streams are reported once, at the level that commits them.
    /// </remarks>
    [Fact]
    public void a_tenant_scope_reports_only_its_own()
    {
        using var session = _store.LightweightSession("north");
        session.Events.StartStream(Guid.NewGuid(), new QuestStarted("North"));

        var scope = session.ForTenant("south");
        scope.Events.StartStream(Guid.NewGuid(), new QuestStarted("South"));

        var pending = ((IDocumentSessionOperations)scope).PendingStreams;

        pending.ShouldHaveSingleItem().TenantId.ShouldBe("south");
    }

    /// <remarks>
    ///     <b><c>EventOperations.PendingStreams</c> is a live view of the tracking dictionary; this is
    ///     not.</b> The contract permits either and tells a caller wanting stability to copy, so copying
    ///     here is what makes every caller's answer stable rather than the ones that read the remark.
    ///     The shape that matters is a hook holding the collection while something appends — Fisher's
    ///     own <c>BeforeSaveChangesAsync</c> listeners can do exactly that, since a listener is allowed
    ///     to extend the unit of work it is bracketing.
    /// </remarks>
    [Fact]
    public void the_collection_is_a_snapshot_rather_than_a_live_view()
    {
        using var session = _store.LightweightSession("north");
        session.Events.StartStream(Guid.NewGuid(), new QuestStarted("First"));

        var taken = ((IDocumentSessionOperations)session).PendingStreams;

        session.Events.StartStream(Guid.NewGuid(), new QuestStarted("Second"));

        taken.ShouldHaveSingleItem();
        ((IDocumentSessionOperations)session).PendingStreams.Count.ShouldBe(2);
    }

    /// <remarks>
    ///     The non-covariance trap, pinned rather than described. Fisher has a member <em>named</em>
    ///     <c>PendingStreams</c> on <c>EventOperations</c> with a different collection type, so the
    ///     interesting question is not whether the values agree but whether the contract member is
    ///     implemented at all — an unimplemented one throws from the shared default, with a clean build
    ///     either way.
    /// </remarks>
    [Fact]
    public void the_contract_member_and_the_native_spelling_agree()
    {
        var streamId = Guid.NewGuid();

        using var session = _store.LightweightSession("north");
        session.Events.StartStream(streamId, new QuestStarted("North"));

        var native = session.Events.PendingStreams.ShouldHaveSingleItem();
        var contract = ((IDocumentSessionOperations)session).PendingStreams.ShouldHaveSingleItem();

        contract.ShouldBeSameAs(native);
        contract.Id.ShouldBe(streamId);
        contract.ActionType.ShouldBe(StreamActionType.Start);
    }
}
