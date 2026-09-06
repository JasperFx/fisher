using System.Buffers;
using System.Data.Common;
using System.Text.Json;
using Fisher.Serialization;
using Weasel.Core;
using Weasel.Storage;
using ISerializer = Fisher.Serialization.ISerializer;

namespace Fisher.Tests.Configuration;

/// <summary>
///     weasel#555 — the STJ serializer and the storage-serializer adapter are the shared ones now,
///     and Fisher's copies are gone.
/// </summary>
/// <remarks>
///     <para>
///         Fisher's <see cref="Serializer" /> and Polecat's were byte-identical: the same options
///         plumbing, the same <c>ToJson</c>/<c>FromJson</c> matrix, and — the part worth naming — the
///         same <c>[UnconditionalSuppressMessage]</c> justifications on every reflection-based member.
///         All of it is <see cref="SystemTextJsonSerializer" />'s now, so the trim/AOT contract Fisher
///         documents is the one the shared base declares rather than a second copy free to drift from
///         it. What stays in Fisher is the identity — the type name <c>StoreOptions.Serializer</c>
///         defaults to, in Fisher's namespace, declaring Fisher's two interfaces.
///     </para>
///     <para>
///         These are shape assertions on purpose. Behaviour is covered everywhere a document or event
///         round-trips; what a subclass can silently lose is a member the base does not satisfy, or an
///         interface declaration that stops being satisfied by inheritance and quietly falls through
///         to an adapter.
///     </para>
/// </remarks>
public class shared_serializer
{
    [Fact]
    public void the_default_serializer_is_the_shared_stj_one()
    {
        new Serializer().ShouldBeAssignableTo<SystemTextJsonSerializer>();

        // The alias adds nothing: both interfaces are satisfied entirely by inherited members.
        typeof(Serializer).GetMembers(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly)
            .Where(x => x is not System.Reflection.ConstructorInfo)
            .ShouldBeEmpty();
    }

    /// <summary>
    ///     The store's own default resolves to it, so nothing had to be re-registered.
    /// </summary>
    [Fact]
    public void the_store_options_default_is_that_serializer()
    {
        new StoreOptions().Serializer.ShouldBeOfType<Serializer>();
    }

    /// <summary>
    ///     Fisher's <see cref="ISerializer" /> extends <c>Weasel.Core.ISerializer</c> with the two
    ///     string-based overloads, and the shared base already carries both — with the propagating
    ///     RUC/RDC annotations those two, and only those two, are meant to have.
    /// </summary>
    [Fact]
    public void the_string_overloads_come_from_the_shared_base()
    {
        typeof(Serializer).GetMethod(nameof(ISerializer.FromJson), [typeof(Type), typeof(string)])!
            .DeclaringType.ShouldBe(typeof(SystemTextJsonSerializer));

        ISerializer serializer = new Serializer();
        serializer.FromJson<Note>(serializer.ToJson(new Note("hello"))).Text.ShouldBe("hello");
    }

    /// <summary>
    ///     <see cref="StorageSerializerAdapter.For" /> hands the default serializer straight back —
    ///     it declares <see cref="IStorageSerializer" /> and satisfies it by inheritance, so there is
    ///     nothing to wrap. That is the property that keeps the adapter off the hot path.
    /// </summary>
    [Fact]
    public void the_default_serializer_is_never_wrapped_by_the_shared_adapter()
    {
        var serializer = new Serializer();

        StorageSerializerAdapter.For(serializer).ShouldBeSameAs(serializer);
    }

    /// <summary>
    ///     A user-supplied serializer that implements only Fisher's interface is wrapped by the
    ///     <em>shared</em> adapter, which derives the seam-only members from the base ones.
    /// </summary>
    [Fact]
    public void a_user_serializer_is_wrapped_by_the_shared_adapter()
    {
        var wrapped = StorageSerializerAdapter.For(new PlainSerializer());

        wrapped.ShouldBeOfType<StorageSerializerAdapter>();

        // Derived from ToJson, which is what the adapter exists to do.
        wrapped.ToCleanJson(new Note("x")).ShouldBe("""{"Text":"x"}""");
        wrapped.ToJson(null).ShouldBe("null");
    }

    /// <summary>
    ///     The options knobs still work through the subclass — <see cref="EnumStorage" /> and
    ///     <see cref="Casing" /> are the two a store configures, and both live on the base now.
    /// </summary>
    [Fact]
    public void the_configuration_knobs_still_reach_the_options()
    {
        var serializer = new Serializer { EnumStorage = EnumStorage.AsString, Casing = Casing.SnakeCase };

        serializer.ToJson(new Ranked("Frodo", Grade.Pass))
            .ShouldBe("""{"hobbit_name":"Frodo","grade":"pass"}""");
    }

    public record Note(string Text);

    public record Ranked(string HobbitName, Grade Grade);

    public enum Grade
    {
        Pass,
        HighDistinction
    }

    /// <summary>
    ///     A serializer implementing Fisher's interface and nothing more — the case the adapter covers.
    /// </summary>
    private sealed class PlainSerializer : ISerializer
    {
        private static readonly JsonSerializerOptions Options = new();

        public EnumStorage EnumStorage { get; set; } = EnumStorage.AsInteger;
        public Casing Casing { get; set; } = Casing.Default;
        public CollectionStorage CollectionStorage { get; set; } = CollectionStorage.Default;
        public NonPublicMembersStorage NonPublicMembersStorage { get; set; } = NonPublicMembersStorage.Default;

        public string ToJson(object? document)
            => JsonSerializer.Serialize(document, document?.GetType() ?? typeof(object), Options);

        public T FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

        public object FromJson(Type type, string json) => JsonSerializer.Deserialize(json, type, Options)!;

        public T FromJson<T>(Stream stream) => JsonSerializer.Deserialize<T>(stream, Options)!;

        public object FromJson(Type type, Stream stream) => JsonSerializer.Deserialize(stream, type, Options)!;

        public T FromJson<T>(DbDataReader reader, int index) => FromJson<T>(reader.GetString(index));

        public object FromJson(Type type, DbDataReader reader, int index)
            => FromJson(type, reader.GetString(index));

        public ValueTask<T> FromJsonAsync<T>(Stream stream, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(FromJson<T>(stream));

        public ValueTask<object> FromJsonAsync(Type type, Stream stream,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(FromJson(type, stream));
    }
}
