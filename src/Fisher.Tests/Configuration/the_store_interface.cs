using System.Reflection;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Fisher.Tests.Configuration;

/// <summary>
///     <see cref="IDocumentStore" /> — fisher#45.
/// </summary>
/// <remarks>
///     Extraction rather than a feature, so the assertions are about the two things extraction can get
///     wrong: the interface drifting behind the class it was taken from, and the disposal shape that
///     fisher#20 established has to be both synchronous and asynchronous.
/// </remarks>
public class the_store_interface : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("store-interface");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    private ServiceProvider ProviderFor()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFisher(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
        });

        return services.BuildServiceProvider();
    }

    [Fact]
    public void the_interface_and_the_concrete_type_resolve_to_one_store()
    {
        using var provider = ProviderFor();

        provider.GetRequiredService<IDocumentStore>()
            .ShouldBeSameAs(provider.GetRequiredService<DocumentStore>());
    }

    /// <summary>
    ///     The fisher#20 regression, one level up. A <c>ServiceProvider</c> disposed synchronously
    ///     refuses outright to dispose a service offering only <see cref="IAsyncDisposable" /> — it
    ///     throws "type only implements IAsyncDisposable" rather than falling back — so an interface
    ///     declaring only the async form would have reintroduced the bug through the registration.
    /// </summary>
    [Fact]
    public void a_synchronously_disposed_container_can_dispose_the_store()
    {
        var provider = ProviderFor();
        provider.GetRequiredService<IDocumentStore>().ShouldNotBeNull();

        Should.NotThrow(() => provider.Dispose());
    }

    [Fact]
    public async Task an_asynchronously_disposed_container_can_dispose_the_store()
    {
        var provider = ProviderFor();
        provider.GetRequiredService<IDocumentStore>().ShouldNotBeNull();

        await Should.NotThrowAsync(async () => await provider.DisposeAsync());
    }

    /// <summary>
    ///     The interface is the store's own API, so a public member added to
    ///     <see cref="DocumentStore" /> without being added here is the drift this test exists to catch.
    /// </summary>
    /// <remarks>
    ///     Only <em>implicit</em> members count. <c>DocumentStore</c> implements
    ///     <see cref="IEventStore" />, <c>IEventStore&lt;,&gt;</c> and <c>ISubscriptionRunner&lt;&gt;</c>
    ///     explicitly and deliberately, so that a tooling-only surface does not crowd the store's API —
    ///     and an explicit implementation is a private member, which is exactly why
    ///     <see cref="BindingFlags.Public" /> is the right filter and not merely a convenient one.
    /// </remarks>
    [Fact]
    public void every_public_instance_member_of_the_store_is_on_the_interface()
    {
        var onTheInterface = typeof(IDocumentStore)
            .GetMembers()
            .Concat(typeof(IDisposable).GetMembers())
            .Concat(typeof(IAsyncDisposable).GetMembers())
            .Select(x => x.Name)
            .ToHashSet();

        var missing = typeof(DocumentStore)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(x => x.MemberType is not MemberTypes.Constructor)
            // Property accessors are reported as methods of their own; the property carries the name.
            .Where(x => x is not MethodInfo { IsSpecialName: true })
            .Select(x => x.Name)
            .Distinct()
            .Where(name => !onTheInterface.Contains(name))
            .ToArray();

        missing.ShouldBeEmpty(
            $"These public members of DocumentStore are not on IDocumentStore: {string.Join(", ", missing)}. "
            + "Add them to the interface, or make them explicit implementations if they are tooling-only.");
    }

    /// <summary>
    ///     The other direction: the interface must not promise something the store does not implement
    ///     implicitly. A member satisfied by an explicit implementation would compile and then be
    ///     unreachable from the concrete type, which is the shape this store deliberately avoids.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Scoped to what <see cref="IDocumentStore" /> <em>promises</em>, which is what the rule was
    ///         always about — Fisher's own store API. The members it inherits from
    ///         <see cref="IDocumentSessionFactory{TOperations,TQuerySession}" /> (fisher#68) are satisfied
    ///         by default interface implementations on <c>IDocumentStore</c> itself rather than by
    ///         <c>DocumentStore</c>, deliberately: neither of Fisher's session factories is genuinely
    ///         parameterless, and forwarding once on the interface beats putting the same three
    ///         one-liners on the store and again on <c>SecondaryStoreProxy</c>.
    ///     </para>
    ///     <para>
    ///         A DIM that explicitly implements a base interface member is a <b>private</b> method on the
    ///         interface, so <c>IsPublic</c> on the <em>interface</em> method is what separates a promise
    ///         from a forwarder — the same reasoning that makes <see cref="BindingFlags.Public" /> the
    ///         right filter in the test above, applied one level up. Filtering on <c>DeclaringType</c>
    ///         does not work: those forwarders are declared on <c>IDocumentStore</c> too. That the
    ///         forwarders are nonetheless reachable is
    ///         <see cref="the_store_is_the_shared_document_session_factory" />'s job.
    ///     </para>
    /// </remarks>
    [Fact]
    public void the_store_implements_every_interface_member_implicitly()
    {
        var map = typeof(DocumentStore).GetInterfaceMap(typeof(IDocumentStore));

        for (var i = 0; i < map.InterfaceMethods.Length; i++)
        {
            if (!map.InterfaceMethods[i].IsPublic)
            {
                continue;
            }

            map.TargetMethods[i].IsPublic.ShouldBeTrue(
                $"{map.InterfaceMethods[i].Name} is implemented explicitly, so it is unreachable from DocumentStore.");
        }
    }

    /// <summary>
    ///     The tooling surfaces stay off the store's own API. Asserted rather than assumed, because
    ///     "add it to IDocumentStore too" is the natural-looking fix for a cast somebody finds awkward.
    /// </summary>
    /// <remarks>
    ///     <see cref="IDocumentSessionFactory{TOperations,TQuerySession}" /> is the one shared contract
    ///     that <em>is</em> on the interface (fisher#68 / jasperfx#647), and the distinction it draws is
    ///     the point rather than an exception to the rule. The tooling interfaces describe a monitoring
    ///     console's view of the store; the session factory describes opening a session, which is
    ///     already the store's own API — and putting it here is what makes an <b>ancillary</b> store
    ///     resolvable without a second mechanism, since <c>AddFisherStore&lt;T&gt;</c> constrains its
    ///     marker to <see cref="IDocumentStore" />. Marten and Polecat both declare it in this exact
    ///     position, which is what keeps a store-agnostic consumer portable across the three.
    /// </remarks>
    [Fact]
    public void the_tooling_interfaces_are_not_re_exposed()
    {
        typeof(IDocumentStore).IsAssignableTo(typeof(IEventStore)).ShouldBeFalse();

        typeof(IDocumentStore).GetInterfaces()
            .ShouldBe([
                typeof(IDisposable), typeof(IAsyncDisposable),
                typeof(IDocumentSessionFactory<IDocumentSession, IQuerySession>), typeof(IDocumentSessionFactory)
            ], ignoreOrder: true);
    }

    /// <summary>
    ///     The store really answers the shared document session factory, through both the generic form
    ///     and the non-generic one a store-agnostic consumer holds (fisher#68).
    /// </summary>
    /// <remarks>
    ///     The default interface implementations that satisfy this are excluded from
    ///     <see cref="the_store_implements_every_interface_member_implicitly" />, so without this test
    ///     they would be pinned by nothing — a forwarder wired to the wrong overload, or to a session
    ///     that is never disposed, would compile and go unnoticed. Both forms are exercised because they
    ///     are separate forwarders that differ only by return type, which is exactly the pair a
    ///     copy-paste gets wrong.
    /// </remarks>
    [Fact]
    public async Task the_store_is_the_shared_document_session_factory()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        IDocumentSessionFactory factory = store;

        await using (var session = factory.LightweightSession())
        {
            session.ShouldBeOfType<Fisher.Internal.FisherSession>();
        }

        await using (var query = factory.QuerySession())
        {
            query.ShouldBeOfType<Fisher.Internal.FisherSession>();
        }

        // The generic form hands back Fisher's own session types rather than the shared contracts.
        IDocumentSessionFactory<IDocumentSession, IQuerySession> typed = store;

        await using (IDocumentSession session = typed.LightweightSession())
        {
            session.TenantId.ShouldBe(JasperFx.StorageConstants.DefaultTenantId);
        }

        await using (IQuerySession query = typed.QuerySession())
        {
            query.TenantId.ShouldBe(JasperFx.StorageConstants.DefaultTenantId);
        }
    }

    /// <summary>
    ///     A store built by hand — no container — is the same object through either type. Worth pinning
    ///     because <c>DocumentStore.For</c> deliberately keeps returning the concrete type.
    /// </summary>
    [Fact]
    public void a_hand_built_store_is_usable_through_the_interface()
    {
        using IDocumentStore store = DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
        });

        store.Options.ShouldNotBeNull();
        store.Database.ShouldNotBeNull();
        store.Advanced.ShouldNotBeNull();

        using var session = store.LightweightSession();
        session.ShouldNotBeNull();
    }
}
