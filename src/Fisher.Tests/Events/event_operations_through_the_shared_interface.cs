using System.Reflection;
using Fisher.Events;
using JasperFx;
using JasperFx.Events;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#379. A downstream code generator (Wolverine's <c>UpdatedAggregate</c>) needs a
///     <see cref="MethodInfo" /> for <c>FetchLatest&lt;T&gt;</c> it can close with
///     <c>MakeGenericMethod</c> under Native AOT, which works on an interface method and not reliably
///     on a class method. Fisher's interface for that is the shared
///     <see cref="IEventStoreOperations" />, the one Marten's fix resolves from too, and
///     <see cref="EventOperations" /> has always implemented it. These pin the two things a caller
///     resolving through it depends on.
/// </summary>
public class event_operations_through_the_shared_interface : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("event-ops-interface");
    private DocumentStore _store = null!;
    private readonly Guid _streamId = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = _store.LightweightSession();
        session.Events.StartStream<QuestParty>(_streamId,
            new QuestStarted("Find the ring"), new MemberJoined("Frodo"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Theory]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(string))]
    public void fetch_latest_is_implemented_by_a_public_member_of_the_class(Type idType)
    {
        // A default interface implementation would also satisfy the interface and compile, and it
        // would be what an interface MethodInfo dispatches to. That is the non-covariance trap the
        // session's Events and PendingStreams already record. The class's own public FetchLatest
        // has to be the target.
        var interfaceMethod = OpenFetchLatest(idType);
        var map = typeof(EventOperations).GetInterfaceMap(typeof(IEventStoreOperations));
        var target = map.TargetMethods[Array.IndexOf(map.InterfaceMethods, interfaceMethod)];

        target.DeclaringType.ShouldBe(typeof(EventOperations));
        target.IsPublic.ShouldBeTrue();
        target.Name.ShouldBe(nameof(IEventStoreOperations.FetchLatest));
    }

    [Fact]
    public async Task a_method_closed_from_the_interface_fetches_the_aggregate()
    {
        // What the downstream frame does: resolve the open definition on the interface, close it over
        // the aggregate, and call it against the session's event operations.
        var closed = OpenFetchLatest(typeof(Guid)).MakeGenericMethod(typeof(QuestParty));

        await using var session = _store.LightweightSession();
        var pending = (ValueTask<QuestParty?>)closed.Invoke(session.Events,
            [_streamId, TestContext.Current.CancellationToken])!;

        var party = await pending;
        party.ShouldNotBeNull();
        party.Members.ShouldBe(["Frodo"]);
    }

    private static MethodInfo OpenFetchLatest(Type idType)
        => typeof(IEventStoreOperations).GetMethods().Single(x =>
            x.Name == nameof(IEventStoreOperations.FetchLatest)
            && x.IsGenericMethodDefinition
            && x.GetGenericArguments().Length == 1
            && x.GetParameters()[0].ParameterType == idType);
}
