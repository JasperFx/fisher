using System.Reflection;
using JasperFx;
using JasperFx.Events;
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
    [Fact]
    public void the_store_implements_every_interface_member_implicitly()
    {
        var map = typeof(DocumentStore).GetInterfaceMap(typeof(IDocumentStore));

        for (var i = 0; i < map.InterfaceMethods.Length; i++)
        {
            map.TargetMethods[i].IsPublic.ShouldBeTrue(
                $"{map.InterfaceMethods[i].Name} is implemented explicitly, so it is unreachable from DocumentStore.");
        }
    }

    /// <summary>
    ///     The tooling surfaces stay off the store's own API. Asserted rather than assumed, because
    ///     "add it to IDocumentStore too" is the natural-looking fix for a cast somebody finds awkward.
    /// </summary>
    [Fact]
    public void the_tooling_interfaces_are_not_re_exposed()
    {
        typeof(IDocumentStore).IsAssignableTo(typeof(IEventStore)).ShouldBeFalse();

        typeof(IDocumentStore).GetInterfaces()
            .ShouldBe([typeof(IDisposable), typeof(IAsyncDisposable)], ignoreOrder: true);
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
